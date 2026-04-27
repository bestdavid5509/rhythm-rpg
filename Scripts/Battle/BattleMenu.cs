using Godot;

/// <summary>
/// Partial — battle menu construction, navigation, and input handling for BattleTest.
/// All members declared here are part of the same BattleTest class; fields and
/// methods defined in BattleTest.cs and BattleAnimator.cs are fully accessible.
/// </summary>
public partial class BattleTest : Node2D
{
    // =========================================================================
    // Battle menu
    // =========================================================================

    private static readonly string[] MenuOptionLabels  = { "Attack", "Skills", "Defend", "Items" };
    private static readonly bool[]   MenuOptionEnabled = { true,     true,             true,     true   };

    // Skills submenu — base skills + Absorber-only entries (Beckon, absorbed
    // moves) followed by "Back". Built dynamically by RebuildSubMenu(); rebuilt
    // every time the submenu opens so it reflects the current active player.
    // TODO: submenu population should eventually be driven by the player's persistent move list from the character system
    private System.Collections.Generic.List<string>          _subMenuLabelsData;
    private System.Collections.Generic.List<AttackCategory?> _subMenuCategoriesData;
    private System.Collections.Generic.List<AttackData>      _subMenuAttacksData;  // null for Back

    // Items submenu — items followed by "Back". Built dynamically based on item counts.
    private System.Collections.Generic.List<string> _itemMenuLabelsData;

    // MP cost for Beckon (utility ability with no AttackData backing).
    private const int BeckonMpCost = 10;

    // Font sizes for the action menu — header is the largest (category label),
    // option labels are bumped above the default UiFontSize=14 so they read
    // clearly alongside the larger header.
    private const int MenuHeaderFontSize = 20;
    private const int MenuOptionFontSize = 18;

    private static readonly Color ColorMenuSelected      = new Color(1.00f, 0.90f, 0.20f, 1.00f);  // yellow — selected item
    private static readonly Color ColorMenuNormal        = new Color(1.00f, 1.00f, 1.00f, 1.00f);  // white  — unselected, no category
    private static readonly Color ColorMenuDisabled      = new Color(0.45f, 0.45f, 0.45f, 1.00f);  // grey   — disabled item
    private static readonly Color ColorCategoryPhysical  = new Color(1.00f, 0.50f, 0.00f, 1.00f);  // orange — Physical attacks in submenu
    private static readonly Color ColorCategoryMagic     = new Color(0.60f, 0.30f, 1.00f, 1.00f);  // purple — Magic attacks in submenu

    private bool            _inSubMenu;      // true while the Skills submenu is open
    private bool            _inItemMenu;     // true while the Items submenu is open
    private int             _menuIndex;
    private int             _subMenuIndex;
    private int             _itemMenuIndex;
    private CanvasLayer     _menuLayer;
    private PanelContainer  _mainMenuPanel;
    private PanelContainer  _subMenuPanel;
    private PanelContainer  _itemMenuPanel;
    private VBoxContainer   _mainMenuVBox;   // direct reference — labels sit inside MarginContainer inside panel
    private VBoxContainer   _subMenuVBox;
    private VBoxContainer   _itemMenuVBox;
    private Label[]         _menuLabels;
    private Label[]         _subMenuLabels;
    private Label[]         _itemMenuLabels;
    // Active-player header — "{Name}'s turn" — shown on each panel above its option list.
    // Main-menu label is created once in BuildMenu and persists. Sub-menu / item-menu labels
    // are created (and wiped + recreated) on every rebuild via PopulateSubMenuPanel /
    // RebuildItemMenu, since those rebuilds clear all VBox children.
    private Label           _mainMenuHeaderLabel;
    private Label           _subMenuHeaderLabel;
    private Label           _itemMenuHeaderLabel;

