using System;
using Godot;

/// <summary>
/// Battle prototype — complete turn-based battle loop with character animations.
///   Enemy attacks → Player defends → Battle menu → Player attacks → repeat.
///
/// Animation flow per turn:
///   1. Setup   — attacker hops to close stance, camera zooms in (ease-out, lunge feel).
///   2. Prompt  — per-pass slams driven by PassEvaluated (works for all prompt types).
///   3. Teardown — attacker hops back to origin, camera zooms out (ease-in, snapping away).
///
/// Core principle: input and damage are always simultaneous — the button press IS the strike.
///
/// Damage model:
///   Enemy attack — Miss per pass  → player takes 10 (unblocked strike)
///   Enemy attack — Hit or Perfect → 0 damage to player (strike blocked)
///   Perfect parry (all passes)    → enemy takes 20 (automatic counter)
///   Player attack — Perfect       → enemy takes 13
///   Player attack — Hit           → enemy takes 10
///   Player attack — Miss          → enemy takes 5, attack ends
/// </summary>
public partial class BattleTest : Node2D
{
    // =========================================================================
    // State machine
    // =========================================================================

    private enum BattleState { EnemyAttack, PlayerMenu, PlayerAttack, GameOver }
    private BattleState _state = BattleState.EnemyAttack;

    // =========================================================================
    // HP
    // =========================================================================

    private const int PlayerMaxHP = 100;
    private const int EnemyMaxHP  = 100;

    private int _playerHP = PlayerMaxHP;
    private int _enemyHP  = EnemyMaxHP;

    private ColorRect _playerHPFill;
    private ColorRect _enemyHPFill;

    // =========================================================================
    // Perfect parry
    // =========================================================================

    // Set true at the start of each enemy attack, cleared to false on any Miss result.
    // Checked when PromptCompleted fires to decide whether to trigger the auto counter.
    private bool _parryClean;

    // =========================================================================
    // Battle menu
    // =========================================================================

    private static readonly string[] MenuOptionLabels  = { "Attack", "Absorbed Moves" };
    private static readonly bool[]   MenuOptionEnabled = { true,     true             };

    // Absorbed Moves submenu — populated with the player's learned moves plus a Back option.
    private static readonly string[] SubMenuOptionLabels  = { "Combo Strike", "Back" };
    private static readonly bool[]   SubMenuOptionEnabled = { true,           true   };

    private static readonly Color ColorMenuSelected = new Color(1.00f, 0.90f, 0.20f, 1.00f);  // yellow
    private static readonly Color ColorMenuNormal   = new Color(1.00f, 1.00f, 1.00f, 1.00f);  // white
    private static readonly Color ColorMenuDisabled = new Color(0.45f, 0.45f, 0.45f, 1.00f);  // grey

    private bool            _inSubMenu;       // true while the Absorbed Moves submenu is open
    private int             _menuIndex;
    private int             _subMenuIndex;
    private CanvasLayer     _menuLayer;
    private PanelContainer  _mainMenuPanel;
    private PanelContainer  _subMenuPanel;
    private Label[]         _menuLabels;
    private Label[]         _subMenuLabels;

    // =========================================================================
    // Damage numbers
    // =========================================================================

    // Colors match the input result that caused the damage.
    private static readonly Color DmgColorPerfect = new Color(0.30f, 1.00f, 0.40f, 1.00f);  // green
    private static readonly Color DmgColorHit     = new Color(1.00f, 1.00f, 1.00f, 1.00f);  // white
    private static readonly Color DmgColorMiss    = new Color(0.60f, 0.60f, 0.60f, 1.00f);  // grey (weak hit)
    private static readonly Color DmgColorPlayer  = new Color(1.00f, 0.25f, 0.25f, 1.00f);  // red

    // Spawn points — centered on each sprite, just above its top edge.
    // PlayerSprite: offset_left=390, offset_right=490 → center X=440,  top Y=590
    // EnemySprite:  offset_left=1420, offset_right=1540 → center X=1480, top Y=550
    private static readonly Vector2 PlayerDamageOrigin = new Vector2(440f,  570f);
    private static readonly Vector2 EnemyDamageOrigin  = new Vector2(1480f, 530f);

    // =========================================================================
    // Prompt management
    // =========================================================================

    // When true, the enemy does not hop in to close stance before attacking.
    // Use for large/stationary enemies that hold their ground (e.g. 8 Sword Warrior).
    // Slam tweens are also skipped — the cross-screen lunge would look wrong for a non-hopping attacker.
    [Export] public bool  SkipHopIn         = true;
    // Tuned Y offsets — finalized visually, no longer need inspector exposure.
    // Positive values move down; negative values move up.
    private const float EnemySpriteOffsetY = 130f;
    private const float EffectOffsetY      =  14f;

    private TimingPrompt     _activePrompt;
    private PackedScene      _timingPromptScene;
    private BattleSystem     _battleSystem;
    private AnimatedSprite2D _enemyAnimSprite;
    private AnimatedSprite2D _playerAnimSprite;


    // =========================================================================
    // Characters and animations
    // =========================================================================

    private ColorRect _playerSprite;
    private ColorRect _enemySprite;
    private Vector2   _playerOrigin;         // ColorRect position at scene load — positioning math anchor
    private Vector2   _enemyOrigin;
    private Vector2   _playerAnimSpriteOrigin;  // AnimatedSprite2D position after floor-anchoring in _Ready

    // Set at the start of each attack turn; used by the shared animation helpers.
    private ColorRect _attacker;
    private ColorRect _defender;
    private Vector2   _attackerClosePos;   // close-but-not-touching stance position for this turn
    private bool      _pendingGameOver;    // cached result of CheckGameOver(); read by OnFinalSlashFinished
    private bool      _playerDead;         // true once player death animation begins; guards all subsequent sprite calls
    private bool      _enemyDead;          // true once enemy death animation begins; guards all subsequent sprite calls
    private bool      _isComboAttack;     // true when the current player turn uses Combo Strike (Bouncing prompt)
    private int       _comboPassIndex;    // which Bouncing pass just resolved; set in OnAttackPassEvaluated

    // Camera — created in _Ready; controls zoom and pan during combat close-ups.
    private Camera2D  _camera;
    private static readonly Vector2 CameraDefaultPos  = new Vector2(960f, 540f);
    private static readonly Vector2 CameraDefaultZoom = Vector2.One;
    private static readonly Vector2 CameraZoomIn      = new Vector2(1.8f, 1.8f);

    // Animation durations (seconds).
    private const float SetupDuration    = 0.35f;  // hop in + zoom in
    private const float TeardownDuration = 0.35f;  // hop out + zoom out
    private const float SlamInDuration   = 0.08f;  // lunge onto defender
    private const float SlamOutDuration  = 0.12f;  // pull back to close stance

    // Spacing constants (pixels).
    private const float FloorY      = 750f;  // world-space Y of the ground line; characters and effects anchor their feet here
    private const float AttackGap   = 200f;  // gap between attacker and defender in close stance; sized so prompt circle (r=120, center X=960) has ~30-40px breathing room from nearest sprite edge
    private const float SlamOverlap = 20f;   // how far attacker overlaps defender on a slam

