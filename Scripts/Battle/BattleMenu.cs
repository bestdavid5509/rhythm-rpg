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

    private static readonly string[] MenuOptionLabels  = { "Attack", "Absorbed Moves", "Items" };
    private static readonly bool[]   MenuOptionEnabled = { true,     true,             true   };

    // Absorbed Moves submenu — absorbed attack entries followed by "Back".
    // SubMenuOptionCategories mirrors the labels array; null means no category (Back, etc.).
    private static readonly string[]          SubMenuOptionBaseLabels = { "Combo Strike", "Comet", "Back" };
    private static readonly AttackCategory?[] SubMenuOptionCategories = { AttackCategory.Physical, AttackCategory.Magic, null };

    // Items submenu — items followed by "Back".
    private static readonly string[] ItemMenuOptionLabels = { "Ether (20 MP)", "Back" };

    private static readonly Color ColorMenuSelected      = new Color(1.00f, 0.90f, 0.20f, 1.00f);  // yellow — selected item
    private static readonly Color ColorMenuNormal        = new Color(1.00f, 1.00f, 1.00f, 1.00f);  // white  — unselected, no category
    private static readonly Color ColorMenuDisabled      = new Color(0.45f, 0.45f, 0.45f, 1.00f);  // grey   — disabled item
    private static readonly Color ColorCategoryPhysical  = new Color(1.00f, 0.50f, 0.00f, 1.00f);  // orange — Physical attacks in submenu
    private static readonly Color ColorCategoryMagic     = new Color(0.60f, 0.30f, 1.00f, 1.00f);  // purple — Magic attacks in submenu

    private bool           _inSubMenu;      // true while the Absorbed Moves submenu is open
    private bool           _inItemMenu;     // true while the Items submenu is open
    private int            _menuIndex;
    private int            _subMenuIndex;
    private int            _itemMenuIndex;
    private CanvasLayer    _menuLayer;
    private PanelContainer _mainMenuPanel;
    private PanelContainer _subMenuPanel;
    private PanelContainer _itemMenuPanel;
    private Label[]        _menuLabels;
    private Label[]        _subMenuLabels;
    private Label[]        _itemMenuLabels;

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

        // Absorbed Moves submenu — Combo Strike / Comet / Back.
        _subMenuPanel  = MakePanel(_menuLayer);
        _subMenuLabels = new Label[SubMenuOptionBaseLabels.Length];
        var subVBox    = _subMenuPanel.GetChild<VBoxContainer>(0);
        for (int i = 0; i < SubMenuOptionBaseLabels.Length; i++)
        {
            var label = new Label();
            label.AddThemeFontSizeOverride("font_size", 24);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            subVBox.AddChild(label);
            _subMenuLabels[i] = label;
        }

        // Items submenu — Ether / Back.
        _itemMenuPanel  = MakePanel(_menuLayer);
        _itemMenuLabels = new Label[ItemMenuOptionLabels.Length];
        var itemVBox    = _itemMenuPanel.GetChild<VBoxContainer>(0);
        for (int i = 0; i < ItemMenuOptionLabels.Length; i++)
        {
            var label = new Label();
            label.AddThemeFontSizeOverride("font_size", 24);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            itemVBox.AddChild(label);
            _itemMenuLabels[i] = label;
        }

        _menuLayer.Visible = false;
    }

    private void ShowMenu()
    {
        _state                  = BattleState.PlayerMenu;
        _inSubMenu              = false;
        _inItemMenu             = false;
        _menuIndex              = 0;
        _mainMenuPanel.Visible  = true;
        _subMenuPanel.Visible   = false;
        _itemMenuPanel.Visible  = false;
        _menuLayer.Visible      = true;
        RefreshMenuLabels();
        GD.Print("[BattleTest] Player menu shown.");
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
        RefreshItemMenuLabels();
        GD.Print("[BattleTest] Items submenu shown.");
    }

    private void HideMenu()
    {
        _menuLayer.Visible = false;
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
        int count = SubMenuOptionBaseLabels.Length;
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
        int count = ItemMenuOptionLabels.Length;
        _itemMenuIndex = (_itemMenuIndex + direction + count) % count;
        RefreshItemMenuLabels();
    }

    private void ConfirmItemMenuSelection()
    {
        GD.Print($"[BattleTest] Player selects item: {ItemMenuOptionLabels[_itemMenuIndex]}.");
        switch (_itemMenuIndex)
        {
            case 0:  // Ether — restore 20 MP, end turn
                RestoreMp(20);
                GD.Print($"[BattleTest] Ether used. MP: {_playerMp}/{PlayerMaxMp}");
                HideMenu();
                BeginEnemyAttack();
                break;
            case 1: ShowMenu(); break;  // Back
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
            case 2: ShowItemMenu(); break;  // Items
        }
    }

    private void ConfirmSubMenuSelection()
    {
        if (!IsSubMenuOptionEnabled(_subMenuIndex)) return;
        GD.Print($"[BattleTest] Player selects submenu: {SubMenuOptionBaseLabels[_subMenuIndex]}.");
        switch (_subMenuIndex)
        {
            case 0:  // Combo Strike (Physical, MpCost=0)
                if (_playerComboStrike != null && _playerComboStrike.MpCost > 0)
                    _playerMp -= _playerComboStrike.MpCost;
                HideMenu(); _isComboAttack = true; BeginPlayerAttack();
                break;
            case 1:  // Comet (Magic)
                if (_playerMagicAttack != null)
                    _playerMp -= _playerMagicAttack.MpCost;
                HideMenu(); BeginPlayerMagicAttack();
                break;
            case 2: ShowMenu(); break;  // Back
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
    /// </summary>
    private AttackData GetSubMenuAttack(int index)
    {
        return index switch
        {
            0 => _playerComboStrike,   // Combo Strike
            1 => _playerMagicAttack,   // Comet
            _ => null,
        };
    }

    /// <summary>
    /// Returns whether a submenu option is currently selectable.
    /// Attack entries with MpCost > 0 are disabled when the player lacks MP.
    /// "Back" is always enabled.
    /// </summary>
    private bool IsSubMenuOptionEnabled(int index)
    {
        var attack = GetSubMenuAttack(index);
        if (attack == null) return true;  // Back
        if (attack.MpCost > 0 && _playerMp < attack.MpCost) return false;
        return true;
    }

    private void RefreshSubMenuLabels()
    {
        for (int i = 0; i < _subMenuLabels.Length; i++)
        {
            bool selected = (i == _subMenuIndex);
            bool enabled  = IsSubMenuOptionEnabled(i);

            var attack = GetSubMenuAttack(i);
            string label = SubMenuOptionBaseLabels[i];
            if (attack != null && attack.MpCost > 0)
                label += $" ({attack.MpCost} MP)";

            string prefix = (selected && enabled) ? "▶ " : "  ";
            _subMenuLabels[i].Text = prefix + label;

            Color baseColor = SubMenuOptionCategories[i] switch
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
            _itemMenuLabels[i].Text     = prefix + ItemMenuOptionLabels[i];
            _itemMenuLabels[i].Modulate = selected ? ColorMenuSelected : ColorMenuNormal;
        }
    }
}