    private void BuildMenu()
    {
        _menuLayer      = new CanvasLayer();
        _menuLayer.Name = "BattleMenu";
        AddChild(_menuLayer);

        // Main menu — Attack / Skills / Defend / Items, with the active-player
        // header label as the first child. The header text is filled in by
        // RefreshMenuHeader (called from AdvanceTurn / ShowMenu). Header sits above
        // a solid divider (not the fade-out variant used between options) so the
        // header reads as a category label rather than another menu entry. The same
        // header treatment is applied to the sub-menu and item-menu panels via
        // AddMenuHeader, so the active player's name stays visible while drilling.
        _mainMenuPanel = MakeMenuPanel(_menuLayer, out _mainMenuVBox);
        AddMenuHeader(_mainMenuVBox, out _mainMenuHeaderLabel);

        _menuLabels    = new Label[MenuOptionLabels.Length];
        for (int i = 0; i < MenuOptionLabels.Length; i++)
        {
            if (i > 0) AddMenuDivider(_mainMenuVBox);
            var label = new Label();
            StyleLabel(label, fontSize: MenuOptionFontSize);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            _mainMenuVBox.AddChild(label);
            _menuLabels[i] = label;
        }

        // Skills submenu — built dynamically from _subMenuLabelsData.
        _subMenuPanel = MakeMenuPanel(_menuLayer, out _subMenuVBox);
        RebuildSubMenu();

        // Items submenu — built dynamically from _itemMenuLabelsData.
        _itemMenuPanel = MakeMenuPanel(_menuLayer, out _itemMenuVBox);
        RebuildItemMenu();

        _menuLayer.Visible = false;
    }

    /// <summary>
    /// Builds a layered Kenney 9-slice panel anchored to the bottom-left of the viewport.
    /// Position is fixed (does not track the active player's panel) — the player panel
    /// strip lives at bottom-center post-C6, so the action menu has its own dedicated
    /// corner at bottom-left. The active player is communicated via the menu header
    /// label (see RefreshMenuHeader) instead.
    /// </summary>
    private PanelContainer MakeMenuPanel(CanvasLayer layer, out VBoxContainer content)
    {
        var p = MakeLayeredPanel(PanelMinWidthMenu, out content);
        p.AnchorLeft     = 0f;
        p.AnchorRight    = 0f;
        p.AnchorTop      = 1f;
        p.AnchorBottom   = 1f;
        p.GrowHorizontal = Control.GrowDirection.End;
        p.GrowVertical   = Control.GrowDirection.Begin;
        p.OffsetLeft     = UiEdgeMargin;
        // Bottom-aligned with the player strip — both sit at OffsetBottom = -UiEdgeMargin
        // so the menu and the strip share a baseline. Visually grounded; no floating.
        p.OffsetBottom   = -UiEdgeMargin;
        layer.AddChild(p);
        return p;
    }

    /// <summary>
    /// Adds the active-player header — a Label sized at <see cref="MenuHeaderFontSize"/>
    /// followed by an <see cref="AddMenuHeaderDivider"/> — as the first two children of
    /// a menu panel's <c>VBoxContainer</c>. Used by all three panels (main, sub, item)
    /// so the active player's name persists while drilling between menus.
    /// </summary>
    private void AddMenuHeader(VBoxContainer parent, out Label headerLabel)
    {
        headerLabel = new Label();
        StyleLabel(headerLabel, fontSize: MenuHeaderFontSize);
        headerLabel.HorizontalAlignment = HorizontalAlignment.Left;
        parent.AddChild(headerLabel);
        AddMenuHeaderDivider(parent);
    }

    /// <summary>
    /// Updates every menu-panel header with the active player's name. Called from
    /// AdvanceTurn (after _activePlayer = current), ShowMenu (defensive — covers
    /// first-turn-after-intro), and the rebuild paths (PopulateSubMenuPanel /
    /// RebuildItemMenu) so freshly-recreated header labels pick up current text.
    /// All three labels hide together when _activePlayer is null. Per-label null
    /// guards because the sub-menu / item-menu labels haven't been built yet on
    /// the very first BuildMenu call (RebuildSubMenu fires before RebuildItemMenu).
    /// </summary>
    private void RefreshMenuHeader()
    {
        bool   show = _activePlayer != null;
        // Combatant names are already title-case ("Knight", "Knight 2"); no .ToUpper().
        // No "▶ " prefix — the active option in the option list already carries its
        // own ▶ cursor; two cursors create confusion, not emphasis.
        string text = show ? $"{_activePlayer.Name}'s turn" : string.Empty;

        if (_mainMenuHeaderLabel != null)
        {
            _mainMenuHeaderLabel.Visible = show;
            _mainMenuHeaderLabel.Text    = text;
        }
        if (_subMenuHeaderLabel != null)
        {
            _subMenuHeaderLabel.Visible = show;
            _subMenuHeaderLabel.Text    = text;
        }
        if (_itemMenuHeaderLabel != null)
        {
            _itemMenuHeaderLabel.Visible = show;
            _itemMenuHeaderLabel.Text    = text;
        }
    }

