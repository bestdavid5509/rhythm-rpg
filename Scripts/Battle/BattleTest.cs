using Godot;

/// <summary>
/// Battle prototype — implements a complete turn-based battle loop:
///   Enemy attacks (TimingPrompt) → Player defends → Battle menu → Player attacks → repeat.
///
/// Core principle: input and damage are always simultaneous — the button press IS the strike.
///
/// Enemy attacks always play their full sequence regardless of player input outcome.
/// Player attack combos end on the first missed input.
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
    // -------------------------------------------------------------------------
    // State machine
    // -------------------------------------------------------------------------

    private enum BattleState { EnemyAttack, PlayerMenu, PlayerAttack, GameOver }
    private BattleState _state = BattleState.EnemyAttack;

    // -------------------------------------------------------------------------
    // HP
    // -------------------------------------------------------------------------

    private const int PlayerMaxHP = 100;
    private const int EnemyMaxHP  = 100;

    private int _playerHP = PlayerMaxHP;
    private int _enemyHP  = EnemyMaxHP;

    private ColorRect _playerHPFill;
    private ColorRect _enemyHPFill;

    // -------------------------------------------------------------------------
    // Perfect parry
    // -------------------------------------------------------------------------

    // Set true at the start of each enemy attack, cleared to false on any Miss result.
    // Checked when PromptCompleted fires to decide whether to trigger the auto counter.
    private bool _parryClean;

    // -------------------------------------------------------------------------
    // Battle menu
    // -------------------------------------------------------------------------

    private static readonly string[] MenuOptionLabels   = { "Attack", "Absorbed Moves" };
    private static readonly bool[]   MenuOptionEnabled  = { true,     false             };

    private static readonly Color ColorMenuSelected = new Color(1.00f, 0.90f, 0.20f, 1.00f);  // yellow
    private static readonly Color ColorMenuNormal   = new Color(1.00f, 1.00f, 1.00f, 1.00f);  // white
    private static readonly Color ColorMenuDisabled = new Color(0.45f, 0.45f, 0.45f, 1.00f);  // grey

    private int          _menuIndex;
    private CanvasLayer  _menuLayer;
    private Label[]      _menuLabels;

    // -------------------------------------------------------------------------
    // Damage numbers
    // -------------------------------------------------------------------------

    // Colors match the input result that caused the damage.
    private static readonly Color DmgColorPerfect = new Color(0.30f, 1.00f, 0.40f, 1.00f);  // green
    private static readonly Color DmgColorHit     = new Color(1.00f, 1.00f, 1.00f, 1.00f);  // white
    private static readonly Color DmgColorMiss    = new Color(0.60f, 0.60f, 0.60f, 1.00f);  // grey (weak hit)
    private static readonly Color DmgColorPlayer  = new Color(1.00f, 0.25f, 0.25f, 1.00f);  // red

    // Spawn point for damage numbers — centered on each sprite, just above its top edge.
    // PlayerSprite:  offset_left=230, offset_right=330  → center X=280,  top Y=440
    // EnemySprite:   offset_left=1570, offset_right=1690 → center X=1630, top Y=400
    private static readonly Vector2 PlayerDamageOrigin = new Vector2(280f,  420f);
    private static readonly Vector2 EnemyDamageOrigin  = new Vector2(1630f, 380f);

    // -------------------------------------------------------------------------
    // Prompt management
    // -------------------------------------------------------------------------

    private TimingPrompt _activePrompt;
    private PackedScene  _timingPromptScene;

    private static readonly Vector2 PromptCenter = new Vector2(960f, 540f);

    // =========================================================================
    // Lifecycle
    // =========================================================================

    public override void _Ready()
    {
        _timingPromptScene = GD.Load<PackedScene>("res://Scenes/Battle/TimingPrompt.tscn");

        _playerHPFill = GetNode<ColorRect>("PlayerHP/Fill");
        _enemyHPFill  = GetNode<ColorRect>("EnemyHP/Fill");

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
        _parryClean = true;  // assume perfect until a Miss is registered
        GD.Print("[BattleTest] Enemy attacks.");

        SpawnPrompt(TimingPrompt.PromptType.Bouncing, OnEnemyPromptCompleted);
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
        GD.Print($"[BattleTest] Enemy attack sequence complete.");

        // Per-pass damage and _parryClean tracking are handled in OnEnemyPassEvaluated.
        // Only the parry counter is resolved here, after all passes have been evaluated.
        if (_parryClean)
        {
            const int CounterDamage = 20;
            _enemyHP = Mathf.Max(0, _enemyHP - CounterDamage);
            GD.Print($"[BattleTest] Perfect parry! Auto counter: {CounterDamage} damage. Enemy HP: {_enemyHP}/{EnemyMaxHP}");
            SpawnDamageNumber(EnemyDamageOrigin, CounterDamage, DmgColorPerfect);
        }

        UpdateHPBars();
        FreeActivePrompt();

        if (CheckGameOver()) return;

        GetTree().CreateTimer(1.0).Timeout += ShowMenu;
    }

    // =========================================================================
    // Battle menu
    // =========================================================================

    private void BuildMenu()
    {
        _menuLayer      = new CanvasLayer();
        _menuLayer.Name = "BattleMenu";
        AddChild(_menuLayer);

        // Outer panel — positions in 1920×1080 canvas space (CanvasLayer coordinate system).
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
        // Step in the requested direction, skipping disabled options.
        for (int i = 0; i < count; i++)
        {
            next = (next + direction + count) % count;
            if (MenuOptionEnabled[next])
            {
                _menuIndex = next;
                break;
            }
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

        SpawnPrompt(TimingPrompt.PromptType.Standard, OnPlayerPromptCompleted);
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
        FreeActivePrompt();

        if (CheckGameOver()) return;

        GetTree().CreateTimer(1.0).Timeout += BeginEnemyAttack;
    }

    // =========================================================================
    // Shared helpers
    // =========================================================================

    /// <summary>
    /// Instantiates a TimingPrompt, sets type and position, disables AutoLoop,
    /// connects the given completion handler, and adds it as a child.
    /// </summary>
    private void SpawnPrompt(TimingPrompt.PromptType type, TimingPrompt.PromptCompletedEventHandler onCompleted)
    {
        var prompt       = _timingPromptScene.Instantiate<TimingPrompt>();
        prompt.Type      = type;
        prompt.AutoLoop  = false;
        prompt.Position  = PromptCenter;
        prompt.PromptCompleted += onCompleted;
        AddChild(prompt);
        _activePrompt = prompt;
    }

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
    /// The label is 80px wide and centered on the spawn point.
    /// </summary>
    private void SpawnDamageNumber(Vector2 position, int amount, Color color)
    {
        var label = new Label();
        label.Text                = amount.ToString();
        label.Modulate            = color;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.CustomMinimumSize   = new Vector2(80f, 0f);
        label.AddThemeFontSizeOverride("font_size", 28);

        // Center the 80px label on the spawn point.
        Vector2 startPos = position - new Vector2(40f, 0f);
        Vector2 endPos   = startPos  - new Vector2(0f, 80f);
        label.Position   = startPos;

        AddChild(label);

        var tween = CreateTween();
        tween.SetParallel(true);
        tween.TweenProperty(label, "position", endPos,  1.0f);
        tween.TweenProperty(label, "modulate:a", 0.0f,  1.0f);
        tween.Finished += label.QueueFree;
    }
}
