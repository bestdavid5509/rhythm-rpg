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
    /// The ctx payload carries attacker/target/attack identity for the owning sequence.
    /// </summary>
    [Signal] public delegate void StepPassEvaluatedEventHandler(
        int result, int passIndex, int stepIndex, SequenceContext ctx);

    /// <summary>Emitted at the start of each step, before circles spawn or timers fire.</summary>
    [Signal] public delegate void StepStartedEventHandler(int stepIndex, SequenceContext ctx);

    /// <summary>Emitted when every step in the current sequence has resolved.</summary>
    [Signal] public delegate void SequenceCompletedEventHandler(SequenceContext ctx);

    // =========================================================================
    // Sequence runner state
    // =========================================================================

    private PackedScene              _promptScene;
    private SequenceContext          _sequenceContext;        // per-sequence payload — attacker/target/attack/id
    private Node2D                   _spawnParent;            // node that owns spawned prompts and sprites
    private Vector2                  _promptPosition;         // world-space position for circle prompts
    private int                      _totalPromptsRemaining;  // total circles across all steps; 0 → SequenceCompleted
    private bool                     _sequenceCancelled;      // set on Physical miss — prevents new steps from spawning
    private bool                     _sequenceActive;         // true while a sequence is running; prevents multi-emit of SequenceCompleted
    private int                      _lastStepRun = -1;       // index of the most recently started step
    private int                      _nextSequenceId;         // monotonic counter — assigned by NextSequenceId()

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
        _promptScene = GD.Load<PackedScene>("res://Scenes/Battle/TimingPrompt.tscn");
    }

    // =========================================================================
    // Sequence runner — public helpers for BattleTest
    // =========================================================================

    /// <summary>
    /// True when the active sequence's attack uses the hop-in melee path.
    /// Returns false outside of a running sequence.
    /// </summary>
    public bool CurrentAttackIsHopIn => _sequenceContext?.CurrentAttack?.IsHopIn ?? false;

    /// <summary>
    /// Returns the animation start delay for step 0 — the time between StartSequence and
    /// when BattleTest should play the attacker's melee animation so its impact frame lands
    /// exactly when circle 0 closes.
    ///
    ///   animStartDelay = max(0, circleCloseDuration - ImpactFrames[0] / Fps)
    ///
    /// Returns 0 if no sequence is active or the attack has no steps.
    /// </summary>
    public float ComputeFirstStepAnimDelay()
    {
        var attack = _sequenceContext?.CurrentAttack;
        if (attack == null || attack.Steps.Count == 0) return 0f;
        var   step                = attack.Steps[0];
        float circleCloseDuration = TimingPrompt.DefaultDurationForType(step.CircleType);
        float rawDelay            = circleCloseDuration - step.ImpactFrames[0] / step.Fps;
        return Mathf.Max(0f, rawDelay);
    }

    /// <summary>
    /// Returns step 0 of the active sequence's attack, or null if no sequence is active
    /// or the attack has no steps.
    /// </summary>
    public AttackStep GetFirstStep()
    {
        var attack = _sequenceContext?.CurrentAttack;
        return (attack != null && attack.Steps.Count > 0) ? attack.Steps[0] : null;
    }

    /// <summary>
    /// Returns the active sequence's attack, or null if no sequence is active.
    /// Kept alive so animation callbacks (OnEnemyAttackAnimFinished) can inspect the
    /// in-flight attack without needing the SequenceContext threaded through.
    /// </summary>
    public AttackData GetCurrentAttack() => _sequenceContext?.CurrentAttack;

    /// <summary>
    /// Returns the PostAnimationDelayMs for step 0 — used by BattleTest to hold the
    /// melee impact pose before calling PlayTeardown.
    /// Returns 0 if no sequence is active or the attack has no steps.
    /// </summary>
    public int GetFirstStepPostAnimDelayMs() =>
        GetFirstStep()?.PostAnimationDelayMs ?? 0;

    /// <summary>
    /// Returns the PostAnimationDelayMs for the last step in the active sequence's attack.
    /// Used for multi-step hop-in attacks where the final step controls the hold before retreat.
    /// Returns 0 if no sequence is active or the attack has no steps.
    /// </summary>
    public int GetLastStepPostAnimDelayMs()
    {
        var attack = _sequenceContext?.CurrentAttack;
        if (attack == null || attack.Steps.Count == 0) return 0;
        return attack.Steps[attack.Steps.Count - 1].PostAnimationDelayMs;
    }

    /// <summary>Returns the index of the most recently started step, or -1 if none.</summary>
    public int GetLastStepRun() => _lastStepRun;

    /// <summary>
    /// Returns the effective base damage for a given step of the active sequence's attack.
    /// Uses the step's BaseDamageOverride when set (> 0), otherwise falls back to AttackData.BaseDamage.
    /// </summary>
    public int GetStepBaseDamage(int stepIndex)
    {
        var attack = _sequenceContext?.CurrentAttack;
        if (attack == null) return 0;
        if (stepIndex < 0 || stepIndex >= attack.Steps.Count) return attack.BaseDamage;
        int over = attack.Steps[stepIndex].BaseDamageOverride;
        return over > 0 ? over : attack.BaseDamage;
    }

    /// <summary>
    /// Returns a fresh monotonic sequence ID. Callers assign this to
    /// <see cref="SequenceContext.SequenceId"/> when constructing a context so
    /// subscribers can use reference equality OR ID equality to identify a sequence.
    /// </summary>
    public int NextSequenceId() => _nextSequenceId++;

    // =========================================================================
    // Sequence runner — public entry point
    // =========================================================================

    /// <summary>
    /// Executes an attack sequence described by the supplied <see cref="SequenceContext"/>.
    /// All step scheduling is timer-driven from RunStep — steps may overlap when
    /// StartOffsetMs is negative. SequenceCompleted fires when every circle across
    /// all steps has resolved.
    ///
    /// <para>
    /// The defender center used for effect-sprite spawn is derived inside the runner
    /// from <c>ctx.Target</c>. Attacker side (for offset selection, FlipH, and
    /// cancel-on-miss) is derived from <c>ctx.Attacker.Side</c>. Subscribers receive
    /// the same <paramref name="ctx"/> instance on every signal emitted during this
    /// sequence so they can identify which sequence a signal belongs to.
    /// </para>
    /// </summary>
    /// <param name="parent">Node to add prompts and AnimatedSprite2Ds to.</param>
    /// <param name="ctx">Sequence context — attacker, target, attack data, monotonic ID.</param>
    /// <param name="promptPosition">World-space position for circle prompts (combat midpoint).</param>
    public void StartSequence(Node2D parent, SequenceContext ctx, Vector2 promptPosition)
    {
        if (ctx == null || ctx.CurrentAttack == null || ctx.CurrentAttack.Steps.Count == 0)
        {
            GD.PrintErr("[BattleSystem] StartSequence called with null or empty SequenceContext.");
            EmitSignal(SignalName.SequenceCompleted, ctx);
            return;
        }

        _spawnParent       = parent;
        _sequenceContext   = ctx;
        _promptPosition    = promptPosition;
        _sequenceCancelled = false;
        _sequenceActive    = true;
        _lastStepRun       = -1;

        // Count every circle across all steps so SequenceCompleted fires only after
        // the last circle of the last concurrent step resolves.
        _totalPromptsRemaining = 0;
        foreach (var s in ctx.CurrentAttack.Steps)
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
        if (!GodotObject.IsInstanceValid(this)) return;
        if (!_sequenceActive)
        {
            GD.Print($"[BattleSystem] RunStep({stepIndex}) skipped — sequence no longer active.");
            return;
        }
        if (_sequenceCancelled)
        {
            GD.Print($"[BattleSystem] RunStep({stepIndex}) skipped — sequence cancelled by Physical miss.");
            return;
        }

        _lastStepRun = stepIndex;
        EmitSignal(SignalName.StepStarted, stepIndex, _sequenceContext);

        var attack      = _sequenceContext.CurrentAttack;
        var step        = attack.Steps[stepIndex];
        int circleCount = step.ImpactFrames.Length;
        int firstImpact = step.ImpactFrames[0];
        int lastImpact  = step.ImpactFrames[circleCount - 1];

        GD.Print($"[BattleSystem] Step {stepIndex + 1}/{attack.Steps.Count}: " +
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
        if (stepIndex + 1 < attack.Steps.Count)
        {
            var   nextStep              = attack.Steps[stepIndex + 1];
            float lastCircleResolveTime = (lastImpact - firstImpact) / step.Fps + circleCloseDuration;
            float nextStepDelay         = Mathf.Max(0f, lastCircleResolveTime + nextStep.StartOffsetMs / 1000f);
            int   nextStepIndex         = stepIndex + 1;

            GD.Print($"[BattleSystem]   Scheduling step {nextStepIndex + 1} in {nextStepDelay:F3}s " +
                     $"(lastCircleResolveTime={lastCircleResolveTime:F3}s  " +
                     $"StartOffsetMs={nextStep.StartOffsetMs}).");

            GetTree().CreateTimer(nextStepDelay).Timeout += () => RunStep(nextStepIndex);
        }

        if (!string.IsNullOrEmpty(step.SpritesheetPath))
            GetTree().CreateTimer(animStartDelay).Timeout += () => SpawnEffectSprite(step, animStartFrame);

        // Schedule frame-synced sound effects for the first animation play.
        ScheduleStepSounds(step, animStartFrame, animStartDelay);

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
                var prompt        = _promptScene.Instantiate<TimingPrompt>();
                prompt.Type       = step.CircleType;
                prompt.BounceCount = step.BounceCount;  // set before AddChild so _Ready/ResetState picks it up
                prompt.AutoLoop   = false;
                prompt.Position   = _promptPosition;

                prompt.PassEvaluated += (result, passIndex) =>
                    EmitSignal(SignalName.StepPassEvaluated, result, passIndex, stepIndex, _sequenceContext);

                var capturedPrompt = prompt;
                prompt.PromptCompleted += result =>
                    OnAnyCircleCompleted(capturedPrompt, result, stepIndex);

                prompt.ZIndex = 20;
                _spawnParent.AddChild(prompt);

                // For Bouncing steps, replay the effect animation from the start on each
                // subsequent inward pass. Subscribe only on circle 0 — the animation is
                // shared across all circles in the step; one replay per bounce is correct.
                //
                // Replay timing: PassEvaluated fires at the end of each inward pass, then
                // the outward pass runs for exactly BounceDuration seconds, then the new
                // inward pass begins. The animation start delay is applied on top so the
                // impact frame lands on the circle close time exactly as on the first play.
                //
                //   replayDelay = BounceDuration + animStartDelay
                if (step.CircleType == TimingPrompt.PromptType.Bouncing && capturedI == 0)
                {
                    int   totalBounces = prompt.BounceCount;   // set by ApplyTypeSettings in _Ready
                    float bounceDur    = prompt.BounceDuration;
                    float replayDelay  = bounceDur + animStartDelay;

                    prompt.PassEvaluated += (result, passIndex) =>
                    {
                        // passIndex is the pass that just completed (0-based).
                        // If passIndex < totalBounces, at least one more inward pass follows.
                        if (passIndex < totalBounces)
                        {
                            GetTree().CreateTimer(replayDelay).Timeout +=
                                () => SpawnEffectSprite(step, animStartFrame);

                            // Replay frame-synced sounds in sync with the replayed animation.
                            ScheduleStepSounds(step, animStartFrame, replayDelay);
                        }
                    };
                }

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
        if (!GodotObject.IsInstanceValid(this)) return;
        // Guard: ignore callbacks from a previous sequence that has already completed.
        if (!_sequenceActive)
        {
            GD.Print($"[BattleSystem] OnAnyCircleCompleted ignored — sequence no longer active.");
            if (IsInstanceValid(prompt))
                prompt.QueueFree();
            return;
        }

        GD.Print($"[BattleSystem] Step {stepIndex + 1} circle resolved " +
                 $"({(TimingPrompt.InputResult)result}). " +
                 $"{_totalPromptsRemaining - 1} circle(s) remaining in sequence.");

        if (IsInstanceValid(prompt))
            GetTree().CreateTimer(TimingPrompt.FlashDuration).Timeout += prompt.QueueFree;

        // Player Physical attacks stop on first miss — cancel pending steps so no new circles spawn.
        // Enemy attacks always play their full sequence regardless of parry outcome.
        // TODO: dismiss already-spawned circles with a grey flash when cancelling (stop-on-miss visual).
        if (!_sequenceCancelled &&
            _sequenceContext?.Attacker?.Side == CombatantSide.Player &&
            _sequenceContext?.CurrentAttack?.Category == AttackCategory.Physical &&
            (TimingPrompt.InputResult)result == TimingPrompt.InputResult.Miss)
        {
            GD.Print("[BattleSystem] Player Physical miss — cancelling remaining steps.");
            _sequenceCancelled = true;
        }

        _totalPromptsRemaining--;
        if (_totalPromptsRemaining > 0) return;

        // Emit exactly once per sequence — _sequenceActive prevents re-emission from
        // late-resolving circles or stacked callbacks. _sequenceContext is intentionally
        // NOT cleared here: animation callbacks like OnEnemyAttackAnimFinished and the
        // teardown path (GetLastStepPostAnimDelayMs) may run AFTER SequenceCompleted and
        // still need to inspect the most recent attack. The context is overwritten by the
        // next StartSequence, matching the old "last attack stays resident" semantics of
        // the retired _currentAttack field.
        _sequenceActive = false;
        GD.Print("[BattleSystem] All circles resolved — emitting SequenceCompleted.");
        EmitSignal(SignalName.SequenceCompleted, _sequenceContext);
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
        if (!GodotObject.IsInstanceValid(this)) return;
        if (string.IsNullOrEmpty(step.SpritesheetPath)) return;

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

        // Baseline position is the target's body center. The active offset is step.Offset
        // for attackers on the right (canonical enemy side) or step.PlayerOffset for
        // attackers on the left (canonical player side) — the flip is derived from
        // attacker-vs-target X coordinates rather than a per-sequence flag. Offset.Y < 0
        // moves the effect up, Offset.Y > 0 moves it down.
        //
        // C7-extra: previously this used a hardcoded `FloorY = 750f` for the Y baseline,
        // which only matched slot 0's floor in the legacy single-row layout. With diagonal
        // columns each slot has its own Y; slots below slot 0 had effects spawning above
        // their bodies. Switching the baseline to <c>targetCenter</c> makes the offset
        // target-relative so each slot's effects align with its own body regardless of
        // slot position.
        var     attacker         = _sequenceContext.Attacker;
        var     target           = _sequenceContext.Target;
        Vector2 targetCenter     = target.Origin + target.PositionRect.Size / 2f;
        // Self-targeting (e.g., Cure heal) has attacker == target with identical Origin.X.
        // The strict > returns false in that case, routing self-target to the left-side
        // branch (PlayerOffset + !step.FlipH). That matches pre-refactor behaviour where
        // _isPlayerAttack=true (player casting heal on self) took the effectively-equivalent
        // branch. Note: there is a pre-existing Cure target-circle positioning quirk —
        // the circle appears slightly right of the knight because target.Origin +
        // PositionRect.Size/2f centers on the 80-wide ColorRect rather than the
        // character's body (same root cause as the damage-number quirk flagged in
        // Phase 3.3 testing). Preserved as-is; proper fix is deferred to B5 / scaffolding work.
        bool    attackerOnRight  = attacker.Origin.X > target.Origin.X;
        Vector2 activeOffset     = attackerOnRight ? step.Offset : step.PlayerOffset;

        var sprite          = new AnimatedSprite2D();
        sprite.SpriteFrames = spriteFrames;
        sprite.Centered     = true;
        // step.FlipH is authored for a right-side attacker firing left toward the target
        // (canonical enemy-attacks-player case). Invert for left-side attackers so the
        // same .tres file works in both directions without a separate PlayerFlipH field.
        sprite.FlipH        = attackerOnRight ? step.FlipH : !step.FlipH;
        sprite.Scale        = step.Scale;
        sprite.Position     = targetCenter + activeOffset;
        // C7-extra-followup: effect sprites take the target's ZIndex so they "join
        // the row" of the combatant they hit — front-row-target effects render at
        // the front-row Z (and may be occluded by adjacent back-row sprites that
        // overlap, by design); back-row-target effects render at the back-row Z
        // (in front of front-row sprites in the same column). Tree order keeps the
        // effect rendering on top of the target sprite at equal Z (effect added
        // later). At 1v1 default (target = slot 0, Z=0) this matches the
        // pre-refactor default-Z behaviour. The Phase 1 → Phase 2 reveal sequence
        // does not call SpawnEffectSprite, so the legacy reveal layering (reveal=1,
        // warrior bumped=2) is unaffected by this change.
        sprite.ZIndex       = target.AnimSprite.ZIndex;
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
            // Hold the sprite for PostAnimationDelayMs before freeing it, so the last
            // frame of the animation stays visible for the configured duration.
            if (step.PostAnimationDelayMs > 0)
                GetTree().CreateTimer(step.PostAnimationDelayMs / 1000f).Timeout += sprite.QueueFree;
            else
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
    // Audio
    // =========================================================================

    /// <summary>
    /// Schedules frame-synced sounds from a step's SoundEffects/SoundTriggerFrames arrays.
    /// Each sound plays at baseDelay + (triggerFrame - animStartFrame) / fps seconds from now.
    /// Called once in RunStep for the initial play, and again on each bouncing replay.
    /// </summary>
    private void ScheduleStepSounds(AttackStep step, int animStartFrame, float baseDelay)
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        if (step.SoundEffects == null || step.SoundEffects.Length == 0) return;

        int soundCount = Mathf.Min(step.SoundEffects.Length, step.SoundTriggerFrames.Length);
        for (int s = 0; s < soundCount; s++)
        {
            string soundPath    = step.SoundEffects[s];
            int    triggerFrame = step.SoundTriggerFrames[s];
            float  soundDelay  = baseDelay + Mathf.Max(0f, (triggerFrame - animStartFrame) / step.Fps);

            if (soundDelay <= 0f)
                PlaySound(soundPath);
            else
                GetTree().CreateTimer(soundDelay).Timeout += () => PlaySound(soundPath);
        }
    }

    /// <summary>
    /// Fire-and-forget one-shot sound playback. Accepts a full res:// path.
    /// Creates a temporary AudioStreamPlayer that frees itself when done.
    /// </summary>
    private void PlaySound(string resPath)
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        var stream = GD.Load<AudioStream>(resPath);
        if (stream == null)
        {
            GD.PrintErr($"[BattleSystem] Failed to load audio: {resPath}");
            return;
        }
        var player = new AudioStreamPlayer();
        player.Stream = stream;
        AddChild(player);
        player.Play();
        player.Finished += player.QueueFree;
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