    // =========================================================================
    // Lifecycle
    // =========================================================================

    public override void _Ready()
    {
        _timingPromptScene = GD.Load<PackedScene>("res://Scenes/Battle/TimingPrompt.tscn");

        _playerHPFill = GetNode<ColorRect>("PlayerHP/Fill");
        _enemyHPFill  = GetNode<ColorRect>("EnemyHP/Fill");

        // Grab character sprites and record their original positions for teardown restoration.
        _playerSprite = GetNode<ColorRect>("PlayerSprite");
        _enemySprite  = GetNode<ColorRect>("EnemySprite");
        _playerOrigin = _playerSprite.Position;
        _enemyOrigin  = _enemySprite.Position;

        // Create a Camera2D centered at the viewport midpoint so the default view
        // matches the no-camera baseline: world (0,0)–(1920,1080) fully visible.
        _camera = new Camera2D();
        _camera.Name     = "BattleCamera";
        _camera.Position = CameraDefaultPos;
        _camera.Zoom     = CameraDefaultZoom;
        AddChild(_camera);

        // Player animated sprite — SpriteFrames built from separate per-animation PNGs.
        // BuildPlayerSpriteFrames returns the frame height (80px) for floor-anchored Y.
        // 0.5f factor: AnimatedSprite2D is center-anchored, so half the scaled height
        // offsets the center up so the bottom of the frame lands exactly on FloorY.
        _playerAnimSprite = GetNode<AnimatedSprite2D>("PlayerAnimatedSprite");
        int playerFrameH = BuildPlayerSpriteFrames();
        _playerAnimSprite.Scale             = new Vector2(3f, 3f);
        _playerAnimSprite.Position          = new Vector2(_playerAnimSprite.Position.X,
                                                          FloorY - playerFrameH * 3f * 0.5f);
        _playerAnimSpriteOrigin             = _playerAnimSprite.Position;  // snapshot for teardown restoration
        _playerAnimSprite.Play("idle");  // OWNER: _Ready — scene init, no battle state yet

        // Enemy animated sprite — SpriteFrames built programmatically from the sheet.
        // Floor-anchored positioning: center Y = FloorY - (frameHeight * scale * 0.6)
        // places the sprite so its visual base sits on the ground line.
        // EnemySpriteOffsetY is a tuned nudge on top of the base formula.
        _enemyAnimSprite = GetNode<AnimatedSprite2D>("EnemyAnimatedSprite");
        BuildEnemySpriteFrames();
        _enemyAnimSprite.Scale    = new Vector2(3f, 3f);
        _enemyAnimSprite.Position = new Vector2(_enemyAnimSprite.Position.X,
                                                FloorY - 160f * 3f * 0.6f + EnemySpriteOffsetY);
        _enemyAnimSprite.Play("idle");

        _battleSystem = new BattleSystem();
        AddChild(_battleSystem);
        _battleSystem.StepPassEvaluated += OnBattleSystemStepPassEvaluated;
        _battleSystem.SequenceCompleted += OnEnemySequenceCompleted;

        BuildMenu();
        UpdateHPBars();

        ShowMenu();
    }

    public override void _Input(InputEvent @event)
    {
        switch (_state)
        {
            case BattleState.PlayerMenu:
                HandleMenuInput(@event);
                break;

            case BattleState.EnemyAttack:
            case BattleState.PlayerAttack:
                if (@event.IsActionPressed("battle_confirm"))
                    TimingPrompt.ConfirmAll();
                break;
        }
    }

    // =========================================================================
    // Enemy attack phase
    // =========================================================================

    private void BeginEnemyAttack()
    {
        _state      = BattleState.EnemyAttack;
        _parryClean = true;
        GD.Print("[BattleTest] Enemy attacks.");

        if (SkipHopIn)
        {
            // Enemy stays at origin — set combat context without any setup animation.
            // Camera stays at its default position and zoom; no hop-in, no zoom-in.
            // _attackerClosePos = origin so PlayTeardown is a zero-distance no-op on the attacker.
            _attacker         = _enemySprite;
            _defender         = _playerSprite;
            _attackerClosePos = GetOrigin(_enemySprite);

            // Start the cast animation and kick off the sequence immediately.
            PlayEnemy("cast_intro");
            _enemyAnimSprite.AnimationFinished += OnCastIntroFinished;

            Vector2 defenderCenter = GetOrigin(_defender) + _defender.Size / 2f;
            _battleSystem.StartSequence(this, defenderCenter, ComputeCameraMidpoint(), EffectOffsetY);
        }
        else
        {
            PlayHopIn(_enemySprite, _playerSprite, () =>
            {
                Vector2 defenderCenter = GetOrigin(_defender) + _defender.Size / 2f;
                _battleSystem.StartSequence(this, defenderCenter, ComputeCameraMidpoint(), EffectOffsetY);
            });
        }
    }

    private void OnEnemyPassEvaluated(int result, int passIndex)
    {
        var r = (TimingPrompt.InputResult)result;
        GD.Print($"[BattleTest] Enemy pass {passIndex + 1} resolved: {r}.");

        if (r == TimingPrompt.InputResult.Miss)
        {
            _parryClean    = false;
            const int damage = 10;
            _playerHP        = Mathf.Max(0, _playerHP - damage);
            GD.Print($"[BattleTest] Pass miss — player takes {damage} damage. Player HP: {_playerHP}/{PlayerMaxHP}");
            SpawnDamageNumber(PlayerDamageOrigin, damage, DmgColorPlayer);
            UpdateHPBars();
        }
    }

    /// <summary>
    /// Adapter: BattleSystem.StepPassEvaluated → slam animation + per-pass damage.
    /// Called once per inward pass across all steps in the enemy's attack sequence.
    /// Slam is skipped when SkipHopIn is true — the enemy never hopped in close,
    /// so the cross-screen lunge ComputeSlamPosition() would produce looks wrong.
    /// </summary>
    private void OnBattleSystemStepPassEvaluated(int result, int passIndex, int stepIndex)
    {
        if (!SkipHopIn)
            OnAttackPassEvaluated(result, passIndex);
        OnEnemyPassEvaluated(result, passIndex);

        // OWNER: OnBattleSystemStepPassEvaluated (enemy turn, per-pass reaction).
        // Pre-empt any in-flight retreat before taking ownership of the sprite.
        // If the backward run loop hasn't fired OnRetreatFinished yet, cancel it here so
        // it doesn't stomp the parry/hit animation or restore idle at the wrong moment.
        // SpeedScale must be reset regardless — it may still be 2 from the retreat.
        _playerAnimSprite.AnimationFinished -= OnRetreatFinished;
        _playerAnimSprite.SpeedScale = 1f;  // always reset — may still be 2 from retreat hop-back

        var r = (TimingPrompt.InputResult)result;
        if (r == TimingPrompt.InputResult.Hit || r == TimingPrompt.InputResult.Perfect)
        {
            // Successful block — deflect animation, then return to idle.
            PlayPlayer("parry");   // OWNER: enemy pass, player defends
            _playerAnimSprite.AnimationFinished += OnParryFinished;
        }
        else if (r == TimingPrompt.InputResult.Miss)
        {
            // Strike landed — flinch animation, then return to idle.
            PlayPlayer("hit");     // OWNER: enemy pass, player takes damage
            _playerAnimSprite.AnimationFinished += OnHitAnimFinished;
        }
    }

