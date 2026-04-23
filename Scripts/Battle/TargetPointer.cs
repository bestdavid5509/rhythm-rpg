using Godot;

/// <summary>
/// Visual pointer shown above the currently-selected target during the
/// <c>SelectingTarget</c> combat state. Node2D in world space; positioned
/// by BattleTest via <see cref="SnapTo"/> when the selection changes.
///
/// <para>
/// Rendering is pure <c>_Draw</c> — a downward-pointing triangle, no sprite
/// assets. Anchored off <c>target.AnimSprite.GlobalPosition</c> rather than
/// <c>target.Origin + PositionRect.Size / 2f</c> to avoid the ColorRect
/// width-bias quirk documented in the Phase 3.5 / Cure self-heal notes.
/// </para>
///
/// <para>
/// Target cycling (ui_left / ui_right) is stubbed in BattleTest today; the
/// scaffolding phase will wire multi-target iteration through this same
/// SnapTo entry point without further pointer changes.
/// </para>
/// </summary>
public partial class TargetPointer : Node2D
{
    private const float TriangleHalfWidth = 18f;
    private const float TriangleHeight    = 22f;

    // Fraction of the target's frame height to offset above the sprite center.
    // Derives per-combatant so 80px-tall player and 130–160px-tall enemies get
    // proportional positioning without a separate tuning constant per height.
    private const float HeadOffsetMultiplier = 0.55f;

    private static readonly Color PointerColor = new(1f, 0.85f, 0.2f, 1f);

    public override void _Ready()
    {
        Visible = false;
        ZIndex  = 10;  // above combatants (0) and effect sprites (3)
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
        float offsetY = -target.PositionRect.Size.Y * HeadOffsetMultiplier;
        GlobalPosition = new Vector2(
            sprite.GlobalPosition.X,
            sprite.GlobalPosition.Y + offsetY);
    }
}
