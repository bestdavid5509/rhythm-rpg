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

    private TimingPrompt _activePrompt;
    private PackedScene  _timingPromptScene;


    // =========================================================================
    // Characters and animations
    // =========================================================================

    private ColorRect _playerSprite;
    private ColorRect _enemySprite;
    private Vector2   _playerOrigin;   // position at scene load — always restored after teardown
    private Vector2   _enemyOrigin;

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
        BeginAttack(_enemySprite, _playerSprite, TimingPrompt.PromptType.Bouncing, OnEnemyPromptCompleted);
        // Connect the per-pass damage handler after BeginAttack sets _activePrompt.
        _activePrompt.PassEvaluated += OnEnemyPassEvaluated;
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

    private void OnEnemyPromptCompleted(int result)
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
        GetTree().CreateTimer(TimingPrompt.FlashDuration).Timeout += FreeActivePrompt;
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
    // Attack animation — setup, per-pass slam, teardown
    // =========================================================================

    /// <summary>
    /// Unified entry point for any attack turn.
    /// Stores attacker/defender, plays the setup animation, then adds the prompt
    /// to the scene tree when the animation completes (so the first prompt input
    /// is only possible after the hop-in finishes).
    /// </summary>
    private void BeginAttack(
        ColorRect attacker,
        ColorRect defender,
        TimingPrompt.PromptType promptType,
        TimingPrompt.PromptCompletedEventHandler onComplete)
    {
        _attacker         = attacker;
        _defender         = defender;
        _attackerClosePos = ComputeClosePosition();

        // Build the prompt node but defer AddChild until the setup tween finishes.
        var prompt = _timingPromptScene.Instantiate<TimingPrompt>();
        prompt.Type     = promptType;
        prompt.AutoLoop = false;
        prompt.Position = ComputeCameraMidpoint();
        prompt.PassEvaluated   += OnAttackPassEvaluated;
        prompt.PromptCompleted += onComplete;
        _activePrompt = prompt;

        var tween = CreateTween();
        tween.SetParallel(true);
        // Hop in — ease-out (fast start, decelerates on arrival = lunge).
        tween.TweenProperty(attacker, "position", _attackerClosePos, SetupDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        // Camera zooms in centered between the two combatants.
        tween.TweenProperty(_camera, "position", ComputeCameraMidpoint(), SetupDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        tween.TweenProperty(_camera, "zoom", CameraZoomIn, SetupDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        // Start the prompt only after the hop-in completes.
        tween.Finished += () => AddChild(prompt);
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
