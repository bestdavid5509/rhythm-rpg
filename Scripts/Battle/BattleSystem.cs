using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Core battle system — manages attack sequences driven by AttackData resources.
///
/// An attack sequence is an ordered list of AttackStep objects. For each step:
///   1. One AnimatedSprite2D effect is spawned and plays the full animation once.
///      Its start is delayed so ImpactFrames[0] lands exactly when circle 0 closes:
///        animationStartDelay = circleCloseDuration - (ImpactFrames[0] / fps)
///   2. One timing circle is spawned per entry in ImpactFrames, each staggered so
///      it closes exactly when the animation reaches its impact frame:
///        circleSpawnDelay[i] = (ImpactFrames[i] - ImpactFrames[0]) / fps
///   3. The next step's start is scheduled at RunStep time using StartOffsetMs:
///        lastCircleResolveTime = (ImpactFrames[last] - ImpactFrames[0]) / fps + circleCloseDuration
///        nextStepDelay = max(0, lastCircleResolveTime + nextStep.StartOffsetMs / 1000)
///      Positive StartOffsetMs → gap after last circle resolves.
///      Zero StartOffsetMs     → next step starts the instant last circle resolves.
///      Negative StartOffsetMs → next step starts before last circle resolves (concurrent).
///
/// SequenceCompleted fires when every circle across ALL steps has resolved.
/// _totalPromptsRemaining tracks this count; it is decremented on each circle completion
/// regardless of which step owned it.
///
/// Signals:
///   StepPassEvaluated — re-emitted from each step's TimingPrompt.PassEvaluated.
///                       stepIndex identifies which step in the sequence fired.
///   SequenceCompleted — emitted when every circle in the sequence has resolved.
///
/// The battle lifecycle methods (StartBattle, phase management, parry/miss outcomes,
/// and absorbed moves) are stubbed here for future implementation.
/// </summary>
public partial class BattleSystem : Node
{
    // =========================================================================
    // Signals
    // =========================================================================

    /// <summary>
    /// Re-emitted from the active step's TimingPrompt.PassEvaluated.
    /// Fires once per inward pass: once for Standard/Slow, multiple times for Bouncing.
    /// </summary>
    [Signal] public delegate void StepPassEvaluatedEventHandler(int result, int passIndex, int stepIndex);

    /// <summary>Emitted when every step in the current sequence has resolved.</summary>
    [Signal] public delegate void SequenceCompletedEventHandler();

    // =========================================================================
    // Sequence runner state
    // =========================================================================

    // Test attack: hardcoded for visual verification in BattleTest.
    // Replace with dynamic assignment when the full battle system drives attack selection.
    private const string TestAttackPath = "res://Resources/Attacks/red_sword_combo_attack.tres";

    private PackedScene              _promptScene;
    private AttackData               _currentAttack;          // the attack currently being executed
    private Node2D                   _spawnParent;            // node that owns spawned prompts and sprites
    private Vector2                  _defenderCenter;         // world-space center of the defender — effect origin
    private Vector2                  _promptPosition;         // world-space position for circle prompts
    private int                      _totalPromptsRemaining;  // total circles across all steps; 0 → SequenceCompleted
    private float                    _effectOffsetY;          // per-sequence Y nudge passed in from BattleTest

    // =========================================================================
    // Legacy battle state — used by stub methods below
    // =========================================================================

    private bool         _inBattle;
    private bool         _isOffensive;
    private int          _consecutiveHits;
    private AbsorbedMove _activeAbsorbedMove;
    private int          _currentPhase;

    // =========================================================================
    // Lifecycle
    // =========================================================================

    public override void _Ready()
    {
        _promptScene   = GD.Load<PackedScene>("res://Scenes/Battle/TimingPrompt.tscn");
        _currentAttack = GD.Load<AttackData>(TestAttackPath);

        if (_currentAttack == null)
            GD.PrintErr($"[BattleSystem] Failed to load test attack: {TestAttackPath}");
        else
            GD.Print($"[BattleSystem] Loaded \"{TestAttackPath}\" — " +
                     $"{_currentAttack.Steps.Count} step(s), {_currentAttack.BaseDamage} base damage.");
    }

    // =========================================================================
    // Sequence runner — public entry point
    // =========================================================================