    /// <summary>
    /// BattleSystem.SequenceCompleted — all steps in the enemy's attack have resolved.
    /// Applies the perfect-parry counter if earned, then tears down the combat close-up.
    /// BattleSystem owns its prompts and frees them internally — no FreeActivePrompt call needed.
    /// </summary>
    private void OnEnemySequenceCompleted()
    {
        GD.Print("[BattleTest] Enemy attack sequence complete.");

        // Per-pass damage and _parryClean are tracked in OnEnemyPassEvaluated.
        // Only the parry counter fires here, after all passes have been evaluated.
        if (_parryClean)
        {
            const int CounterDamage = 20;
            _enemyHP = Mathf.Max(0, _enemyHP - CounterDamage);
            GD.Print($"[BattleTest] Perfect parry! Auto counter: {CounterDamage} damage. Enemy HP: {_enemyHP}/{EnemyMaxHP}");
            SpawnDamageNumber(EnemyDamageOrigin, CounterDamage, DmgColorPerfect);
        }

        UpdateHPBars();
        bool over = CheckGameOver();

        if (!over)
        {
            // Normal completion — enemy plays cast_end then returns to idle; menu reappears.
            // cast_end (≈0.25s) completes before the 0.5s post-teardown delay, so idle is
            // reached well before the player menu shows.
            PlayEnemy("cast_end");
            _enemyAnimSprite.AnimationFinished += OnCastEndFinished;
            PlayTeardown(() => GetTree().CreateTimer(0.5f).Timeout += ShowMenu);
            return;
        }

        // Game over — determine which side is dead and play the appropriate death animation.
        // PlayTeardown resets the camera without scheduling a next turn.
        PlayTeardown(null);

        if (_playerHP <= 0)
        {
            // Enemy's attack landed the killing blow.
            // Enemy completes the cast_end pose normally; player plays death.
            PlayEnemy("cast_end");
            _enemyAnimSprite.AnimationFinished += OnCastEndFinished;
            _playerDead = true;
            _playerAnimSprite.Play("death");            // OWNER: player death — interrupts current animation
            _playerAnimSprite.AnimationFinished += OnPlayerDeathFinished;
        }
        else // _enemyHP <= 0 — perfect parry counter killed the enemy
        {
            // Enemy plays death (interrupts the cast pose); player returns to idle.
            _enemyDead = true;
            _enemyAnimSprite.Play("death");             // OWNER: enemy death from parry counter
            _enemyAnimSprite.AnimationFinished += OnEnemyDeathFinished;
            PlayPlayer("idle");                         // OWNER: sequence over, player at rest
        }
    }

    // =========================================================================
    // Battle menu
    // =========================================================================

    private void BuildMenu()
    {
        _menuLayer      = new CanvasLayer();
        _menuLayer.Name = "BattleMenu";
        AddChild(_menuLayer);

        // Both panels share the same canvas position. Only one is visible at a time.
        static PanelContainer MakePanel(CanvasLayer layer)
        {
            var p = new PanelContainer();
            p.Position          = new Vector2(810f, 470f);
            p.CustomMinimumSize = new Vector2(300f, 0f);
            layer.AddChild(p);
            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 8);
            p.AddChild(vbox);
            return p;
        }

        // Main menu — Attack / Absorbed Moves.
        _mainMenuPanel = MakePanel(_menuLayer);
        _menuLabels    = new Label[MenuOptionLabels.Length];
        var mainVBox   = _mainMenuPanel.GetChild<VBoxContainer>(0);
        for (int i = 0; i < MenuOptionLabels.Length; i++)
        {
            var label = new Label();
            label.AddThemeFontSizeOverride("font_size", 24);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            mainVBox.AddChild(label);
            _menuLabels[i] = label;
        }

        // Absorbed Moves submenu — Combo Strike / Back.
        _subMenuPanel  = MakePanel(_menuLayer);
        _subMenuLabels = new Label[SubMenuOptionLabels.Length];
        var subVBox    = _subMenuPanel.GetChild<VBoxContainer>(0);
        for (int i = 0; i < SubMenuOptionLabels.Length; i++)
        {
            var label = new Label();
            label.AddThemeFontSizeOverride("font_size", 24);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            subVBox.AddChild(label);
            _subMenuLabels[i] = label;
        }

