using Godot;

/// <summary>
/// One step in an attack sequence — a single animation paired with one timing circle prompt.
///
/// Circle input counts:
///   Standard and Slow each represent exactly one input opportunity per step.
///   Bouncing represents a variable number of passes — the circle itself controls the
///   pass count via BounceCount. Do not assume any fixed input count for Bouncing steps.
///
/// NOTE — Bouncing animation replay:
///   A Bouncing step's animation will need to replay once per pass in future so the
///   visual stays in sync with each inward approach. This is not yet implemented.
///   When wiring up animation playback, hook into TimingPrompt.PassEvaluated to
///   restart the animation at the start of each new inward pass.
/// </summary>
[GlobalClass]
public partial class AttackStep : Resource
{
    /// <summary>
    /// res:// path to the spritesheet used for this step's animation.
    /// </summary>
    [Export] public string SpritesheetPath = "";

    /// <summary>Width of a single frame in the spritesheet, in pixels.</summary>
    [Export] public int FrameWidth = 64;

    /// <summary>Height of a single frame in the spritesheet, in pixels.</summary>
    [Export] public int FrameHeight = 64;

    /// <summary>Playback speed of the animation in frames per second.</summary>
    [Export] public float Fps = 12f;

    /// <summary>
    /// Zero-based index of the frame on which the hit lands — the frame that plays
    /// at the moment the timing circle reaches the target zone.
    /// </summary>
    [Export] public int ImpactFrame = 0;

    /// <summary>
    /// The type of timing circle shown for this step.
    /// Standard and Slow each give exactly one input opportunity.
    /// Bouncing gives a variable number of passes controlled by the circle's BounceCount.
    /// </summary>
    [Export] public TimingPrompt.PromptType CircleType = TimingPrompt.PromptType.Standard;

    /// <summary>
    /// Milliseconds to wait after the previous step resolves before this step begins.
    /// Zero means this step starts immediately when the previous one completes.
    /// </summary>
    [Export] public int DelayMs = 0;

    /// <summary>
    /// When true, the effect sprite is mirrored horizontally.
    /// Allows the same spritesheet to serve attacks from either side of the screen.
    /// </summary>
    [Export] public bool FlipH = false;

    /// <summary>
    /// World-space offset applied to the effect position relative to the target.
    /// Use this to fine-tune where the visual lands on the defender.
    /// </summary>
    [Export] public Vector2 Offset = Vector2.Zero;
}