    /// <summary>
    /// Executes the test attack sequence.
    /// All step scheduling is timer-driven from RunStep — steps may overlap when
    /// StartOffsetMs is negative. SequenceCompleted fires when every circle across
    /// all steps has resolved.
    /// </summary>
    /// <param name="parent">Node to add prompts and AnimatedSprite2Ds to.</param>
    /// <param name="defenderCenter">World-space center of the defender — effect spawn origin.</param>
    /// <param name="promptPosition">World-space position for circle prompts (combat midpoint).</param>
    /// <param name="effectOffsetY">Additional Y offset applied to every effect sprite this sequence (inspector-tunable from BattleTest).</param>
    public void StartSequence(Node2D parent, Vector2 defenderCenter, Vector2 promptPosition, float effectOffsetY = 0f)
    {
        if (_currentAttack == null || _currentAttack.Steps.Count == 0)
        {
            GD.PrintErr("[BattleSystem] StartSequence called but no valid attack data is loaded.");
            EmitSignal(SignalName.SequenceCompleted);
            return;
        }

        _spawnParent    = parent;
        _defenderCenter = defenderCenter;
        _promptPosition = promptPosition;
        _effectOffsetY  = effectOffsetY;

        // Count every circle across all steps so SequenceCompleted fires only after
        // the last circle of the last concurrent step resolves.
        _totalPromptsRemaining = 0;
        foreach (var s in _currentAttack.Steps)
            _totalPromptsRemaining += s.ImpactFrames.Length;

        RunStep(0);
    }

    // =========================================================================
    // Sequence runner — private step logic
    // =========================================================================

