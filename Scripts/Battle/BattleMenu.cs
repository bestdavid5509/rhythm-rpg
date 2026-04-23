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

    private static readonly string[] MenuOptionLabels  = { "Attack", "Absorbed Moves", "Defend", "Items" };
    private static readonly bool[]   MenuOptionEnabled = { true,     true,             true,     true   };

    // Absorbed Moves submenu — absorbed attack entries followed by "Back".
    // Built dynamically; RebuildSubMenu() appends absorbed moves at runtime.
    // TODO: submenu population should eventually be driven by the player's persistent move list from the character system
    private System.Collections.Generic.List<string>          _subMenuLabelsData;
    private System.Collections.Generic.List<AttackCategory?> _subMenuCategoriesData;
    private System.Collections.Generic.List<AttackData>      _subMenuAttacksData;  // null for Back

    // Items submenu — items followed by "Back". Built dynamically based on item counts.
    private System.Collections.Generic.List<string> _itemMenuLabelsData;

    // MP cost for Beckon (utility ability with no AttackData backing).
    private const int BeckonMpCost = 10;

    private static readonly Color ColorMenuSelected      = new Color(1.00f, 0.90f, 0.20f, 1.00f);  // yellow — selected item
    private static readonly Color ColorMenuNormal        = new Color(1.00f, 1.00f, 1.00f, 1.00f);  // white  — unselected, no category
    private static readonly Color ColorMenuDisabled      = new Color(0.45f, 0.45f, 0.45f, 1.00f);  // grey   — disabled item
    private static readonly Color ColorCategoryPhysical  = new Color(1.00f, 0.50f, 0.00f, 1.00f);  // orange — Physical attacks in submenu
    private static readonly Color ColorCategoryMagic     = new Color(0.60f, 0.30f, 1.00f, 1.00f);  // purple — Magic attacks in submenu

    private bool            _inSubMenu;      // true while the Absorbed Moves submenu is open
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

    private void BuildMenu()
    {
        _menuLayer      = new CanvasLayer();
        _menuLayer.Name = "BattleMenu";
        AddChild(_menuLayer);

        // Main menu — Attack / Absorbed Moves / Defend / Items.
        _mainMenuPanel = MakeMenuPanel(_menuLayer, out _mainMenuVBox);
        _menuLabels    = new Label[MenuOptionLabels.Length];
        for (int i = 0; i < MenuOptionLabels.Length; i++)
        {
            if (i > 0) AddMenuDivider(_mainMenuVBox);
            var label = new Label();
            StyleLabel(label);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            _mainMenuVBox.AddChild(label);
            _menuLabels[i] = label;
        }

        // Absorbed Moves submenu — built dynamically from _subMenuLabelsData.
        _subMenuPanel = MakeMenuPanel(_menuLayer, out _subMenuVBox);
        InitSubMenuData();
        PopulateSubMenuPanel();

        // Items submenu — built dynamically from _itemMenuLabelsData.
        _itemMenuPanel = MakeMenuPanel(_menuLayer, out _itemMenuVBox);
        RebuildItemMenu();

        _menuLayer.Visible = false;

        // Position menu panels directly above the player panel once its size is known.
        // Runs deferred so the PanelContainer layout pass has completed; also re-fires on resize.
        if (_playerPanel != null)
        {
            _playerPanel.Resized += PositionMenuPanelsAbovePlayerPanel;
            CallDeferred(MethodName.PositionMenuPanelsAbovePlayerPanel);
        }
    }

    /// <summary>
    /// Builds a layered Kenney 9-slice panel anchored to the bottom-left of the viewport.
    /// The panel's OffsetBottom is set by <see cref="PositionMenuPanelsAbovePlayerPanel"/>
    /// once the player panel's post-layout height is known. Every menu variant (main,
    /// absorbed moves, items) uses this same position — only one is visible at a time.
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
        // OffsetBottom populated by PositionMenuPanelsAbovePlayerPanel — default here keeps
        // the panel visible if the player panel is unavailable for some reason.
        p.OffsetBottom   = -(UiEdgeMargin + 100f + UiPanelSpacing);
        layer.AddChild(p);
        return p;
    }

    /// <summary>
    /// Repositions all menu panels so their bottom edge sits exactly
    /// (UiPanelSpacing + UiEdgeMargin) below the player panel's top edge, using the
    /// player panel's actual post-layout height.
    /// </summary>
    private void PositionMenuPanelsAbovePlayerPanel()
    {
        if (_playerPanel == null) return;
        float playerHeight = _playerPanel.Size.Y;
        float offsetBottom = -(UiEdgeMargin + playerHeight + UiPanelSpacing);
        if (_mainMenuPanel != null) _mainMenuPanel.OffsetBottom = offsetBottom;
        if (_subMenuPanel  != null) _subMenuPanel .OffsetBottom = offsetBottom;
        if (_itemMenuPanel != null) _itemMenuPanel.OffsetBottom = offsetBottom;
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

    private void ShowMenu()
    {
        _state                        = BattleState.PlayerMenu;
        _inputLocked                  = false;  // Unlock input — player can interact with menu.
        _playerParty[0].IsDefending   = false;  // Defend only lasts one enemy turn.
        _inSubMenu                    = false;
        _inItemMenu                   = false;
        _menuIndex                    = 0;
        _mainMenuPanel.Visible        = true;
        _subMenuPanel.Visible         = false;
        _itemMenuPanel.Visible        = false;
        _menuLayer.Visible            = true;
        RefreshMenuLabels();
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
        _inSubMenu              = true;
        _inItemMenu             = false;
        _subMenuIndex           = 0;
        _mainMenuPanel.Visible  = false;
        _subMenuPanel.Visible   = true;
        _itemMenuPanel.Visible  = false;
        RefreshSubMenuLabels();
        GD.Print("[BattleTest] Absorbed Moves submenu shown.");
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

        _itemMenuLabels = new Label[_itemMenuLabelsData.Count];
        for (int i = 0; i < _itemMenuLabelsData.Count; i++)
        {
            if (i > 0) AddMenuDivider(_itemMenuVBox);
            var label = new Label();
            StyleLabel(label);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            _itemMenuVBox.AddChild(label);
            _itemMenuLabels[i] = label;
        }
    }

    private void HideMenu()
    {
        _menuLayer.Visible = false;
    }

    /// <summary>
    /// Populates the base submenu data lists (Combo Strike, Comet, Back).
    /// Called once from BuildMenu.
    /// </summary>
    private void InitSubMenuData()
    {
        _subMenuLabelsData     = new System.Collections.Generic.List<string>          { "Combo Strike", "Beckon", "Comet", "Cure", "Back" };
        _subMenuCategoriesData = new System.Collections.Generic.List<AttackCategory?> { AttackCategory.Physical, AttackCategory.Magic, AttackCategory.Magic, AttackCategory.Magic, null };
        _subMenuAttacksData    = new System.Collections.Generic.List<AttackData>      { null, null, null, null, null };  // resolved in GetSubMenuAttack
    }

    /// <summary>
    /// Creates Label nodes in the submenu panel to match _subMenuLabelsData, with Kenney
    /// divider TextureRects between entries.
    /// </summary>
    private void PopulateSubMenuPanel()
    {
        // Clear existing labels and dividers.
        foreach (var child in _subMenuVBox.GetChildren())
            child.QueueFree();

        _subMenuLabels = new Label[_subMenuLabelsData.Count];
        for (int i = 0; i < _subMenuLabelsData.Count; i++)
        {
            if (i > 0) AddMenuDivider(_subMenuVBox);
            var label = new Label();
            StyleLabel(label);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            _subMenuVBox.AddChild(label);
            _subMenuLabels[i] = label;
        }
    }

    /// <summary>
    /// Rebuilds the Absorbed Moves submenu to include the just-absorbed move.
    /// Called from TryTriggerAbsorption when _absorbedMoveAttack is loaded.
    /// </summary>
    private void RebuildSubMenu()
    {
        // Insert the absorbed move before "Back" (last entry).
        int backIndex = _subMenuLabelsData.Count - 1;
        string label = !string.IsNullOrEmpty(_absorbedMoveAttack.DisplayName)
            ? _absorbedMoveAttack.DisplayName
            : "Absorbed Move";
        _subMenuLabelsData.Insert(backIndex, label);
        _subMenuCategoriesData.Insert(backIndex, _absorbedMoveAttack.Category);
        _subMenuAttacksData.Insert(backIndex, _absorbedMoveAttack);
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
            EtherCount--;
            HideMenu();
            UseEtherItem();
        }
    }

    private void ConfirmMenuSelection()
    {
        if (!MenuOptionEnabled[_menuIndex]) return;
        GD.Print($"[BattleTest] Player selects: {MenuOptionLabels[_menuIndex]}.");
        switch (_menuIndex)
        {
            case 0: HideMenu(); _isComboAttack = false; BeginPlayerAttack(); break;
            case 1: ShowSubMenu(); break;   // Absorbed Moves
            case 2:                         // Defend — halve miss damage this enemy turn
                _playerParty[0].IsDefending = true;  // single defender in the current UI
                GD.Print("[BattleTest] Player defending — incoming miss damage halved this turn.");
                HideMenu();
                BeginEnemyAttack();
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

        // Beckon — utility action, hands off to enemy turn immediately.
        if (label == "Beckon")
        {
            HideMenu();
            PerformBeckon();
            return;
        }

        var attack   = GetSubMenuAttack(_subMenuIndex);
        var category = _subMenuCategoriesData[_subMenuIndex];
        var player   = _playerParty[0];  // single MP spender in the current UI

        if (category == AttackCategory.Physical)
        {
            // Physical moves (Combo Strike) — combo attack flow.
            if (attack != null && attack.MpCost > 0)
                player.CurrentMp -= attack.MpCost;
            HideMenu(); _isComboAttack = true; BeginPlayerAttack();
        }
        else if (category == AttackCategory.Magic)
        {
            // Magic moves (Comet, Comet Barrage, Cure, etc.) — magic attack flow.
            if (attack != null)
                player.CurrentMp -= attack.MpCost;
            _activeMagicAttack = attack;
            HideMenu(); BeginPlayerMagicAttack();
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
    /// Returns the AttackData associated with a submenu index, or null for non-attack entries (Back).
    /// Indices 0 and 1 are the hardcoded base moves; further entries come from _subMenuAttacksData.
    /// </summary>
    private AttackData GetSubMenuAttack(int index)
    {
        // Back (last entry) has no attack.
        if (index == _subMenuLabelsData.Count - 1) return null;

        // Base hardcoded moves.
        if (index == 0) return _playerComboStrike;
        if (index == 1) return null;  // Beckon — utility, no attack data
        if (index == 2) return _playerMagicAttack;
        if (index == 3) return _playerCureAttack;

        // Dynamically added absorbed moves.
        return _subMenuAttacksData[index];
    }

    /// <summary>
    /// Returns whether a submenu option is currently selectable.
    /// Attack entries with MpCost > 0 are disabled when the player lacks MP.
    /// "Back" is always enabled.
    /// </summary>
    private bool IsSubMenuOptionEnabled(int index)
    {
        var player = _playerParty[0];  // single MP spender in the current UI

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
