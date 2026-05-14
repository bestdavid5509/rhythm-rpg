using Godot;

/// <summary>
/// Visual pointer shown above the currently-selected target during the
/// <c>SelectingTarget</c> combat state. Node2D in world space; positioned
/// by BattleTest via <see cref="SnapTo"/> when the selection changes.
///
/// <para>
/// Rendering is pure <c>_Draw</c> — a downward-pointing triangle, no sprite
/// assets. Anchored off <c>target.AnimSprite.GlobalPosition</c> for X and
/// off the rendered sprite top plus a fixed pixel margin for Y. Phase 6
/// C11.1 introduced a yellow per-sprite highlight (<c>target_amount</c> on
/// CombatantOverlay) as the primary "selected target" indicator; with the
/// highlight carrying the visual weight, the pointer is a secondary cue
/// and uses the simpler fixed-pixel margin formula so the tip sits at a
/// consistent distance above each sprite top regardless of sprite size
/// (80px Knight, 130/160px Warriors).
/// </para>
///
/// <para>
/// Target cycling (ui_left / ui_right) is wired in BattleTest's
/// HandleSelectingTargetInput (Phase 6 C9) and routes through the same
/// <see cref="SnapTo"/> entry point. Cycle handlers also drive the
/// per-sprite highlight via ApplyTargetHighlight / ClearTargetHighlight.
/// </para>
/// </summary>
public partial class TargetPointer : Node2D
{
    private const float TriangleHalfWidth = 18f;
    private const float TriangleHeight    = 22f;

    // Fixed pixel margin above the upper-third anchor (see SnapTo). Used in
    // addition to the per-combatant rendered sprite height
    // (FrameHeight * AnimSpriteScale.Y) so all sprite sizes get the same
    // visible above-anchor gap. Tunes the pointer's distance above the
    // upper-third when the pointer is re-enabled — the pointer is gated off
    // in C11.1 (see ShowTargetPointer in BattleTest.cs) but the constant is
    // preserved so re-enabling produces a sensible starting placement.
    private const float HeadMarginPixels = 12f;

    private static readonly Color PointerColor = new(1f, 0.85f, 0.2f, 1f);

    public override void _Ready()
    {
        Visible = false;
        ZIndex  = 30;  // selection-UI tier — above formation (max 14) + hop-in (15) + headroom for future formation expansion
    }

    public override void _Draw()
    {
        Vector2 topLeft  = new(-TriangleHalfWidth, -TriangleHeight);
        Vector2 topRight = new( TriangleHalfWidth, -TriangleHeight);
        Vector2 tip      = Vector2.Zero;
        DrawColoredPolygon(new[] { topLeft, topRight, tip }, PointerColor);
    }

    /// <summary>Snaps the pointer above <paramref name="target"/>'s rendered sprite.</summary>
    public void SnapTo(Combatant target)
    {
        var sprite = target.AnimSprite;
        float renderedSpriteHeight = target.FrameHeight * target.AnimSpriteScale.Y;
        // Tip anchors at a slight proportional distance above sprite center
        // (10% of the rendered height) plus HeadMarginPixels above that.
        // Empirically tuned against the current sprite roster: sprite frames
        // have transparent space above the character, so frame-top (0.5)
        // floats the pointer well above the visible head; the 0.1 multiplier
        // pulls the tip back toward sprite center where the visible character
        // body actually is. The constant scales mildly with sprite size so
        // bigger sprites (8 Sword Warrior at 480px rendered) get slightly more
        // headroom than smaller ones (Knight at 240px) without per-sprite
        // tuning. Per-sprite content-top authoring (a Combatant
        // ContentTopOffsetY field, hand-tuned per character) would give
        // pixel-precise above-head placement but is deferred. Pointer is
        // gated off in C11.1 anyway (ShowTargetPointer flag in BattleTest);
        // this formula tunes the starting placement for any future
        // re-enablement.
        float offsetY = -(renderedSpriteHeight * 0.1f + HeadMarginPixels);
        GlobalPosition = new Vector2(
            sprite.GlobalPosition.X,
            sprite.GlobalPosition.Y + offsetY);
    }
}