    /// <summary>
    /// Starts a single step. Immediately schedules the next step's timer (if any) based on
    /// StartOffsetMs — this decouples step advancement from circle resolution so steps can
    /// overlap when StartOffsetMs is negative.
    ///
    /// The full timing chain for step N:
    ///   lastCircleResolveTime = (ImpactFrames[last] - ImpactFrames[0]) / fps + circleCloseDuration
    ///   nextStepDelay = max(0, lastCircleResolveTime + nextStep.StartOffsetMs / 1000)
    ///
    /// SequenceCompleted is NOT emitted here — it is emitted by OnAnyCircleCompleted when
    /// _totalPromptsRemaining reaches zero.
    /// </summary>
    private void RunStep(int stepIndex)
    {
        var step        = _currentAttack.Steps[stepIndex];
        int circleCount = step.ImpactFrames.Length;
        int firstImpact = step.ImpactFrames[0];
        int lastImpact  = step.ImpactFrames[circleCount - 1];

        GD.Print($"[BattleSystem] Step {stepIndex + 1}/{_currentAttack.Steps.Count}: " +
                 $"CircleType={step.CircleType}  ImpactFrames=[{string.Join(", ", step.ImpactFrames)}]  " +
                 $"Fps={step.Fps}  Circles={circleCount}");

        // Synchronise the animation so its impact frame lands exactly when circle 0 closes.
        //
        //   rawDelay = circleCloseDuration - (ImpactFrames[0] / fps)
        //
        // Two cases:
        //
        //   rawDelay >= 0  → animation starts rawDelay seconds from now at frame 0.
        //                    By the time the circle closes it has played exactly
        //                    circleCloseDuration - rawDelay = ImpactFrames[0] / fps seconds
        //                    = ImpactFrames[0] frames. Correct.
        //
        //   rawDelay < 0   → ImpactFrames[0] / fps > circleCloseDuration.
        //                    At low fps the impact frame takes longer to reach than the circle
        //                    close time. Clamping to 0 and starting at frame 0 means only
        //                    circleCloseDuration * fps frames play before the circle closes —
        //                    fewer than ImpactFrames[0] — causing the animation to lag behind
        //                    the circle and appear out of sync.
        //
        //                    Fix: start the animation immediately (delay = 0) but skip ahead
        //                    to startFrame = round(|rawDelay| * fps) so the impact frame still
        //                    aligns with the circle close time. AnimatedSprite2D.Frame is set
        //                    after Play() to begin mid-animation.
        //
        float circleCloseDuration = TimingPrompt.DefaultDurationForType(step.CircleType);
        float rawDelay            = circleCloseDuration - firstImpact / step.Fps;
        float animStartDelay      = Mathf.Max(0f, rawDelay);
        int   animStartFrame      = 0;

        if (rawDelay < 0f)
        {
            animStartFrame = Mathf.RoundToInt(-rawDelay * step.Fps);
            GD.PrintErr($"[BattleSystem] WARNING: ImpactFrames[0]({firstImpact}) / Fps({step.Fps}) " +
                        $"= {firstImpact / step.Fps:F3}s exceeds circleCloseDuration({circleCloseDuration:F2}s). " +
                        $"Animation will start at frame {animStartFrame} to stay in sync. " +
                        $"Consider raising Fps or lowering ImpactFrames[0].");
        }

        GD.Print($"[BattleSystem]   circleCloseDuration={circleCloseDuration:F2}s  " +
                 $"impactFrame[0]/fps={firstImpact / step.Fps:F3}s  " +
                 $"rawDelay={rawDelay:F3}s  animStartDelay={animStartDelay:F3}s  " +
                 $"animStartFrame={animStartFrame}");

        // Schedule the next step's start immediately, based on when this step's last circle
        // is expected to resolve. The timer runs concurrently with this step's circles.
        //
        //   lastCircleResolveTime = time from now until this step's last circle closes
        //   nextStepDelay = lastCircleResolveTime + nextStep.StartOffsetMs / 1000
        //     > 0 → gap after last circle (positive StartOffsetMs)
        //     = 0 → next step starts exactly when last circle closes (zero StartOffsetMs)
        //     < 0 → next step starts before last circle closes (negative StartOffsetMs, clamped to 0)
        if (stepIndex + 1 < _currentAttack.Steps.Count)
        {
            var   nextStep              = _currentAttack.Steps[stepIndex + 1];
            float lastCircleResolveTime = (lastImpact - firstImpact) / step.Fps + circleCloseDuration;
            float nextStepDelay         = Mathf.Max(0f, lastCircleResolveTime + nextStep.StartOffsetMs / 1000f);
            int   nextStepIndex         = stepIndex + 1;

            GD.Print($"[BattleSystem]   Scheduling step {nextStepIndex + 1} in {nextStepDelay:F3}s " +
                     $"(lastCircleResolveTime={lastCircleResolveTime:F3}s  " +
                     $"StartOffsetMs={nextStep.StartOffsetMs}).");

            GetTree().CreateTimer(nextStepDelay).Timeout += () => RunStep(nextStepIndex);
        }

        GetTree().CreateTimer(animStartDelay).Timeout += () => SpawnEffectSprite(step, animStartFrame);

        // Spawn one circle per impact frame, staggered so each closes exactly when its
        // frame plays in the animation:
        //   circleSpawnDelay[i] = (ImpactFrames[i] - ImpactFrames[0]) / fps
        // Circle 0 always spawns at delay 0 (its close time is the anchor for the animation).
        for (int i = 0; i < circleCount; i++)
        {
            float spawnDelay = (step.ImpactFrames[i] - firstImpact) / step.Fps;
            int   capturedI  = i;

            void SpawnCircle()
            {
                var prompt      = _promptScene.Instantiate<TimingPrompt>();
                prompt.Type     = step.CircleType;
                prompt.AutoLoop = false;
                prompt.Position = _promptPosition;

                prompt.PassEvaluated += (result, passIndex) =>
                    EmitSignal(SignalName.StepPassEvaluated, result, passIndex, stepIndex);

                var capturedPrompt = prompt;
                prompt.PromptCompleted += result =>
                    OnAnyCircleCompleted(capturedPrompt, result, stepIndex);

                _spawnParent.AddChild(prompt);
                GD.Print($"[BattleSystem]   Circle {capturedI + 1}/{circleCount} spawned " +
                         $"(impact frame {step.ImpactFrames[capturedI]}, " +
                         $"spawnDelay={spawnDelay:F3}s).");
            }

            if (spawnDelay <= 0f)
                SpawnCircle();
            else
                GetTree().CreateTimer(spawnDelay).Timeout += SpawnCircle;
        }
    }

