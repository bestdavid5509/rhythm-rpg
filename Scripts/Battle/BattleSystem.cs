using System;
using Godot;

/// <summary>
/// Core battle system — manages attack sequences driven by AttackData resources.
///
/// An attack sequence is an ordered list of AttackStep objects. For each step:
///   1. A circle prompt spawns at the combat midpoint.
///   2. An AnimatedSprite2D spawns at the defender's position, timed so its impact
///      frame lands exactly when the circle closes:
///        animationStartDelay = circleCloseDuration - (impactFrame / fps)
///   3. The next step begins only after the current step's circle resolves and
///      the step's DelayMs has elapsed.
///
/// Signals:
///   StepPassEvaluated — re-emitted from each step's TimingPrompt.PassEvaluated.
///                       stepIndex identifies which step in the sequence fired.
///   SequenceCompleted — emitted when every step in the sequence has resolved.
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
    private const string TestAttackPath = "res://Resources/Attacks/blue_sword_plunge.tres";

    private PackedScene  _promptScene;
    private AttackData   _currentAttack;   // the attack currently being executed
    private Node2D       _spawnParent;     // node that owns spawned prompts and sprites
    private Vector2      _defenderCenter;  // world-space center of the defender — effect origin
    private Vector2      _promptPosition;  // world-space position for circle prompts
    private int          _stepIndex;       // current index into _currentAttack.Steps
    private TimingPrompt _stepPrompt;      // active prompt for the current step
    private float        _effectOffsetY;   // per-sequence Y nudge passed in from BattleTest

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
    /// Spawns prompts and timed effect sprites in step order, waiting for each
    /// prompt to resolve before starting the next step's delay timer.
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
        _stepIndex      = 0;

        RunCurrentStep();
    }

    // =========================================================================
    // Sequence runner — private step logic
    // =========================================================================

    private void RunCurrentStep()
    {
        if (_stepIndex >= _currentAttack.Steps.Count)
        {
            GD.Print($"[BattleSystem] All {_currentAttack.Steps.Count} step(s) resolved — sequence complete.");
            EmitSignal(SignalName.SequenceCompleted);
            return;
        }

        var step = _currentAttack.Steps[_stepIndex];
        GD.Print($"[BattleSystem] Step {_stepIndex + 1}/{_currentAttack.Steps.Count}: " +
                 $"CircleType={step.CircleType}  ImpactFrame={step.ImpactFrame}  Fps={step.Fps}");

        // Spawn the circle prompt at the combat midpoint.
        var prompt  = _promptScene.Instantiate<TimingPrompt>();
        prompt.Type     = step.CircleType;
        prompt.AutoLoop = false;
        prompt.Position = _promptPosition;
        _stepPrompt     = prompt;

        int capturedStep = _stepIndex;
        prompt.PassEvaluated   += (result, passIndex) =>
            EmitSignal(SignalName.StepPassEvaluated, result, passIndex, capturedStep);
        prompt.PromptCompleted += OnStepPromptCompleted;

        _spawnParent.AddChild(prompt);

        // Schedule the effect animation so its impact frame lands exactly when the circle closes.
        //   animationStartDelay = circleCloseDuration - (impactFrame / fps)
        // A positive delay means the animation starts mid-flight; a zero delay (clamped from
        // negative) means the animation starts immediately and the impact frame is already past.
        float circleCloseDuration = TimingPrompt.DefaultDurationForType(step.CircleType);
        float animStartDelay      = Mathf.Max(0f, circleCloseDuration - step.ImpactFrame / step.Fps);

        GD.Print($"[BattleSystem]   circleCloseDuration={circleCloseDuration:F2}s  " +
                 $"animStartDelay={animStartDelay:F2}s");

        GetTree().CreateTimer(animStartDelay).Timeout += () =>
        {
            if (IsInstanceValid(prompt))
                SpawnEffectSprite(step);
        };
    }

    private void OnStepPromptCompleted(int result)
    {
        GD.Print($"[BattleSystem] Step {_stepIndex + 1} prompt resolved " +
                 $"({(TimingPrompt.InputResult)result}).");

        // Hand off the prompt reference and schedule its cleanup after the flash.
        var completed = _stepPrompt;
        _stepPrompt   = null;
        if (IsInstanceValid(completed))
            GetTree().CreateTimer(TimingPrompt.FlashDuration).Timeout += completed.QueueFree;

        _stepIndex++;

        if (_stepIndex >= _currentAttack.Steps.Count)
        {
            GD.Print("[BattleSystem] All steps resolved — emitting SequenceCompleted.");
            EmitSignal(SignalName.SequenceCompleted);
            return;
        }

        // Wait for the next step's inter-step delay before spawning its prompt.
        int delayMs = _currentAttack.Steps[_stepIndex].DelayMs;
        if (delayMs > 0)
        {
            GD.Print($"[BattleSystem] Waiting {delayMs}ms before step {_stepIndex + 1}.");
            GetTree().CreateTimer(delayMs / 1000.0).Timeout += RunCurrentStep;
        }
        else
        {
            RunCurrentStep();
        }
    }

    /// <summary>
    /// Builds a SpriteFrames resource from the step's spritesheet and spawns an
    /// AnimatedSprite2D at the defender's center plus the step's Offset.
    /// The sprite queues itself free when its animation finishes.
    ///
    /// Frame grid is derived from texture dimensions divided by FrameWidth/FrameHeight.
    /// Any trailing empty cells (when total frames &lt; rows×cols) are included in the
    /// SpriteFrames but are never reached because the animation is non-looping.
    /// </summary>
    private void SpawnEffectSprite(AttackStep step)
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

        GD.Print($"[BattleSystem]   Spawned effect ({cols}×{rows} grid, {cols * rows} frames) " +
                 $"at {sprite.Position}.");
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