        _menuLayer.Visible = false;
    }

    private void ShowMenu()
    {
        _state                 = BattleState.PlayerMenu;
        _inSubMenu             = false;
        _menuIndex             = 0;
        _mainMenuPanel.Visible = true;
        _subMenuPanel.Visible  = false;
        _menuLayer.Visible     = true;
        RefreshMenuLabels();
        GD.Print("[BattleTest] Player menu shown.");
    }

    private void ShowSubMenu()
    {
        _inSubMenu             = true;
        _subMenuIndex          = 0;
        _mainMenuPanel.Visible = false;
        _subMenuPanel.Visible  = true;
        RefreshSubMenuLabels();
        GD.Print("[BattleTest] Absorbed Moves submenu shown.");
    }

    private void HideMenu()
    {
        _menuLayer.Visible = false;
    }

    private void HandleMenuInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_up"))
        {
            if (_inSubMenu) NavigateSubMenu(-1); else NavigateMenu(-1);
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed("ui_down"))
        {
            if (_inSubMenu) NavigateSubMenu(1); else NavigateMenu(1);
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed("battle_confirm"))
        {
            if (_inSubMenu) ConfirmSubMenuSelection(); else ConfirmMenuSelection();
            GetViewport().SetInputAsHandled();
        }
    }

    private void NavigateMenu(int direction)
    {
        int count = MenuOptionLabels.Length;
        int next  = _menuIndex;
        for (int i = 0; i < count; i++)
        {
            next = (next + direction + count) % count;
            if (MenuOptionEnabled[next]) { _menuIndex = next; break; }
        }
        RefreshMenuLabels();
    }

    private void NavigateSubMenu(int direction)
    {
        int count = SubMenuOptionLabels.Length;
        int next  = _subMenuIndex;
        for (int i = 0; i < count; i++)
        {
            next = (next + direction + count) % count;
            if (SubMenuOptionEnabled[next]) { _subMenuIndex = next; break; }
        }
        RefreshSubMenuLabels();
    }

    private void ConfirmMenuSelection()
    {
        if (!MenuOptionEnabled[_menuIndex]) return;
        GD.Print($"[BattleTest] Player selects: {MenuOptionLabels[_menuIndex]}.");
        switch (_menuIndex)
        {
            case 0: HideMenu(); _isComboAttack = false; BeginPlayerAttack(); break;
            case 1: ShowSubMenu(); break;  // Absorbed Moves — open submenu without hiding the layer
        }
    }

    private void ConfirmSubMenuSelection()
    {
        if (!SubMenuOptionEnabled[_subMenuIndex]) return;
        GD.Print($"[BattleTest] Player selects submenu: {SubMenuOptionLabels[_subMenuIndex]}.");
        switch (_subMenuIndex)
        {
            case 0: HideMenu(); _isComboAttack = true; BeginPlayerAttack(); break;  // Combo Strike
            case 1: ShowMenu(); break;  // Back — return to main menu
        }
    }

    private void RefreshMenuLabels()
    {
        for (int i = 0; i < _menuLabels.Length; i++)
        {
            bool selected = (i == _menuIndex);
            bool enabled  = MenuOptionEnabled[i];
            string prefix = (selected && enabled) ? "▶ " : "  ";
            _menuLabels[i].Text     = prefix + MenuOptionLabels[i];
            _menuLabels[i].Modulate = enabled
                ? (selected ? ColorMenuSelected : ColorMenuNormal)
                : ColorMenuDisabled;
        }
    }

    private void RefreshSubMenuLabels()
    {
        for (int i = 0; i < _subMenuLabels.Length; i++)
        {
            bool selected = (i == _subMenuIndex);
            bool enabled  = SubMenuOptionEnabled[i];
            string prefix = (selected && enabled) ? "▶ " : "  ";
            _subMenuLabels[i].Text     = prefix + SubMenuOptionLabels[i];
            _subMenuLabels[i].Modulate = enabled
                ? (selected ? ColorMenuSelected : ColorMenuNormal)
                : ColorMenuDisabled;
        }
    }

    // =========================================================================
    // Player attack phase
    // =========================================================================

    private void BeginPlayerAttack()
    {
        _state = BattleState.PlayerAttack;
        GD.Print(_isComboAttack ? "[BattleTest] Player uses Combo Strike." : "[BattleTest] Player attacks.");
        _comboPassIndex = 0;
        var promptType = _isComboAttack ? TimingPrompt.PromptType.Bouncing : TimingPrompt.PromptType.Standard;
        BeginAttack(_playerSprite, _enemySprite, promptType, OnPlayerPromptCompleted);
    }

    private void OnPlayerPromptCompleted(int result)
    {
        var r = (TimingPrompt.InputResult)result;
        GD.Print($"[BattleTest] Player attack resolved: {r}.");

        int damage = r switch
        {
            TimingPrompt.InputResult.Perfect => 13,
            TimingPrompt.InputResult.Hit     => 10,
            _                               => 5,   // Miss — glancing strike still lands
        };

        Color dmgColor = r switch
        {
            TimingPrompt.InputResult.Perfect => DmgColorPerfect,
            TimingPrompt.InputResult.Hit     => DmgColorHit,
            _                               => DmgColorMiss,
        };

        if (damage > 0)
        {
            _enemyHP = Mathf.Max(0, _enemyHP - damage);
            GD.Print($"[BattleTest] Player deals {damage} damage. Enemy HP: {_enemyHP}/{EnemyMaxHP}");
            SpawnDamageNumber(EnemyDamageOrigin, damage, dmgColor);
        }
        else
        {
            GD.Print("[BattleTest] Player missed — no damage.");
        }

        UpdateHPBars();
        _pendingGameOver = CheckGameOver();
        GetTree().CreateTimer(TimingPrompt.FlashDuration).Timeout += FreeActivePrompt;

        if (_isComboAttack)
        {
            // Combo: pass 2 in OnAttackPassEvaluated already plays combo_slash1 and subscribes
            // OnFinalSlashFinished — the retreat flow is already in motion, nothing to do here.
            return;
        }

        // Single attack: play the slash now that the circle has resolved.
        // combo_slash1 covers sheet frames 1–3; frame 0 was already shown as the wind-up.
        // PlayTeardown is deferred to OnFinalSlashFinished so the strike plays before retreat.
        PlayPlayer("combo_slash1");  // OWNER: player turn, single-hit slash on resolve
        _playerAnimSprite.AnimationFinished += OnFinalSlashFinished;
    }

    // =========================================================================
    // Shared helpers
    // =========================================================================

    private void FreeActivePrompt()
    {
        if (_activePrompt != null && IsInstanceValid(_activePrompt))
        {
            _activePrompt.QueueFree();
            _activePrompt = null;
        }
    }

    private bool CheckGameOver()
    {
        if (_playerHP <= 0)
        {
            GD.Print("[BattleTest] Enemy wins.");
            _state = BattleState.GameOver;
            return true;
        }
        if (_enemyHP <= 0)
        {
            GD.Print("[BattleTest] Player wins.");
            _state = BattleState.GameOver;
            return true;
        }
        return false;
    }

    private void UpdateHPBars()
    {
        const float BarWidth = 300f;
        _playerHPFill.Size = new Vector2(BarWidth * ((float)_playerHP / PlayerMaxHP), _playerHPFill.Size.Y);
        _enemyHPFill.Size  = new Vector2(BarWidth * ((float)_enemyHP  / EnemyMaxHP),  _enemyHPFill.Size.Y);
    }

    /// <summary>
    /// Spawns a floating damage number at <paramref name="position"/> that drifts upward
    /// 80px and fades to transparent over 1 second, then frees itself.
    /// </summary>
    private void SpawnDamageNumber(Vector2 position, int amount, Color color)
    {
        var label = new Label();
        label.Text                = amount.ToString();
        label.Modulate            = color;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.CustomMinimumSize   = new Vector2(80f, 0f);
        label.AddThemeFontSizeOverride("font_size", 28);

        Vector2 startPos = position - new Vector2(40f, 0f);
        Vector2 endPos   = startPos  - new Vector2(0f, 80f);
        label.Position   = startPos;
        AddChild(label);

        var tween = CreateTween();
        tween.SetParallel(true);
        tween.TweenProperty(label, "position",   endPos, 1.0f);
        tween.TweenProperty(label, "modulate:a", 0.0f,   1.0f);
        tween.Finished += label.QueueFree;
    }

    // =========================================================================
    // Player sprite setup
    // =========================================================================

    /// <summary>
    /// Builds and assigns SpriteFrames for the player AnimatedSprite2D.
    /// Each animation is a separate horizontal-strip PNG in Assets/Characters/Knight/.
    /// Frame size is derived at runtime from texture height (all knight frames are 80×80).
    /// Returns the frame height so the caller can position the sprite floor-anchored.
    /// </summary>
    private int BuildPlayerSpriteFrames()
    {
        const string Base  = "res://Assets/Characters/Knight/";
        const int    Fw    = 120;   // all knight frames are 120×80 (not square)
        const int    Fh    = 80;
        var frames = new SpriteFrames();
        frames.RemoveAnimation("default");

        // idle / run / hit / death — each file maps to one animation.
        //
        // All player attack animations come from _AttackComboNoMovement.png (1200×80, 10 frames):
        //   "combo"        — all 10 frames; used as the frame-index reference for wind-up holds
        //   "combo_slash1" — frames 1–3; first strike (single attack resolve, combo passes 0 & 1)
        //   "combo_slash2" — frames 6–9; second strike (combo final pass)
        //
        // _Attack2NoMovement.png (720×80, 6 frames) provides one animation:
        //   "parry" — frames 2–5; plays on every successful enemy-attack parry
        const string Combo = Base + "_AttackComboNoMovement.png";
        int frameH = AddPlayerAnimation(frames, "idle",         Base + "_Idle.png",             loop: true,  fw: Fw, fh: Fh);
                     AddPlayerAnimation(frames, "run",          Base + "_Run.png",              loop: true,  fw: Fw, fh: Fh);
                     AddPlayerAnimation(frames, "combo",        Combo,                          loop: false, fw: Fw, fh: Fh);
                     AddPlayerAnimation(frames, "combo_slash1", Combo,                          loop: false, fw: Fw, fh: Fh, startFrame: 1, endFrame: 3);
                     AddPlayerAnimation(frames, "combo_slash2", Combo,                          loop: false, fw: Fw, fh: Fh, startFrame: 6, endFrame: 9);
                     AddPlayerAnimation(frames, "parry",        Base + "_Attack2NoMovement.png", loop: false, fw: Fw, fh: Fh, startFrame: 2, endFrame: 5);
                     AddPlayerAnimation(frames, "hit",          Base + "_Hit.png",              loop: false, fw: Fw, fh: Fh);
                     AddPlayerAnimation(frames, "death",        Base + "_DeathNoMovement.png",  loop: false, fw: Fw, fh: Fh);

        _playerAnimSprite.SpriteFrames = frames;

        // Diagnostic: confirm every animation and its actual frame count.
        // A 0-frame entry means the PNG failed to load (missing .import file).
        // Calling Play() on a 0-frame animation hides the sprite — the primary blink cause.
        GD.Print("[BattleTest] Player sprite frames summary:");
        foreach (string anim in frames.GetAnimationNames())
            GD.Print($"[BattleTest]   {anim}: {frames.GetFrameCount(anim)} frame(s)");

        return frameH > 0 ? frameH : Fh;
    }

    /// <summary>
    /// Loads a horizontal-strip texture and adds it as a named animation.
    /// All knight PNGs use 120×80 frames — pass fw=120, fh=80 explicitly.
    ///
    /// <paramref name="startFrame"/> skips leading sheet frames (zero-based).
    /// <paramref name="endFrame"/> is the last sheet frame to include (inclusive).
    ///   Pass -1 (default) to include all frames from startFrame to the end of the strip.
    ///
    /// Examples (all from _AttackComboNoMovement.png, 1200×80 = 10 frames):
    ///   "combo"        startFrame=0  endFrame=-1 → frames 0–9  (all 10)
    ///   "combo_slash1" startFrame=1  endFrame=3  → frames 1–3  (3 frames, first strike)
    ///   "combo_slash2" startFrame=6  endFrame=9  → frames 6–9  (4 frames, second strike)
    ///   "parry"        startFrame=2  endFrame=5  → frames 2–5  (4 frames, defensive deflect)
    ///
    /// Returns the frame height for floor-anchored Y positioning; returns 0 on load failure.
    /// </summary>
    private static int AddPlayerAnimation(
        SpriteFrames frames, string name, string path,
        bool loop, int fw, int fh, float fps = 12f, int startFrame = 0, int endFrame = -1)
    {
        var texture = GD.Load<Texture2D>(path);
        if (texture == null)
        {
            // Most likely cause: PNG has no .import file yet.
            // Open the Godot editor once to trigger auto-import, then re-run.
            GD.PrintErr($"[BattleTest] LOAD FAILED — '{name}' will have 0 frames: {path}");
            frames.AddAnimation(name);  // stub so Play() won't throw; sprite will hide until fixed
            return 0;
        }

        int stripCount = texture.GetWidth() / fw;
        int last       = (endFrame < 0) ? stripCount - 1 : endFrame;
        int used       = last - startFrame + 1;

        GD.Print($"[BattleTest]   '{name}': {texture.GetWidth()}x{texture.GetHeight()}  " +
                 $"fw={fw} fh={fh}  strip={stripCount}  frames={startFrame}–{last}  used={used}");

        frames.AddAnimation(name);
        frames.SetAnimationSpeed(name, fps);
        frames.SetAnimationLoop(name, loop);

        for (int i = startFrame; i <= last; i++)
        {
            var atlas    = new AtlasTexture();
            atlas.Atlas  = texture;
            atlas.Region = new Rect2(i * fw, 0, fw, fh);
            frames.AddFrame(name, atlas);
        }

        return fh;
    }

    // =========================================================================
    // Enemy sprite setup
    // =========================================================================

    /// <summary>
    /// Builds and assigns SpriteFrames for the enemy AnimatedSprite2D by slicing
    /// the warrior sprite sheet into named animations. Mirrors the runtime AtlasTexture
    /// construction used by BattleSystem.SpawnEffectSprite for one-shot effects.
    ///
    /// Sheet: 8_sword_warrior_red-Sheet.png — 160×160 per frame, 21 cols × 7 rows.
    /// Row mapping: idle=0, run=1, attack=2, cast_full=3, cast_loop=4, death=6.
    /// </summary>
    private void BuildEnemySpriteFrames()
    {
        const string SheetPath = "res://Assets/Enemies/8_Sword_Warrior/8_Sword_Warrior_Red/8_sword_warrior_red-Sheet.png";
        var texture = GD.Load<Texture2D>(SheetPath);
        if (texture == null)
        {
            GD.PrintErr($"[BattleTest] Could not load enemy sprite sheet: {SheetPath}");
            return;
        }

        const int Fw = 160, Fh = 160;
        var frames = new SpriteFrames();
        frames.RemoveAnimation("default");  // SpriteFrames always starts with a "default" stub

        AddEnemyAnimation(frames, texture, "idle",       row: 0, count: 14, fw: Fw, fh: Fh, fps: 12f, loop: true);
        AddEnemyAnimation(frames, texture, "run",        row: 1, count:  8, fw: Fw, fh: Fh, fps: 12f, loop: true);
        AddEnemyAnimation(frames, texture, "attack",     row: 2, count: 15, fw: Fw, fh: Fh, fps: 12f, loop: false);
        // cast_full row (row 3) is split into three phases:
        //   cast_intro — frames 0–3  (wind-up, plays once before the prompt appears)
        //   cast_loop  — row 4       (hold pose, loops for the duration of the prompt sequence)
        //   cast_end   — frames 18–20 (release, plays once after the sequence resolves)
        AddEnemyAnimation(frames, texture, "cast_intro", row: 3, count:  4, fw: Fw, fh: Fh, fps: 12f, loop: false, startCol:  0);
        AddEnemyAnimation(frames, texture, "cast_loop",  row: 4, count: 14, fw: Fw, fh: Fh, fps: 12f, loop: true);
        AddEnemyAnimation(frames, texture, "cast_end",   row: 3, count:  3, fw: Fw, fh: Fh, fps: 12f, loop: false, startCol: 18);
        AddEnemyAnimation(frames, texture, "death",      row: 6, count: 15, fw: Fw, fh: Fh, fps: 12f, loop: false);

        _enemyAnimSprite.SpriteFrames = frames;
        GD.Print("[BattleTest] Enemy sprite frames built — 8 animations loaded.");
    }

    /// <param name="startCol">First column to read from (default 0). Use for sub-ranges of a row, e.g. cast_end starts at col 18.</param>
    private static void AddEnemyAnimation(
        SpriteFrames frames, Texture2D sheet,
        string name, int row, int count, int fw, int fh, float fps, bool loop,
        int startCol = 0)
    {
        frames.AddAnimation(name);
        frames.SetAnimationSpeed(name, fps);
        frames.SetAnimationLoop(name, loop);
        for (int col = startCol; col < startCol + count; col++)
        {
            var atlas    = new AtlasTexture();
            atlas.Atlas  = sheet;
            atlas.Region = new Rect2(col * fw, row * fh, fw, fh);
            frames.AddFrame(name, atlas);
        }
    }

    // -------------------------------------------------------------------------
    // Enemy cast animation handlers
    // -------------------------------------------------------------------------
    // Sequence: cast_intro (once) → cast_loop (until sequence ends) → cast_end (once) → idle
    // Each handler disconnects itself before transitioning to prevent stacking on repeat turns.

    private void OnCastIntroFinished()
    {
        _enemyAnimSprite.AnimationFinished -= OnCastIntroFinished;
        PlayEnemy("cast_loop");
    }

    private void OnCastEndFinished()
    {
        _enemyAnimSprite.AnimationFinished -= OnCastEndFinished;
        PlayEnemy("idle");
    }

    private void OnParryFinished()
    {
        _playerAnimSprite.AnimationFinished -= OnParryFinished;
        PlayPlayer("idle");  // OWNER: enemy pass resolved, parry complete
    }

    private void OnHitAnimFinished()
    {
        _playerAnimSprite.AnimationFinished -= OnHitAnimFinished;
        PlayPlayer("idle");  // OWNER: enemy pass resolved, flinch complete
    }

    /// <summary>
    /// Fires when the player death animation completes.
    /// Does not return to idle — the sprite holds on the final death frame.
    /// Placeholder: prints to console until a game over screen is implemented.
    /// </summary>
    private void OnPlayerDeathFinished()
    {
        _playerAnimSprite.AnimationFinished -= OnPlayerDeathFinished;
        GD.Print("[BattleTest] Player died.");
        ShowEndLabel("Game Over");
    }

    /// <summary>
    /// Fires when the enemy death animation completes (from player attack or parry counter).
    /// Does not return to idle — the sprite holds on the final death frame.
    /// Placeholder: prints to console until a win screen is implemented.
    /// </summary>
    private void OnEnemyDeathFinished()
    {
        _enemyAnimSprite.AnimationFinished -= OnEnemyDeathFinished;
        GD.Print("[BattleTest] Enemy defeated.");
        GetTree().CreateTimer(1.0f).Timeout += () => ShowEndLabel("Victory!");
    }

    /// <summary>
    /// Fires when combo_slash1 (frames 1–3) finishes after pass 0.
    /// Holds on "combo" frame 5 — the second wind-up — so the sprite is posed ready
    /// for combo_slash2 while the outward bounce travels back out.
    /// Stop() is called before setting Frame to counteract Godot 4's reset-to-0 on stop.
    /// </summary>
    private void OnComboPass0SlashFinished()
    {
        _playerAnimSprite.AnimationFinished -= OnComboPass0SlashFinished;
        if (_playerDead) return;
        _playerAnimSprite.Animation = "combo";
        StopPlayer();
        SetPlayerFrame(5);  // OWNER: combo pass 0 resolved — wind-up before slash2 (sheet frame 5)
    }

    /// <summary>
    /// Fires when combo_slash2 (frames 6–9) finishes after pass 1.
    /// Holds on "combo" frame 0 — the first wind-up again — ready for combo_slash1 on the final pass.
    /// Stop() is called before setting Frame to counteract Godot 4's reset-to-0 on stop.
    /// </summary>
    private void OnComboPass1SlashFinished()
    {
        _playerAnimSprite.AnimationFinished -= OnComboPass1SlashFinished;
        if (_playerDead) return;
        _playerAnimSprite.Animation = "combo";
        StopPlayer();
        SetPlayerFrame(0);  // OWNER: combo pass 1 resolved — wind-up before slash1 again (sheet frame 0)
    }

    /// <summary>
    /// Fires when the final slash animation completes (combo_slash1 for single, combo_slash2 for combo).
    /// Holds the last frame for 0.3s so the strike reads, then starts the retreat:
    /// disables looping on "run" so PlayBackwards fires AnimationFinished at frame 0,
    /// then launches the position teardown and backwards run animation together.
    /// PlayTeardown is called here rather than in OnPlayerPromptCompleted so the slash always
    /// completes before the combatant starts moving back.
    /// </summary>
    private void OnFinalSlashFinished()
    {
        _playerAnimSprite.AnimationFinished -= OnFinalSlashFinished;
        StopPlayer();   // OWNER: OnFinalSlashFinished — hold last slash frame (sheet frame 3 or 9)
        // Godot 4 resets Frame to 0 when Stop() is called on a finished non-looping animation.
        // Re-apply the last frame index explicitly to counteract this and hold the final pose.
        SetPlayerFrame(_playerAnimSprite.SpriteFrames.GetFrameCount("combo_slash1") - 1);

        if (_pendingGameOver)
        {
            // Enemy HP reached zero from the player's attack.
            // (Player cannot die during their own attack; _pendingGameOver here always means enemy defeated.)
            // Interrupt the enemy's current animation and play death; reset camera without next turn.
            _enemyDead = true;
            _enemyAnimSprite.Play("death");         // OWNER: enemy death from player attack
            _enemyAnimSprite.AnimationFinished += OnEnemyDeathFinished;
            PlayTeardown(null);
            return;
        }

        // OWNER: OnFinalSlashFinished (player turn, retreat begins).
        // Hold the last slash frame for 0.3s so it reads before the character moves,
        // then start the retreat. PlayTeardown and PlayBackwards begin together after
        // the pause so the position tween and animation are in sync.
        GetTree().CreateTimer(0.3f).Timeout += () =>
        {
            // Disable looping on "run" for this one-shot backwards pass so that
            // AnimationFinished fires when frame 0 is reached and OnRetreatFinished triggers.
            // Looping is intentional for the forward hop-in; we restore it in OnRetreatFinished.
            _playerAnimSprite.SpriteFrames.SetAnimationLoop("run", false);
            _playerAnimSprite.SpeedScale = 2f;
            PlayPlayerBackwards("run");  // OWNER: player turn, retreat hop-back
            _playerAnimSprite.AnimationFinished += OnRetreatFinished;

            PlayTeardown(() => GetTree().CreateTimer(0.5f).Timeout += BeginEnemyAttack);
        };
    }

    /// <summary>
    /// Called when the backwards run animation reaches frame 0 (or loops) after the player hops back.
    /// Always resets SpeedScale so subsequent animations aren't affected.
    /// Only restores idle if the sprite is still on "run" — if an enemy-side handler (parry, hit)
    /// took ownership while the retreat was in flight, that animation takes priority.
    /// </summary>
    private void OnRetreatFinished()
    {
        _playerAnimSprite.AnimationFinished -= OnRetreatFinished;
        _playerAnimSprite.SpeedScale = 1f;  // always reset — SpeedScale affects all animations
        // Restore looping on "run" — it was disabled before PlayBackwards so AnimationFinished
        // would fire once at frame 0. The forward hop-in on the next player turn needs it looping.
        _playerAnimSprite.SpriteFrames.SetAnimationLoop("run", true);
        // Guard: only return to idle if the retreat run still owns the sprite.
        // OnBattleSystemStepPassEvaluated may have pre-empted this handler and started
        // parry or hit; in that case let those complete without overriding them.
        if (_playerAnimSprite.Animation == "run")
            PlayPlayer("idle");  // OWNER: OnRetreatFinished — retreat complete
    }

    // =========================================================================
    // Attack animation — setup, per-pass slam, teardown
    // =========================================================================

    /// <summary>
    /// Plays the hop-in animation: attacker lunges to close stance, camera zooms in.
    /// Sets <see cref="_attacker"/>, <see cref="_defender"/>, and
    /// <see cref="_attackerClosePos"/> before starting the tween, so
    /// <see cref="ComputeCameraMidpoint"/> and <see cref="ComputeSlamPosition"/> are
    /// usable immediately after this call returns. <paramref name="onComplete"/> fires
    /// when the tween finishes; safe to pass null.
    /// </summary>
    private void PlayHopIn(ColorRect attacker, ColorRect defender, Action onComplete)
    {
        _attacker         = attacker;
        _defender         = defender;
        _attackerClosePos = ComputeClosePosition();

        // Play run at double speed while the player hops in — snappy charge feel.
        // Guard: only call Play("run") if the animation has frames. A 0-frame animation
        // (caused by a missing .import file for _Run.png) hides the sprite entirely.
        if (attacker == _playerSprite)
        {
            // OWNER: PlayHopIn (player turn, charge begins).
            // Guard: only call Play("run") if the animation has frames — a 0-frame animation
            // (caused by a missing .import file for _Run.png) hides the sprite entirely.
            int runFrames = _playerAnimSprite.SpriteFrames?.GetFrameCount("run") ?? 0;
            if (runFrames > 0)
            {
                _playerAnimSprite.SpeedScale = 2f;
                PlayPlayer("run");   // OWNER: player turn, hop-in charge
            }
            else
            {
                GD.PrintErr("[BattleTest] 'run' has 0 frames — staying on idle during hop-in. " +
                            "Open the Godot editor to auto-import _Run.png.");
            }
        }

        var tween = CreateTween();
        tween.SetParallel(true);
        // Hop in — ease-out (fast start, decelerates on arrival = lunge).
        tween.TweenProperty(attacker, "position", _attackerClosePos, SetupDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        // When the player is the attacker, the visible AnimatedSprite2D must move with the
        // ColorRect. Compute the same X delta and apply it to the sprite's floor-anchored origin.
        if (attacker == _playerSprite)
        {
            float   hopDeltaX    = _attackerClosePos.X - _playerOrigin.X;
            Vector2 animTarget   = new Vector2(_playerAnimSpriteOrigin.X + hopDeltaX,
                                               _playerAnimSpriteOrigin.Y);
            tween.TweenProperty(_playerAnimSprite, "position", animTarget, SetupDuration)
                 .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        }
        // Camera zooms in centered between the two combatants.
        tween.TweenProperty(_camera, "position", ComputeCameraMidpoint(), SetupDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        tween.TweenProperty(_camera, "zoom", CameraZoomIn, SetupDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        if (onComplete != null)
            tween.Finished += () => onComplete();
    }

    /// <summary>
    /// Unified entry point for player attack turns.
    /// Plays the hop-in animation, then adds the prompt to the scene tree when
    /// the animation completes (so the first input is only possible after the hop-in).
    /// </summary>
    private void BeginAttack(
        ColorRect attacker,
        ColorRect defender,
        TimingPrompt.PromptType promptType,
        TimingPrompt.PromptCompletedEventHandler onComplete)
    {
        // Build the prompt node but defer AddChild until the hop-in tween finishes.
        var prompt = _timingPromptScene.Instantiate<TimingPrompt>();
        prompt.Type     = promptType;
        prompt.AutoLoop = false;
        prompt.PassEvaluated   += OnAttackPassEvaluated;
        prompt.PromptCompleted += onComplete;
        _activePrompt = prompt;

        PlayHopIn(attacker, defender, () =>
        {
            // Hop-in finished — freeze on frame 0 (wind-up pose) without playing.
            // The slash fires from frame 1 only after the timing circle resolves,
            // so the pose reads as intent-to-strike while the player waits for input.
            if (attacker == _playerSprite)
            {
                // OWNER: BeginAttack hop-in callback (player turn, awaiting input).
                // "combo" frame 0 = first wind-up pose for both single and combo attacks.
                _playerAnimSprite.SpeedScale = 1f;
                _playerAnimSprite.Animation  = "combo";
                SetPlayerFrame(0);   // OWNER: player turn, wind-up pose (sheet frame 0)
                StopPlayer();
            }
            // Position is set here so ComputeCameraMidpoint() reflects the final close stance.
            prompt.Position = ComputeCameraMidpoint();
            AddChild(prompt);
        });
    }

    /// <summary>
    /// Fires on every PassEvaluated — attacker briefly lunges to overlap the defender,
    /// then pulls back to the close stance. Works identically for all prompt types
    /// because TimingPrompt now emits PassEvaluated for Standard and Slow too.
    /// </summary>
    private void OnAttackPassEvaluated(int result, int passIndex)
    {
        // Slam tween on every pass — attacker lunges forward then snaps back to close stance.
        Vector2 slamPos = ComputeSlamPosition();
        var tween = CreateTween();
        tween.TweenProperty(_attacker, "position", slamPos, SlamInDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        tween.TweenProperty(_attacker, "position", _attackerClosePos, SlamOutDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);

        if (!_isComboAttack) return;  // single: animation driven entirely in OnPlayerPromptCompleted

        // Combo Strike — drive animation per pass using _AttackComboNoMovement.png frame layout.
        // Slashes alternate: slash1 (frames 1–3) and slash2 (frames 6–9).
        // The hold after each non-final slash is the wind-up for the NEXT slash:
        //   Pass 0: combo_slash1 → hold frame 5 (wind-up for slash2)
        //   Pass 1: combo_slash2 → hold frame 0 (wind-up for slash1 again)
        //   Pass 2: combo_slash1 → hold last frame of slash1 (final hit); retreat via OnFinalSlashFinished
        _comboPassIndex = passIndex;
        switch (_comboPassIndex)
        {
            case 0:
                PlayPlayer("combo_slash1");  // OWNER: combo pass 0, first strike (frames 1–3)
                _playerAnimSprite.AnimationFinished += OnComboPass0SlashFinished;
                break;
            case 1:
                PlayPlayer("combo_slash2");  // OWNER: combo pass 1, second strike (frames 6–9)
                _playerAnimSprite.AnimationFinished += OnComboPass1SlashFinished;
                break;
            case 2:
                // Final strike — OnFinalSlashFinished handles the 0.3s hold and retreat.
                // _pendingGameOver is set moments later by OnPlayerPromptCompleted (PromptCompleted
                // fires in the same frame as the last PassEvaluated), so it is always written
                // before the animation timer in OnFinalSlashFinished reads it.
                PlayPlayer("combo_slash1");  // OWNER: combo pass 2, final strike (frames 1–3)
                _playerAnimSprite.AnimationFinished += OnFinalSlashFinished;
                break;
        }
    }

    /// <summary>
    /// Animates the attacker back to their origin (ease-in = slow start, accelerates away)
    /// and the camera back to its default position and zoom.
    /// Calls <paramref name="onComplete"/> when done; safe to pass null.
    /// </summary>
    private void PlayTeardown(Action onComplete)
    {
        var tween = CreateTween();
        tween.SetParallel(true);
        // Hop out — ease-in (slow start, accelerates = snapping away).
        tween.TweenProperty(_attacker, "position", GetOrigin(_attacker), TeardownDuration)
             .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
        // Return the visible player sprite to its scene origin alongside the ColorRect.
        if (_attacker == _playerSprite)
        {
            tween.TweenProperty(_playerAnimSprite, "position", _playerAnimSpriteOrigin, TeardownDuration)
                 .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
        }
        // Camera zooms back out to default.
        tween.TweenProperty(_camera, "position", CameraDefaultPos, TeardownDuration)
             .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
        tween.TweenProperty(_camera, "zoom", CameraDefaultZoom, TeardownDuration)
             .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
        if (onComplete != null)
            tween.Finished += () => onComplete();
    }

    // =========================================================================
    // Sprite play guards
    // =========================================================================
    // All Play / PlayBackwards / Stop / Frame= calls on the two animated sprites
    // route through these helpers. Once a dead flag is set the sprite holds its
    // final death frame — no subsequent animation call can override it.

    /// <summary>Calls _playerAnimSprite.Play(anim) only if the player is not dead.</summary>
    private void PlayPlayer(string anim)          { if (!_playerDead) _playerAnimSprite.Play(anim); }

    /// <summary>Calls _playerAnimSprite.PlayBackwards(anim) only if the player is not dead.</summary>
    private void PlayPlayerBackwards(string anim) { if (!_playerDead) _playerAnimSprite.PlayBackwards(anim); }

    /// <summary>Calls _playerAnimSprite.Stop() only if the player is not dead.</summary>
    private void StopPlayer()                     { if (!_playerDead) _playerAnimSprite.Stop(); }

    /// <summary>Assigns _playerAnimSprite.Frame only if the player is not dead.</summary>
    private void SetPlayerFrame(int frame)        { if (!_playerDead) _playerAnimSprite.Frame = frame; }

    /// <summary>Calls _enemyAnimSprite.Play(anim) only if the enemy is not dead.</summary>
    private void PlayEnemy(string anim)           { if (!_enemyDead)  _enemyAnimSprite.Play(anim); }

    // =========================================================================
    // End-of-battle overlay
    // =========================================================================

    /// <summary>
    /// Creates a large white label — "Game Over" or "Victory!" — centered on screen
    /// on a dedicated CanvasLayer so it renders above all game elements.
    /// The label starts fully transparent and fades in to opaque over 0.5 seconds.
    /// Called once; no battle restart follows.
    /// </summary>
    private void ShowEndLabel(string text)
    {
        var layer = new CanvasLayer();
        AddChild(layer);

        var label = new Label();
        label.Text                = text;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment   = VerticalAlignment.Center;
        label.AddThemeFontSizeOverride("font_size", 64);
        label.Modulate = new Color(1f, 1f, 1f, 0f);  // start transparent; Tween fades in below

        // Stretch the label to fill the entire CanvasLayer viewport so the text centers
        // correctly regardless of resolution. Anchors: (0,0)→(1,1), offsets cleared.
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.OffsetLeft   = 0f;
        label.OffsetTop    = 0f;
        label.OffsetRight  = 0f;
        label.OffsetBottom = 0f;
        layer.AddChild(label);

        var tween = CreateTween();
        tween.TweenProperty(label, "modulate:a", 1.0f, 0.5f);
    }

    // =========================================================================
    // Animation position helpers
    // =========================================================================

    /// <summary>Returns the stored world-space origin for the given sprite.</summary>
    private Vector2 GetOrigin(ColorRect sprite) =>
        sprite == _playerSprite ? _playerOrigin : _enemyOrigin;

    /// <summary>
    /// Returns the position where the attacker stands in the close stance —
    /// <see cref="AttackGap"/> pixels from the defender's near edge, same Y as origin.
    /// Calculated from stored origins so it is independent of any animation in progress.
    /// </summary>
    private Vector2 ComputeClosePosition()
    {
        Vector2 attackerOrigin = GetOrigin(_attacker);
        Vector2 defenderOrigin = GetOrigin(_defender);
        bool    onLeft         = attackerOrigin.X < defenderOrigin.X;

        float closeX = onLeft
            ? defenderOrigin.X - _attacker.Size.X - AttackGap   // attacker right edge = defender left - gap
            : defenderOrigin.X + _defender.Size.X + AttackGap;  // attacker left edge  = defender right + gap

        return new Vector2(closeX, attackerOrigin.Y);
    }

    /// <summary>
    /// Returns the slam position — attacker overlaps the defender by <see cref="SlamOverlap"/> pixels.
    /// Also calculated from stored origins so slam depth is always the same regardless of
    /// where the attacker currently is.
    /// </summary>
    private Vector2 ComputeSlamPosition()
    {
        Vector2 attackerOrigin = GetOrigin(_attacker);
        Vector2 defenderOrigin = GetOrigin(_defender);
        bool    onLeft         = attackerOrigin.X < defenderOrigin.X;

        float slamX = onLeft
            ? defenderOrigin.X - _attacker.Size.X + SlamOverlap   // right edge overlaps defender by SlamOverlap
            : defenderOrigin.X + _defender.Size.X - SlamOverlap;  // left edge overlaps defender by SlamOverlap

        return new Vector2(slamX, _attackerClosePos.Y);
    }

    /// <summary>
    /// Returns the world-space midpoint between the attacker's close stance center
    /// and the defender's center — the point the camera zooms in on.
    /// </summary>
    private Vector2 ComputeCameraMidpoint()
    {
        Vector2 attackerCenter = _attackerClosePos   + _attacker.Size / 2f;
        Vector2 defenderCenter = GetOrigin(_defender) + _defender.Size / 2f;
        return (attackerCenter + defenderCenter) / 2f;
    }
}
