using Godot;

/// <summary>
/// Visual pointer shown above the currently-selected target during the
/// <c>SelectingTarget</c> combat state. Node2D in world space; positioned
/// by BattleTest via <see cref="SnapTo"/> when the selection changes.
///
/// <para>
/// Rendering is pure <c>_Draw</c> — a downward-pointing triangle, no sprite
/// assets. Anchored off <c>target.AnimSprite.GlobalPosition</c> for X and
/// off the rendered sprite height (<c>FrameHeight * AnimSpriteScale.Y</c>)
/// for Y. The earlier <c>PositionRect.Size.Y</c> basis read the ColorRect
/// anchor instead of the visible sprite, placing the pointer near sprite
/// center rather than above the head once multi-character density made
/// the discrepancy observable.
/// </para>
///
/// <para>
/// Target cycling (ui_left / ui_right) is wired in BattleTest's
/// HandleSelectingTargetInput (Phase 6 C9) and routes through the same
/// <see cref="SnapTo"/> entry point.
/// </para>
/// </summary>
public partial class TargetPointer : Node2D
{
    private const float TriangleHalfWidth = 18f;
    private const float TriangleHeight    = 22f;

    // Fraction of the rendered sprite height to offset above the sprite center.
    // Reads the per-combatant FrameHeight * AnimSpriteScale.Y so all sprite
    // sizes (80px Knight, 130/160px Warriors) get proportional placement
    // without a separate tuning constant per height. Tuned at 4v8 — adjust
    // if interactive verification shows the tip drifting too high or low.
    private const float HeadOffsetMultiplier = 0.5f;

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
        float offsetY = -renderedSpriteHeight * HeadOffsetMultiplier;
        GlobalPosition = new Vector2(
            sprite.GlobalPosition.X,
            sprite.GlobalPosition.Y + offsetY);
    }
}