    /// <summary>
    /// Adds a Kenney Divider Fade TextureRect between menu options. Not a Label —
    /// navigation logic iterates the `_menuLabels` / `_subMenuLabels` / `_itemMenuLabels`
    /// arrays directly, so dividers are purely decorative siblings in the VBox.
    ///
    /// <paramref name="texturePath"/> defaults to the battle-menu divider-fade asset;
    /// callers (e.g. the Game Over panel) can pass a different divider art file.
    /// </summary>
    private void AddMenuDivider(VBoxContainer parent, string texturePath = null)
    {
        var divider = new TextureRect();
        divider.Texture             = GD.Load<Texture2D>(texturePath ?? UiDividerPath);
        divider.StretchMode         = TextureRect.StretchModeEnum.Scale;
        divider.CustomMinimumSize   = new Vector2(0f, 6f);
        divider.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        divider.Modulate            = new Color(1f, 1f, 1f, 0.25f);
        parent.AddChild(divider);
    }

    /// <summary>
    /// Adds the action-menu header divider — a Kenney fade-texture variant matching
    /// the panel's other UI chrome (NinePatch borders use the same family). Distinct
    /// from the between-options <see cref="AddMenuDivider"/> (fade-000 at 6px)
    /// in that fade-002 has a stronger center crossbar and renders at the texture's
    /// natural 10px height, so the active player's name reads as a category label
    /// rather than another menu entry.
    /// 4px spacer Controls above and below give the divider visible breathing room
    /// between the header text and the first option (VBoxContainer separation only
    /// affects spacing between adjacent children, not above/below specific ones).
    /// </summary>
    private void AddMenuHeaderDivider(VBoxContainer parent)
    {
        // Top padding before divider.
        var topPad = new Control();
        topPad.CustomMinimumSize = new Vector2(0f, 4f);
        parent.AddChild(topPad);

        // The Kenney fade-002 textured divider (96x10 natural; horizontal stretches
        // to panel width; vertical stays at natural 10 px to avoid distortion).
        var divider = new TextureRect();
        divider.Texture             = GD.Load<Texture2D>(
            "res://Assets/UI/kenney_fantasy-ui-borders/PNG/Default/Divider Fade/divider-fade-002.png");
        divider.StretchMode         = TextureRect.StretchModeEnum.Scale;
        divider.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        divider.CustomMinimumSize   = new Vector2(0f, 10f);
        parent.AddChild(divider);

        // Bottom padding after divider.
        var bottomPad = new Control();
        bottomPad.CustomMinimumSize = new Vector2(0f, 4f);
        parent.AddChild(bottomPad);
    }

    private void ShowMenu()
    {
        // _activePlayer is set by AdvanceTurn before this method is called.
        // The queue's IsDead-skip ensures dead combatants never reach here;
        // CheckGameOver at AdvanceTurn entry catches end-of-battle before any
        // dispatch.
        _state                        = BattleState.PlayerMenu;
        _inputLocked                  = false;  // Unlock input — player can interact with menu.
        _activePlayer.IsDefending     = false;  // Defend only lasts one enemy turn.
        _inSubMenu                    = false;
        _inItemMenu                   = false;
        _menuIndex                    = 0;
        _mainMenuPanel.Visible        = true;
        _subMenuPanel.Visible         = false;
        _itemMenuPanel.Visible        = false;
        _menuLayer.Visible            = true;
        RefreshMenuLabels();
        // Defensive header refresh — the AdvanceTurn call before this also updates
        // it, but the first-turn-after-intro path enters via ShowMenuWithFadeIn
        // which doesn't go through AdvanceTurn for the very first turn.
        RefreshMenuHeader();
        // Refresh panels — _state is now PlayerMenu, so the active-player highlight
        // branch fires for the correct panel. The earlier UpdateHPBars in AdvanceTurn
        // still covers dead-slot Modulate refresh at queue-advance time, but at that
        // call _state hasn't transitioned yet (still EnemyAttack from the prior turn);
        // this second refresh catches the active highlight once the state is right.
        UpdateHPBars();
        GD.Print("[BattleTest] Player menu shown.");
    }