    /// <summary>
    /// Called when any circle in the sequence resolves, regardless of which step owns it.
    /// Schedules the circle's cleanup after the flash duration, then decrements the
    /// total-remaining counter. Emits SequenceCompleted when the last circle resolves.
    ///
    /// Step advancement is NOT handled here — it is timer-driven in RunStep so that
    /// steps with a negative StartOffsetMs can begin before the previous step's circles finish.
    /// </summary>
    private void OnAnyCircleCompleted(TimingPrompt prompt, int result, int stepIndex)
    {
        GD.Print($"[BattleSystem] Step {stepIndex + 1} circle resolved " +
                 $"({(TimingPrompt.InputResult)result}). " +
                 $"{_totalPromptsRemaining - 1} circle(s) remaining in sequence.");

        if (IsInstanceValid(prompt))
            GetTree().CreateTimer(TimingPrompt.FlashDuration).Timeout += prompt.QueueFree;

        _totalPromptsRemaining--;
        if (_totalPromptsRemaining > 0) return;

        GD.Print("[BattleSystem] All circles resolved — emitting SequenceCompleted.");
        EmitSignal(SignalName.SequenceCompleted);
    }

    /// <summary>
    /// Builds a SpriteFrames resource from the step's spritesheet and spawns an
    /// AnimatedSprite2D at the defender's center plus the step's Offset.
    /// The sprite queues itself free when its animation finishes.
    ///
    /// <paramref name="startFrame"/> is the frame index to begin playing from.
    /// 0 in the normal case (animation starts at the beginning); a positive value
    /// when the impact frame / fps exceeds circleCloseDuration — the animation skips
    /// ahead so the impact frame still lands when circle 0 closes.
    ///
    /// Frame grid is derived from texture dimensions divided by FrameWidth/FrameHeight.
    /// Any trailing empty cells (when total frames &lt; rows×cols) are included in the
    /// SpriteFrames but are never reached because the animation is non-looping.
    /// </summary>
    private void SpawnEffectSprite(AttackStep step, int startFrame = 0)
    {
        var texture = GD.Load<Texture2D>(step.SpritesheetPath);
        if (texture == null)
        {
            GD.PrintErr($"[BattleSystem] Could not load spritesheet: {step.SpritesheetPath}");
            return;
        }

        int cols = texture.GetWidth()  / step.FrameWidth;
        int rows = texture.GetHeight() / step.FrameHeight;

        var spriteFrames = new SpriteFrames();
        // Guard: SpriteFrames constructor already adds "default" in Godot 4.
        // Calling AddAnimation("default") again without removing it first throws
        // "SpriteFrames already has animation default". Remove it if present, then re-add
        // with the step's settings so the animation is always configured correctly.
        if (spriteFrames.HasAnimation("default")) spriteFrames.RemoveAnimation("default");
        spriteFrames.AddAnimation("default");
        spriteFrames.SetAnimationSpeed("default", step.Fps);
        spriteFrames.SetAnimationLoop("default", false);

        for (int row = 0; row < rows; row++)
        for (int col = 0; col < cols; col++)
        {
            var atlas    = new AtlasTexture();
            atlas.Atlas  = texture;
            atlas.Region = new Rect2(
                col * step.FrameWidth,  row * step.FrameHeight,
                step.FrameWidth,        step.FrameHeight);
            spriteFrames.AddFrame("default", atlas);
        }

        // Floor-anchored positioning: center Y = FloorY - (frameHeight * scale * 0.5) so the
        // bottom of the effect frame lands on the ground line. step.Offset applies on top as
        // a per-step fine adjustment.
        const float FloorY      = 750f;
        const float EffectScale = 3f;
        float       centerY     = FloorY - step.FrameHeight * EffectScale * 0.5f;

        var sprite          = new AnimatedSprite2D();
        sprite.SpriteFrames = spriteFrames;
        sprite.FlipH        = step.FlipH;
        sprite.Scale        = new Vector2(EffectScale, EffectScale);
        sprite.Position     = new Vector2(_defenderCenter.X, centerY + _effectOffsetY) + step.Offset;
        // Use an explicit named delegate so the handler can disconnect itself before
        // calling QueueFree. The direct `+= sprite.QueueFree` pattern causes Godot's
        // automatic signal cleanup (which runs when the node is freed) to attempt a
        // second disconnect of the same connection, producing "Attempt to disconnect a
        // nonexistent connection". Disconnecting explicitly here — guarded with IsConnected —
        // leaves nothing for Godot's cleanup to find.
        Action onFinished = null;
        onFinished = () =>
        {
            var callable = Callable.From(onFinished);
            if (sprite.IsConnected(AnimatedSprite2D.SignalName.AnimationFinished, callable))
                sprite.Disconnect(AnimatedSprite2D.SignalName.AnimationFinished, callable);
            sprite.QueueFree();
        };
        sprite.AnimationFinished += onFinished;

        _spawnParent.AddChild(sprite);
        sprite.Play("default");

        // Skip ahead to startFrame when the raw animation delay was negative —
        // the animation needed to begin "mid-flight" to keep its impact frame aligned
        // with the circle close time. Frame must be set after Play() because Play()
        // resets Frame to 0 in Godot 4.
        int totalFrames = cols * rows;
        int clampedStart = Mathf.Clamp(startFrame, 0, Mathf.Max(0, totalFrames - 1));
        if (clampedStart > 0)
            sprite.Frame = clampedStart;

        GD.Print($"[BattleSystem]   Spawned effect ({cols}×{rows} grid, {totalFrames} frames) " +
                 $"startFrame={clampedStart}  Fps={step.Fps}  at {sprite.Position}.");
    }

