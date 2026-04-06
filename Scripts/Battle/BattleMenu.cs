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

    private static readonly string[] MenuOptionLabels  = { "Attack", "Absorbed Moves" };
    private static readonly bool[]   MenuOptionEnabled = { true,     true             };

    // Absorbed Moves submenu — absorbed attack entries followed by "Back".
    // SubMenuOptionCategories mirrors the labels array; null means no category (Back, etc.).
    private static readonly string[]          SubMenuOptionLabels     = { "Combo Strike", "Comet", "Back" };
    private static readonly bool[]            SubMenuOptionEnabled    = { true,           true,    true   };
    private static readonly AttackCategory?[] SubMenuOptionCategories = { AttackCategory.Physical, AttackCategory.Magic, null };

    private static readonly Color ColorMenuSelected      = new Color(1.00f, 0.90f, 0.20f, 1.00f);  // yellow — selected item
    private static readonly Color ColorMenuNormal        = new Color(1.00f, 1.00f, 1.00f, 1.00f);  // white  — unselected, no category
    private static readonly Color ColorMenuDisabled      = new Color(0.45f, 0.45f, 0.45f, 1.00f);  // grey   — disabled item
    private static readonly Color ColorCategoryPhysical  = new Color(1.00f, 0.50f, 0.00f, 1.00f);  // orange — Physical attacks in submenu
    private static readonly Color ColorCategoryMagic     = new Color(0.60f, 0.30f, 1.00f, 1.00f);  // purple — Magic attacks in submenu

    private bool           _inSubMenu;      // true while the Absorbed Moves submenu is open
    private int            _menuIndex;
    private int            _subMenuIndex;
    private CanvasLayer    _menuLayer;
    private PanelContainer _mainMenuPanel;
    private PanelContainer _subMenuPanel;
    private Label[]        _menuLabels;
    private Label[]        _subMenuLabels;

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
            case 0: HideMenu(); _isComboAttack = true; BeginPlayerAttack(); break;   // Combo Strike
            case 1: HideMenu(); BeginPlayerMagicAttack(); break;                     // Comet
            case 2: ShowMenu(); break;                                               // Back
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
            _subMenuLabels[i].Text = prefix + SubMenuOptionLabels[i];

            // Unselected enabled items use their category color when set; white otherwise.
            // Selected items always use yellow regardless of category.
            Color baseColor = SubMenuOptionCategories[i] switch
            {
                AttackCategory.Physical => ColorCategoryPhysical,
                AttackCategory.Magic    => ColorCategoryMagic,
                _                       => ColorMenuNormal,  // null (Back, future no-category items)
            };

            _subMenuLabels[i].Modulate = enabled
                ? (selected ? ColorMenuSelected : baseColor)
                : ColorMenuDisabled;
        }
    }
}