    /// <summary>
    /// Wraps <see cref="ShowMenu"/> with a Modulate-alpha fade-in on the main menu panel.
    /// Used by the intro-dialogue handoff where instant reveal would pop against the
    /// concurrent music fade-in. Pre-sets the panel's modulate alpha to 0 before ShowMenu
    /// (so Visible = true doesn't snap it opaque), then tweens alpha to 1 over the duration.
    /// </summary>
    private void ShowMenuWithFadeIn(float durationSec)
    {
        if (_mainMenuPanel != null)
            _mainMenuPanel.Modulate = new Color(1f, 1f, 1f, 0f);
        ShowMenu();
        if (_mainMenuPanel == null) return;
        var tween = CreateTween();
        tween.TweenProperty(_mainMenuPanel, "modulate:a", 1f, durationSec);
    }

    private void ShowSubMenu()
    {
        // Rebuild from the active player's skill set every time the submenu opens.
        // C4.5: the active player is always slot 0 so the rebuild output is stable;
        // C5's queue can rotate active player between turns, and this rebuild call
        // ensures the submenu reflects the current active player's IsAbsorber state.
        RebuildSubMenu();

        _inSubMenu              = true;
        _inItemMenu             = false;
        _subMenuIndex           = 0;
        _mainMenuPanel.Visible  = false;
        _subMenuPanel.Visible   = true;
        _itemMenuPanel.Visible  = false;
        RefreshSubMenuLabels();
        GD.Print("[BattleTest] Skills submenu shown.");
    }

    private void ShowItemMenu()
    {
        _inSubMenu              = false;
        _inItemMenu             = true;
        _itemMenuIndex          = 0;
        _mainMenuPanel.Visible  = false;
        _subMenuPanel.Visible   = false;
        _itemMenuPanel.Visible  = true;
        RebuildItemMenu();
        RefreshItemMenuLabels();
        GD.Print("[BattleTest] Items submenu shown.");
    }

    /// <summary>
    /// Rebuilds the Items submenu labels based on current item counts.
    /// Ether appears only when EtherCount > 0; Back is always the last entry.
    /// </summary>
    private void RebuildItemMenu()
    {
        _itemMenuLabelsData = new System.Collections.Generic.List<string>();
        if (EtherCount > 0)
            _itemMenuLabelsData.Add($"Ether x{EtherCount}");
        _itemMenuLabelsData.Add("Back");

        foreach (var child in _itemMenuVBox.GetChildren())
            child.QueueFree();

        // Active-player header is recreated on every rebuild because the wipe above
        // destroyed the previous one. RefreshMenuHeader at the end populates the
        // text now that the field reference points at the new label.
        AddMenuHeader(_itemMenuVBox, out _itemMenuHeaderLabel);

        _itemMenuLabels = new Label[_itemMenuLabelsData.Count];
        for (int i = 0; i < _itemMenuLabelsData.Count; i++)
        {
            if (i > 0) AddMenuDivider(_itemMenuVBox);
            var label = new Label();
            StyleLabel(label, fontSize: MenuOptionFontSize);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            _itemMenuVBox.AddChild(label);
            _itemMenuLabels[i] = label;
        }
        RefreshMenuHeader();
    }

    private void HideMenu()
    {
        _menuLayer.Visible = false;
    }