    // =========================================================================
    // Battle lifecycle — stubs for future implementation
    // =========================================================================

    /// <summary>
    /// Initialises and starts a new battle encounter.
    /// Sets up combatants, resets state, and begins the first prompt sequence.
    /// </summary>
    public void StartBattle() { }

    // =========================================================================
    // Battle phases
    // =========================================================================

    /// <summary>
    /// Evaluates whether the conditions for a phase transition have been met
    /// and triggers one if so. Called at appropriate points in the battle loop.
    /// The opening boss moves to Phase 2 on survival, not on an HP threshold.
    /// </summary>
    private void CheckPhaseTransition() { }

    /// <summary>
    /// Transitions the battle to the specified phase.
    /// Phase 2 of the opening boss introduces the bouncing circle mechanic
    /// and signals this via music and dialogue.
    /// </summary>
    private void TransitionToPhase(int phase) { }

    // =========================================================================
    // Timing prompts
    // =========================================================================

    /// <summary>
    /// Spawns a single timing prompt. Pass bounceCount &gt; 0 for a bouncing variant.
    /// </summary>
    private void SpawnTimingPrompt(int bounceCount = 0) { }

    /// <summary>
    /// Handles a timing prompt that has bounced outward and is closing again.
    /// First introduced in the opening boss Phase 2.
    /// </summary>
    private void HandleBouncingPrompt(int bouncesRemaining) { }

    // =========================================================================
    // Input evaluation
    // =========================================================================

    /// <summary>
    /// Called when the player presses the input button.
    /// Routes to a hit or miss outcome based on current prompt position.
    /// </summary>
    public void EvaluatePlayerInput() { }

    // =========================================================================
    // Parry / miss outcomes
    // =========================================================================

    /// <summary>
    /// Called when all inputs in a sequence are hit without a miss.
    /// Defensive: negates damage and triggers a counter attack.
    /// Absorb context: move is added permanently to the player's library.
    /// </summary>
    private void HandlePerfectParry() { }

    /// <summary>
    /// Called when the player misses a timing prompt.
    /// Defensive: enemy attack lands and deals damage to the player.
    /// Offensive: active absorbed move ends; damage is totalled from consecutive hits.
    /// </summary>
    private void HandleMissedInput() { }

    // =========================================================================
    // Absorbed moves
    // =========================================================================

    /// <summary>
    /// Begins an offensive sequence using an absorbed enemy move.
    /// Plays the same prompt sequence the enemy used. Continues until the player
    /// misses or all prompts are cleared. Damage scales with consecutive hits.
    /// </summary>
    public void TriggerAbsorbedMove(AbsorbedMove move) { }
}

/// <summary>
/// Represents a move that has been absorbed from a defeated enemy.
/// Stores the prompt sequence and damage data needed to replay the move offensively.
/// </summary>
public class AbsorbedMove
{
    public string           SourceEnemyName { get; set; }
    public TimingPromptData[] PromptSequence { get; set; }
    public float            DamagePerHit    { get; set; }
}

/// <summary>Data describing a single timing prompt within a move's sequence.</summary>
public class TimingPromptData
{
    public float CloseSpeed        { get; set; }
    public float HitWindowSeconds  { get; set; }
}
