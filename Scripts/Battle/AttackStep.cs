using Godot;

/// <summary>
/// One step in an attack sequence — a single animation play paired with one or more
/// timing circle prompts.
///
/// One step = one animation play. ImpactFrames lists every frame within that animation
/// at which a hit lands. Each entry produces one independent timing circle, staggered
/// so each circle closes exactly when its impact frame plays:
///
///   animationStartDelay = circleCloseDuration - (ImpactFrames[0] / Fps)
///   circleSpawnDelay[i] = (ImpactFrames[i] - ImpactFrames[0]) / Fps
///
/// All circles in a step share the same CircleType.
///
/// For a single-hit step, ImpactFrames has one entry and behaves identically to the
/// old single ImpactFrame design.
///
/// Bouncing animation replay:
///   For Bouncing steps, BattleSystem subscribes to TimingPrompt.PassEvaluated on
///   circle 0 and replays SpawnEffectSprite after each outward pass so the animation
///   stays in sync with each inward approach. The animStartFrame compensation applies
///   on every replay so impact-frame sync is maintained across all passes.
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
    /// Zero-based indices of the frames on which hits land — one entry per timing circle.
    /// The animation plays once; each entry produces one circle timed so it closes
    /// exactly when the animation reaches that frame.
    /// ImpactFrames[0] determines when the animation starts relative to all circles;
    /// subsequent entries are staggered by (ImpactFrames[i] - ImpactFrames[0]) / Fps seconds.
    /// </summary>
    [Export] public int[] ImpactFrames = { 0 };

    /// <summary>
    /// The type of timing circle shown for every circle in this step.
    /// Standard and Slow each give exactly one input opportunity per circle.
    /// Bouncing gives a variable number of passes controlled by BounceCount below.
    /// </summary>
    [Export] public TimingPrompt.PromptType CircleType = TimingPrompt.PromptType.Standard;

    /// <summary>
    /// Number of additional bounces after the first inward pass for Bouncing circles.
    /// Total input opportunities = BounceCount + 1.
    /// Ignored for Standard and Slow circle types.
    /// Default of 2 gives three passes total (first approach + two bounces).
    /// </summary>
    [Export] public int BounceCount = 2;

    /// <summary>
    /// Controls when this step starts relative to the previous step's last circle resolving.
    ///
    /// Positive — this step starts N milliseconds AFTER the previous step's last circle resolves.
    ///            Use for a deliberate pause between animations (e.g. 300 for a short breath).
    ///
    /// Zero     — this step starts immediately when the previous step's last circle resolves.
    ///
    /// Negative — this step starts N milliseconds BEFORE the previous step's last circle resolves.
    ///            Steps overlap: this step's animation and circles are live while the previous
    ///            step's final circle(s) are still closing. Use for fast chained combos or
    ///            simultaneous multi-animation hits.
    ///            Clamped to 0 if the overlap would push the start before the sequence began.
    ///
    /// Ignored on step 0 — the first step always starts immediately.
    /// </summary>
    [Export] public int StartOffsetMs = 0;

    /// <summary>
    /// When true, the effect sprite is mirrored horizontally.
    /// Allows the same spritesheet to serve attacks from either side of the screen.
    /// </summary>
    [Export] public bool FlipH = false;

    /// <summary>
    /// Uniform scale applied to the effect AnimatedSprite2D.
    /// Default (3, 3) matches the standard 3× world-space upscale used for all effect sheets.
    /// Adjust per-step to shrink or enlarge specific animations without editing the spritesheet.
    /// </summary>
    [Export] public Vector2 Scale = new Vector2(3, 3);

    /// <summary>
    /// World-space offset applied to the effect position when the enemy uses this attack
    /// against the player. Use this to fine-tune where the visual lands on the defender.
    /// </summary>
    [Export] public Vector2 Offset = Vector2.Zero;

    /// <summary>
    /// World-space offset applied to the effect position when the player uses this attack
    /// against the enemy. Mirrors Offset but targets the enemy's position instead.
    /// Defaults to Vector2.Zero; adjust per-step to position the visual on the enemy.
    /// </summary>
    [Export] public Vector2 PlayerOffset = Vector2.Zero;

    /// <summary>
    /// Per-step damage override. When greater than zero, this value is used instead of
    /// AttackData.BaseDamage for damage calculations on this step's passes.
    /// When 0 (default), the parent AttackData.BaseDamage applies as normal.
    /// </summary>
    [Export] public int BaseDamageOverride = 0;

    /// <summary>
    /// Extra hold time in milliseconds after this step's animation completes before the
    /// sequence or teardown proceeds.
    ///
    /// For effect-sprite steps (SpritesheetPath non-empty): the sprite stays visible for
    /// this duration after its animation finishes, then frees itself.
    ///
    /// For melee hop-in steps (IsHopIn = true on AttackData): BattleTest waits this long
    /// after the attacker's animation finishes before calling PlayTeardown. Use to let
    /// the impact pose read before the enemy retreats.
    ///
    /// Default 0 — no hold; sprite frees / teardown begins immediately on animation end.
    /// </summary>
    [Export] public int PostAnimationDelayMs = 0;

    /// <summary>
    /// Audio files to play at specific animation frames during this step.
    /// SoundEffects[i] pairs with SoundTriggerFrames[i] — each pair defines a
    /// res:// path to a sound file and the zero-based animation frame it plays on.
    /// Empty arrays mean no sounds for this step.
    /// </summary>
    [Export] public string[] SoundEffects = System.Array.Empty<string>();

    /// <summary>
    /// Zero-based animation frame indices at which each corresponding SoundEffects entry plays.
    /// Must have the same length as SoundEffects.
    /// </summary>
    [Export] public int[] SoundTriggerFrames = System.Array.Empty<int>();
}
