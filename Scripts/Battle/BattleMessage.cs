using Godot;

/// <summary>
/// Reusable battle message display — shows narrative text that fades in, holds, and fades out.
///
/// Uses <see cref="BattleTest.MakeLayeredPanel"/> so the fill + border NinePatchRects are
/// laid out by PanelContainer.fit_child_in_rect — the same mechanism that makes the status
/// panels render correctly. The outer PanelContainer is then anchored bottom-center manually.
///
/// Modulate is applied to the PanelContainer so border, fill, and text fade together.
/// </summary>
public class BattleMessage
{
    private readonly Label           _label;
    private readonly PanelContainer  _panel;
    private readonly Node            _tweenOwner;
    private Tween                    _tween;

    private const float PanelBottomInset = 100f;  // gap above viewport bottom
    private const float PanelHeight      = 80f;
    private const float PanelMinWidth    = BattleTest.PanelMinWidthMessage;  // 400f

    public BattleMessage(Node parent)
    {
        _tweenOwner = parent;

        var layer = new CanvasLayer();
        layer.Name  = "BattleMessageLayer";
        layer.Layer = 10;  // above status panels and menus
        parent.AddChild(layer);

        // Build the layered panel via the same helper the status panels use.
        _panel = BattleTest.MakeLayeredPanel(PanelMinWidth, out var content, minHeight: PanelHeight);

        // Anchor to bottom-center of the viewport. Overrides the defaults from MakeLayeredPanel.
        _panel.AnchorLeft     = 0.5f;
        _panel.AnchorRight    = 0.5f;
        _panel.AnchorTop      = 1f;
        _panel.AnchorBottom   = 1f;
        _panel.GrowHorizontal = Control.GrowDirection.Both;
        _panel.GrowVertical   = Control.GrowDirection.Begin;
        _panel.OffsetBottom   = -PanelBottomInset;
        _panel.MouseFilter    = Control.MouseFilterEnum.Ignore;
        _panel.Modulate       = new Color(1f, 1f, 1f, 0f);  // start invisible
        layer.AddChild(_panel);

        // Message label fills the content VBox so it centers vertically inside the panel.
        _label                      = new Label();
        _label.HorizontalAlignment  = HorizontalAlignment.Center;
        _label.VerticalAlignment    = VerticalAlignment.Center;
        _label.SizeFlagsHorizontal  = Control.SizeFlags.Fill;
        _label.SizeFlagsVertical    = Control.SizeFlags.ExpandFill;
        BattleTest.StyleLabel(_label, fontSize: 20);
        content.AddChild(_label);
    }

    /// <summary>
    /// Show a message with fade-in, hold, and fade-out. Cancels any in-progress message.
    /// </summary>
    public void Show(string text, float holdDuration = 2.0f,
                     float fadeInDuration = 0.3f, float fadeOutDuration = 0.5f)
    {
        _tween?.Kill();

        _label.Text     = text;
        _panel.Modulate = new Color(1f, 1f, 1f, 0f);

        _tween = _tweenOwner.CreateTween();
        _tween.TweenProperty(_panel, "modulate:a", 1.0f, fadeInDuration);
        _tween.TweenInterval(holdDuration);
        _tween.TweenProperty(_panel, "modulate:a", 0.0f, fadeOutDuration);
    }
}