    /// <summary>
    /// Creates Label nodes in the submenu panel to match _subMenuLabelsData, with Kenney
    /// divider TextureRects between entries.
    /// </summary>
    private void PopulateSubMenuPanel()
    {
        // Clear existing labels and dividers (including the previous header label,
        // which is recreated below).
        foreach (var child in _subMenuVBox.GetChildren())
            child.QueueFree();

        // Active-player header — same treatment as the main menu.
        AddMenuHeader(_subMenuVBox, out _subMenuHeaderLabel);

        _subMenuLabels = new Label[_subMenuLabelsData.Count];
        for (int i = 0; i < _subMenuLabelsData.Count; i++)
        {
            if (i > 0) AddMenuDivider(_subMenuVBox);
            var label = new Label();
            StyleLabel(label, fontSize: MenuOptionFontSize);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            _subMenuVBox.AddChild(label);
            _subMenuLabels[i] = label;
        }
        RefreshMenuHeader();
    }

    /// <summary>
    /// Rebuilds the Skills submenu data lists from the active player's skill set:
    ///   - Base entries (Combo Strike, Magic Comet, Cure) — present for every player.
    ///   - Absorber-only entries (Beckon, absorbed moves from <c>_absorbedMoves</c>) —
    ///     gated on <c>_activePlayer.IsAbsorber</c>.
    ///   - "Back" — always last.
    /// Idempotent: clears the lists at top so re-entry from BuildMenu, ShowSubMenu,
    /// or TryTriggerAbsorption doesn't accumulate duplicates. Resolves AttackData
    /// references at rebuild time so <see cref="GetSubMenuAttack"/> reads
    /// <c>_subMenuAttacksData[index]</c> directly without index-specific switch logic.
    /// </summary>
    private void RebuildSubMenu()
    {
        _subMenuLabelsData     = new System.Collections.Generic.List<string>();
        _subMenuCategoriesData = new System.Collections.Generic.List<AttackCategory?>();
        _subMenuAttacksData    = new System.Collections.Generic.List<AttackData>();

        // Combo Strike — every player has it.
        _subMenuLabelsData.Add("Combo Strike");
        _subMenuCategoriesData.Add(AttackCategory.Physical);
        _subMenuAttacksData.Add(_playerComboStrike);

        // Beckon — Absorber only. Bucketed as Magic for color tinting (it's an
        // MP-costed magical utility ability, no AttackData backing).
        if (_activePlayer != null && _activePlayer.IsAbsorber)
        {
            _subMenuLabelsData.Add("Beckon");
            _subMenuCategoriesData.Add(AttackCategory.Magic);
            _subMenuAttacksData.Add(null);
        }

        // Magic Comet, Cure — every player has both.
        _subMenuLabelsData.Add("Magic Comet");
        _subMenuCategoriesData.Add(AttackCategory.Magic);
        _subMenuAttacksData.Add(_playerMagicAttack);

        _subMenuLabelsData.Add("Cure");
        _subMenuCategoriesData.Add(AttackCategory.Magic);
        _subMenuAttacksData.Add(_playerCureAttack);

        // Absorbed moves — Absorber only. Order is iteration order of the HashSet
        // (stable enough at Phase 6 scope: at most 1 entry per fight today).
        if (_activePlayer != null && _activePlayer.IsAbsorber)
        {
            foreach (var move in _absorbedMoves)
            {
                string label = !string.IsNullOrEmpty(move.DisplayName)
                    ? move.DisplayName
                    : "Absorbed Move";
                _subMenuLabelsData.Add(label);
                _subMenuCategoriesData.Add(move.Category);
                _subMenuAttacksData.Add(move);
            }
        }

        // Back — always last.
        _subMenuLabelsData.Add("Back");
        _subMenuCategoriesData.Add(null);
        _subMenuAttacksData.Add(null);

        PopulateSubMenuPanel();
    }

