using Godot;

/// <summary>
/// Reusable battle message display — shows narrative text that fades in, holds, and fades out.
/// Not a Node subclass; owns a Label added to a provided parent CanvasLayer.
/// </summary>
public class BattleMessage
{
    private readonly Label _label;
    private readonly Node  _tweenOwner;
    private Tween          _tween;

    public BattleMessage(Node parent)
    {
        _tweenOwner = parent;

        var layer = new CanvasLayer();
        layer.Name  = "BattleMessageLayer";
        layer.Layer = 10;  // above status panels and menus
        parent.AddChild(layer);

        _label = new Label();
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        _label.VerticalAlignment   = VerticalAlignment.Center;
        _label.AddThemeFontSizeOverride("font_size", 36);
        _label.AnchorLeft   = 0f;
        _label.AnchorRight  = 1f;
        _label.AnchorTop    = 0f;
        _label.AnchorBottom = 0f;
        _label.OffsetTop    = 100f;
        _label.OffsetBottom = 140f;
        _label.Modulate     = new Color(1f, 1f, 1f, 0f);  // start invisible
        layer.AddChild(_label);
    }

    /// <summary>
    /// Show a message with fade-in, hold, and fade-out. Cancels any in-progress message.
    /// </summary>
    public void Show(string text, float holdDuration = 2.0f,
                     float fadeInDuration = 0.3f, float fadeOutDuration = 0.5f)
    {
        // Cancel any running tween.
        _tween?.Kill();

        _label.Text     = text;
        _label.Modulate = new Color(1f, 1f, 1f, 0f);

        _tween = _tweenOwner.CreateTween();
        _tween.TweenProperty(_label, "modulate:a", 1.0f, fadeInDuration);
        _tween.TweenInterval(holdDuration);
        _tween.TweenProperty(_label, "modulate:a", 0.0f, fadeOutDuration);
    }
}
