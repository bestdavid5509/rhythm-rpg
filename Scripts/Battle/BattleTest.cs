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
    private static readonly bool[]   MenuOptionEnabled = { true,     false             };

    private static readonly Color ColorMenuSelected = new Color(1.00f, 0.90f, 0.20f, 1.00f);  // yellow
    private static readonly Color ColorMenuNormal   = new Color(1.00f, 1.00f, 1.00f, 1.00f);  // white
    private static readonly Color ColorMenuDisabled = new Color(0.45f, 0.45f, 0.45f, 1.00f);  // grey

    private int         _menuIndex;
    private CanvasLayer _menuLayer;
    private Label[]     _menuLabels;

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
    private Vector2   _attackerClosePos;  // close-but-not-touching stance position for this turn

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
        _playerAnimSprite.Play("idle");

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
            _enemyAnimSprite.Play("cast_intro");
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

        // Drive player sprite reactions per-pass.
        // AnimationFinished handlers self-disconnect to prevent stacking on multi-step sequences.
        var r = (TimingPrompt.InputResult)result;
        if (r == TimingPrompt.InputResult.Hit || r == TimingPrompt.InputResult.Perfect)
        {
            // Successful block — deflect animation, then return to idle.
            _playerAnimSprite.Play("parry");
            _playerAnimSprite.AnimationFinished += OnParryFinished;
        }
        else if (r == TimingPrompt.InputResult.Miss)
        {
            // Strike landed — flinch animation, then return to idle.
            _playerAnimSprite.Play("hit");
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

        // Release pose: cast_end (3 frames @ 12fps ≈ 0.25s) plays concurrently with
        // PlayTeardown (0.35s) and finishes before the 0.5s post-teardown timer fires,
        // so idle is reached well before the player menu reappears.
        _enemyAnimSprite.Play("cast_end");
        _enemyAnimSprite.AnimationFinished += OnCastEndFinished;

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
        PlayTeardown(over ? null : () => GetTree().CreateTimer(0.5f).Timeout += ShowMenu);
    }

    // =========================================================================
    // Battle menu
    // =========================================================================

    private void BuildMenu()
    {
        _menuLayer      = new CanvasLayer();
        _menuLayer.Name = "BattleMenu";
        AddChild(_menuLayer);

        // Panel in 1920×1080 canvas space (CanvasLayer is unaffected by Camera2D).
        var panel = new PanelContainer();
        panel.Name              = "Panel";
        panel.Position          = new Vector2(810f, 470f);
        panel.CustomMinimumSize = new Vector2(300f, 0f);
        _menuLayer.AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.Name = "VBox";
        vbox.AddThemeConstantOverride("separation", 8);
        panel.AddChild(vbox);

        _menuLabels = new Label[MenuOptionLabels.Length];
        for (int i = 0; i < MenuOptionLabels.Length; i++)
        {
            var label = new Label();
            label.AddThemeFontSizeOverride("font_size", 24);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            vbox.AddChild(label);
            _menuLabels[i] = label;
        }

        _menuLayer.Visible = false;
    }

    private void ShowMenu()
    {
        _state     = BattleState.PlayerMenu;
        _menuIndex = 0;
        RefreshMenuLabels();
        _menuLayer.Visible = true;
        GD.Print("[BattleTest] Player menu shown.");
    }

    private void HideMenu()
    {
        _menuLayer.Visible = false;
    }

    private void HandleMenuInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_up"))
        {
            NavigateMenu(-1);
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed("ui_down"))
        {
            NavigateMenu(1);
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed("battle_confirm"))
        {
            ConfirmMenuSelection();
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

    private void ConfirmMenuSelection()
    {
        if (!MenuOptionEnabled[_menuIndex]) return;
        GD.Print($"[BattleTest] Player selects: {MenuOptionLabels[_menuIndex]}.");
        HideMenu();
        switch (_menuIndex)
        {
            case 0: BeginPlayerAttack(); break;
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

    // =========================================================================
    // Player attack phase
    // =========================================================================

    private void BeginPlayerAttack()
    {
        _state = BattleState.PlayerAttack;
        GD.Print("[BattleTest] Player attacks.");
        BeginAttack(_playerSprite, _enemySprite, TimingPrompt.PromptType.Standard, OnPlayerPromptCompleted);
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
        // Return to idle now that the attack prompt has resolved.
        _playerAnimSprite.Play("idle");
        GetTree().CreateTimer(TimingPrompt.FlashDuration).Timeout += FreeActivePrompt;
        bool over = CheckGameOver();
        PlayTeardown(over ? null : () => GetTree().CreateTimer(0.5f).Timeout += BeginEnemyAttack);
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

        // idle / run / attack1 / hit / death — each file maps to one animation.
        // _Attack2NoMovement.png (720×80 = 6 frames) provides two animations:
        //   attack2 — all 6 frames, for chained player attacks
        //   parry   — frames 2–5 (4 frames), plays on every successful parry input
        int frameH = AddPlayerAnimation(frames, "idle",    Base + "_Idle.png",              loop: true,  fw: Fw, fh: Fh);
                     AddPlayerAnimation(frames, "run",     Base + "_Run.png",               loop: true,  fw: Fw, fh: Fh);
                     AddPlayerAnimation(frames, "attack1", Base + "_AttackNoMovement.png",  loop: false, fw: Fw, fh: Fh);
                     AddPlayerAnimation(frames, "attack2", Base + "_Attack2NoMovement.png", loop: false, fw: Fw, fh: Fh);
                     AddPlayerAnimation(frames, "parry",   Base + "_Attack2NoMovement.png", loop: false, fw: Fw, fh: Fh, startFrame: 2);
                     AddPlayerAnimation(frames, "hit",     Base + "_Hit.png",               loop: false, fw: Fw, fh: Fh);
                     AddPlayerAnimation(frames, "death",   Base + "_DeathNoMovement.png",   loop: false, fw: Fw, fh: Fh);

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
    /// <paramref name="startFrame"/> skips leading frames; the loop runs from startFrame to strip end.
    ///
    /// parry detail: _Attack2NoMovement.png is 720×80 → 6 frames of 120×80.
    /// startFrame=2 adds regions Rect2(240,0,120,80)…Rect2(600,0,120,80) = 4 frames (indices 2–5).
    ///
    /// Returns the frame height for floor-anchored Y positioning; returns 0 on load failure.
    /// </summary>
    private static int AddPlayerAnimation(
        SpriteFrames frames, string name, string path,
        bool loop, int fw, int fh, float fps = 12f, int startFrame = 0)
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

        int count = texture.GetWidth() / fw;
        int used  = count - startFrame;

        GD.Print($"[BattleTest]   '{name}': {texture.GetWidth()}x{texture.GetHeight()}  " +
                 $"fw={fw} fh={fh}  strip={count}  startFrame={startFrame}  used={used}  " +
                 $"firstRegion=Rect2({startFrame * fw},0,{fw},{fh})");

        frames.AddAnimation(name);
        frames.SetAnimationSpeed(name, fps);
        frames.SetAnimationLoop(name, loop);

        for (int i = startFrame; i < count; i++)
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
        _enemyAnimSprite.Play("cast_loop");
    }

    private void OnCastEndFinished()
    {
        _enemyAnimSprite.AnimationFinished -= OnCastEndFinished;
        _enemyAnimSprite.Play("idle");
    }

    private void OnParryFinished()
    {
        _playerAnimSprite.AnimationFinished -= OnParryFinished;
        _playerAnimSprite.Play("idle");
    }

    private void OnHitAnimFinished()
    {
        _playerAnimSprite.AnimationFinished -= OnHitAnimFinished;
        _playerAnimSprite.Play("idle");
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
            int runFrames = _playerAnimSprite.SpriteFrames?.GetFrameCount("run") ?? 0;
            GD.Print($"[BattleTest] PlayHopIn — attacker={attacker.Name}  isPlayer=true  run frames={runFrames}");
            if (runFrames > 0)
            {
                _playerAnimSprite.SpeedScale = 2f;
                _playerAnimSprite.Play("run");
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
            // Hop-in finished — switch player to attack1 so the swing is ready for input.
            if (attacker == _playerSprite)
            {
                GD.Print($"[BattleTest] Hop-in finished — SpeedScale → 1, playing attack1. " +
                         $"Was SpeedScale={_playerAnimSprite.SpeedScale:F1}");
                _playerAnimSprite.SpeedScale = 1f;
                _playerAnimSprite.Play("attack1");
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
        Vector2 slamPos = ComputeSlamPosition();
        var tween = CreateTween();
        // Quick lunge forward.
        tween.TweenProperty(_attacker, "position", slamPos, SlamInDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        // Pull back to close stance.
        tween.TweenProperty(_attacker, "position", _attackerClosePos, SlamOutDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
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