    private void HandleMenuInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_up"))
        {
            if (_inItemMenu) NavigateItemMenu(-1);
            else if (_inSubMenu) NavigateSubMenu(-1);
            else NavigateMenu(-1);
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed("ui_down"))
        {
            if (_inItemMenu) NavigateItemMenu(1);
            else if (_inSubMenu) NavigateSubMenu(1);
            else NavigateMenu(1);
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed("battle_confirm"))
        {
            if (_inItemMenu) ConfirmItemMenuSelection();
            else if (_inSubMenu) ConfirmSubMenuSelection();
            else ConfirmMenuSelection();
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed("battle_cancel"))
        {
            // Quick back-out from either submenu. Main menu has no parent to
            // return to, so battle_cancel is a no-op there. Identical effect
            // to selecting the "Back" entry + confirm; kept as a parallel path
            // so controller users don't need to navigate to Back every time.
            if (_inItemMenu || _inSubMenu)
            {
                ShowMenu();
                GetViewport().SetInputAsHandled();
            }
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
        int count = _subMenuLabelsData.Count;
        int next  = _subMenuIndex;
        for (int i = 0; i < count; i++)
        {
            next = (next + direction + count) % count;
            if (IsSubMenuOptionEnabled(next)) { _subMenuIndex = next; break; }
        }
        RefreshSubMenuLabels();
    }

    private void NavigateItemMenu(int direction)
    {
        int count = _itemMenuLabelsData.Count;
        _itemMenuIndex = (_itemMenuIndex + direction + count) % count;
        RefreshItemMenuLabels();
    }

    private void ConfirmItemMenuSelection()
    {
        string label = _itemMenuLabelsData[_itemMenuIndex];
        GD.Print($"[BattleTest] Player selects item: {label}.");

        // "Back" is always the last entry.
        if (_itemMenuIndex == _itemMenuLabelsData.Count - 1)
        {
            ShowMenu();
            return;
        }

        if (label.StartsWith("Ether"))
        {
            // EtherCount decrement and UseEtherItem invocation move into the launcher
            // so cancel from SelectingTarget is a clean no-cost back-out. Default target
            // is the player (sole MP-having combatant today); multi-ally pools post
            // Phase 6 will let the player pick which ally receives the MP restore.
            _pendingActionLauncher = () =>
            {
                EtherCount--;
                UseEtherItem();
            };
            HideMenu();
            EnterSelectingTarget(_activePlayer, MenuContext.Items);
        }
    }

    private void ConfirmMenuSelection()
    {
        if (!MenuOptionEnabled[_menuIndex]) return;
        GD.Print($"[BattleTest] Player selects: {MenuOptionLabels[_menuIndex]}.");
        switch (_menuIndex)
        {
            case 0:  // Basic Attack — single-hit enemy attack, no MP cost.
                _isComboAttack = false;
                _pendingActionLauncher = () => BeginPlayerAttack();
                HideMenu();
                EnterSelectingTarget(_enemyParty[0], MenuContext.Main);
                break;
            case 1: ShowSubMenu(); break;   // Skills
            case 2:                         // Defend — halve miss damage this enemy turn
                _activePlayer.IsDefending = true;
                GD.Print("[BattleTest] Player defending — incoming miss damage halved this turn.");
                HideMenu();
                AdvanceTurn();
                break;
            case 3: ShowItemMenu(); break;  // Items
        }
    }

