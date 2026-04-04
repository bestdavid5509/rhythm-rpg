using Godot;

/// <summary>
/// A persistent shared target zone drawn at the combat midpoint during an attack sequence.
/// Renders the stationary hit-window band and target ring that all closing circles aim at.
///
/// Design rationale:
///   With multiple TimingPrompt circles active simultaneously (multi-hit steps, staggered
///   spawns), each prompt drawing its own target ring produced N stacked identical rings.
///   A single persistent node eliminates the stacking — all circles share one target.
///
/// Lifecycle (driven by BattleTest):
///   - BattleTest positions and shows this node when a sequence starts.
///   - BattleTest hides it when the sequence ends (SequenceCompleted / PromptCompleted).
///   - This node has no knowledge of individual circles; it just sits there.
///
/// Visual constants mirror TimingPrompt's TargetRadius, RingLineWidth, and zone colors
/// exactly so the ring is always in sync with where circles actually resolve.
/// </summary>
public partial class TargetZone : Node2D
{
    // Must match TimingPrompt constants exactly — if TimingPrompt's values change, update here too.
    private const float TargetRadius  = 28f;
    private const float RingLineWidth = 6f;

    private static readonly Color ColorTarget    = new Color(1.00f, 1.00f, 1.00f, 0.90f);  // white ring
    private static readonly Color ColorHitWindow = new Color(0.30f, 1.00f, 0.50f, 0.40f);  // green band

    public override void _Draw()
    {
        // Green band — fills the valid hit window so the player can see the target zone width.
        // Drawn at double RingLineWidth to create a filled band centered on TargetRadius.
        DrawArc(Vector2.Zero, TargetRadius, 0f, Mathf.Tau, 64, ColorHitWindow, RingLineWidth * 2f);
        // White ring — the precise target line all closing circles aim at.
        DrawArc(Vector2.Zero, TargetRadius, 0f, Mathf.Tau, 64, ColorTarget, RingLineWidth);
    }
}