    private void ConfirmSubMenuSelection()
    {
        if (!IsSubMenuOptionEnabled(_subMenuIndex)) return;
        string label = _subMenuLabelsData[_subMenuIndex];
        GD.Print($"[BattleTest] Player selects submenu: {label}.");

        // "Back" is always the last entry.
        if (_subMenuIndex == _subMenuLabelsData.Count - 1)
        {
            ShowMenu();
            return;
        }

        // Beckon — Absorber-only utility. Routes through SelectingTarget so the
        // player picks which enemy to redirect onto themselves. The launcher
        // reads _selectedTarget at confirm time and stores it on the active
        // player's BeckoningTarget; cancel back-out costs nothing.
        if (label == "Beckon")
        {
            _pendingActionLauncher = () => PerformBeckon();
            HideMenu();
            EnterSelectingTarget(_enemyParty[0], MenuContext.Skills);
            return;
        }

        var attack   = GetSubMenuAttack(_subMenuIndex);
        var category = _subMenuCategoriesData[_subMenuIndex];

        if (category == AttackCategory.Physical)
        {
            // Physical moves (Combo Strike) — combo attack flow. MP deduction moves
            // into the launcher so cancel from SelectingTarget is a no-cost back-out.
            _isComboAttack = true;
            int mpCost = attack?.MpCost ?? 0;
            _pendingActionLauncher = () =>
            {
                if (mpCost > 0) _activePlayer.CurrentMp -= mpCost;
                BeginPlayerAttack();
            };
            HideMenu();
            EnterSelectingTarget(_enemyParty[0], MenuContext.Skills);
        }
        else if (category == AttackCategory.Magic)
        {
            // Magic moves (Magic Comet, Comet Barrage, Cure, etc.) — magic attack flow.
            // Default-target resolution via the attack-identity dispatch predicate
            // (same split as Phase 3.6): Cure targets self, other magic targets enemy.
            // MP deduction moves into the launcher — deducted on confirm, not on menu
            // pick, so cancel from SelectingTarget is a clean no-cost back-out.
            _activeMagicAttack = attack;
            bool isHealAttack = attack == _playerCureAttack;
            Combatant defaultTarget = isHealAttack ? _activePlayer : _enemyParty[0];
            int mpCost = attack?.MpCost ?? 0;
            _pendingActionLauncher = () =>
            {
                if (mpCost > 0) _activePlayer.CurrentMp -= mpCost;
                BeginPlayerMagicAttack();
            };
            HideMenu();
            EnterSelectingTarget(defaultTarget, MenuContext.Skills);
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

    /// <summary>
    /// Returns the AttackData associated with a submenu index, or null for non-attack
    /// entries (Beckon — utility ability without AttackData; Back — submenu exit).
    /// AttackData references are resolved at rebuild time inside <see cref="RebuildSubMenu"/>
    /// and stored directly in <c>_subMenuAttacksData</c>; this method is just a list read.
    /// </summary>
    private AttackData GetSubMenuAttack(int index) => _subMenuAttacksData[index];

    /// <summary>
    /// Returns whether a submenu option is currently selectable.
    /// Attack entries with MpCost > 0 are disabled when the player lacks MP.
    /// "Back" is always enabled.
    /// </summary>
    private bool IsSubMenuOptionEnabled(int index)
    {
        var player = _activePlayer;  // MP affordability is per-active-player

        // Beckon — utility ability with a fixed MP cost (no AttackData backing).
        // Also disabled when there's nothing to draw out: no learnable move, or this
        // specific learnable has already been absorbed (per-move-type gating).
        if (_subMenuLabelsData[index] == "Beckon")
        {
            if (player.CurrentMp < BeckonMpCost) return false;
            if (EnemyData?.LearnableAttack == null) return false;
            if (_absorbedMoves.Contains(EnemyData.LearnableAttack)) return false;
            return true;
        }

        var attack = GetSubMenuAttack(index);
        if (attack == null) return true;  // Back
        if (attack.MpCost > 0 && player.CurrentMp < attack.MpCost) return false;
        return true;
    }

    private void RefreshSubMenuLabels()
    {
        for (int i = 0; i < _subMenuLabels.Length; i++)
        {
            bool selected = (i == _subMenuIndex);
            bool enabled  = IsSubMenuOptionEnabled(i);

            var attack = GetSubMenuAttack(i);
            string label = _subMenuLabelsData[i];
            if (attack != null && attack.MpCost > 0)
                label += $" ({attack.MpCost} MP)";
            else if (label == "Beckon")
                label += $" ({BeckonMpCost} MP)";

            string prefix = (selected && enabled) ? "▶ " : "  ";
            _subMenuLabels[i].Text = prefix + label;

            Color baseColor = _subMenuCategoriesData[i] switch
            {
                AttackCategory.Physical => ColorCategoryPhysical,
                AttackCategory.Magic    => ColorCategoryMagic,
                _                       => ColorMenuNormal,
            };

            _subMenuLabels[i].Modulate = enabled
                ? (selected ? ColorMenuSelected : baseColor)
                : ColorMenuDisabled;
        }
    }

    private void RefreshItemMenuLabels()
    {
        for (int i = 0; i < _itemMenuLabels.Length; i++)
        {
            bool selected = (i == _itemMenuIndex);
            string prefix = selected ? "▶ " : "  ";
            _itemMenuLabels[i].Text     = prefix + _itemMenuLabelsData[i];
            _itemMenuLabels[i].Modulate = selected ? ColorMenuSelected : ColorMenuNormal;
        }
    }
}
