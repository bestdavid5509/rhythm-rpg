using Godot;
using System.Collections.Generic;

/// <summary>
/// A timing prompt visual — a ring that starts large and closes toward a smaller target
/// ring at the center. The player presses the input button when the moving ring overlaps
/// the target ring; success is evaluated by how close the centers are at press time.
///
/// Prompt types:
///   Standard — white ring, ease-in inward, standard speed.
///   Slow     — blue ring, half speed.
///   Bouncing — pink → orange → white across three inward passes; BounceCount forced to 2.
///              Color shift signals the final pass.
///
/// Movement — fully scripted; player input never alters the path:
///   Inward  — ease-in (slow start, fast finish). Travels from StartRadius to
///             (TargetRadius - RingLineWidth), the inner edge of the valid zone.
///             t = 1 completes the pass regardless of input.
///   Outward — ease-out (fast launch, decelerates). Always lerps from TargetRadius to
///             StartRadius over exactly BounceDuration seconds. Bounce point is scripted.
///
/// Input rules:
///   Valid zone   = |_currentRadius - TargetRadius| <= RingLineWidth (ring overlap).
///   Inward only  = for Bouncing, input is additionally restricted to inward passes.
///   In zone      → hit registered, flash immediately, no lockout; circle continues.
///   Outside zone → failed input, InputLockoutDuration lockout; circle unaffected.
///   Outward pass → failed input, lockout; outward is purely animated.
///   Auto-miss    → pass exits zone at t = 1 without input; damage dealt, NO lockout.
///
/// Multi-circle resolution:
///   All active prompts register in _activePrompts on _Ready.
///   BattleSystem calls TimingPrompt.ConfirmAll() once per input event.
/// </summary>
public partial class TimingPrompt : Node2D
{
    // -------------------------------------------------------------------------
    // Enums
    // -------------------------------------------------------------------------

    public enum InputResult
    {
        Perfect,  // within PerfectWindowFraction of the center of the valid zone
        Hit,      // within RingLineWidth of TargetRadius, outside the perfect zone
        Miss,     // pass exits without a registered hit
    }

    public enum PromptType
    {
        Standard,  // white ring, default speed
        Slow,      // blue ring, half speed
        Bouncing,  // pink→orange→white; scripted three-pass sequence, BounceCount = 2
    }

    // -------------------------------------------------------------------------
    // Exported properties
    // -------------------------------------------------------------------------

    /// <summary>
    /// Determines the visual style, speed, and bounce behaviour of this prompt.
    /// Automatically configures Duration and BounceCount on _Ready.
    /// </summary>
    [Export] public PromptType Type = PromptType.Standard;

    /// <summary>
    /// Total time in seconds for one inward pass.
    /// Set automatically by Type — override only when driving directly from BattleSystem.
    /// </summary>
    [Export] public float Duration = 1.0f;

    /// <summary>
    /// Easing curve for inward movement.
    /// X = normalised time (0–1), Y = normalised position (0–1).
    /// Ease-in shape: starts low, accelerates toward 1. Falls back to t² if null.
    /// </summary>
    [Export] public Curve InCurve;

    /// <summary>
    /// Easing curve for outward movement.
    /// Ease-out shape: starts high, decelerates toward 0. Falls back to 1-(1-t)² if null.
    /// </summary>
    [Export] public Curve OutCurve;

    /// <summary>
    /// Stroke width of both rings in pixels. Drives three things simultaneously:
    ///   • Moving ring visual thickness (DrawArc line width).
    ///   • Target ring visual thickness (DrawArc line width).
    ///   • Valid input zone: input is accepted when |_currentRadius - TargetRadius| &lt;= RingLineWidth,
    ///     which equals exactly the condition "the two rings overlap at all".
    /// Keeping these in sync via a single value ensures the green rim always matches reality.
    /// </summary>
    [Export] public float RingLineWidth = 6f;

    /// <summary>
    /// Time in seconds for the outward (bounce) pass.
    /// The ring always lerps from TargetRadius to StartRadius in this exact duration,
    /// making Perfect@ timestamps on subsequent passes fully deterministic.
    /// </summary>
    [Export] public float BounceDuration = 0.5f;

    /// <summary>
    /// Seconds during which input is locked out after a failed press (outside zone or during
    /// outward pass). Prevents accidental multiple presses from stacking.
    /// No lockout is applied after a successful input or after an auto-miss.
    /// </summary>
    [Export] public float InputLockoutDuration = 0.3f;

    /// <summary>
    /// Number of bounces after the first inward pass.
    /// Forced to 2 when Type is Bouncing. Override only when driving from BattleSystem.
    /// </summary>
    [Export] public int BounceCount = 0;

    /// <summary>
    /// Assign an AudioStreamPlayer to play a sound on Miss.
    /// Safely no-op when null — assign once audio assets are ready.
    /// </summary>
    [Export] public AudioStreamPlayer MissSoundPlayer;

    // DEV ONLY ----------------------------------------------------------------
    /// <summary>
    /// When true, the prompt resets automatically one second after resolving.
    /// For development testing only — disable before shipping.
    /// </summary>
    [Export] public bool AutoLoop = true;

    // DEBUG ONLY — no-op in release; costs nothing when false.
    /// <summary>
    /// When true, draws a per-frame / per-pass / per-resolution debug overlay
    /// in the top-left corner of the screen. Disable before shipping.
    /// </summary>
    [Export] public bool DebugMode = true;
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Signals
    // -------------------------------------------------------------------------

    /// <summary>
    /// Emitted once per inward pass on Bouncing prompts — hit or miss — so BattleSystem
    /// can apply per-pass damage. Cast result to InputResult.
    /// Not emitted by Standard or Slow (use PromptCompleted for those).
    /// </summary>
    [Signal]
    public delegate void PassEvaluatedEventHandler(int result, int passIndex);

    /// <summary>
    /// Emitted when the full prompt sequence is resolved.
    /// For Standard/Slow: carries the single-pass result.
    /// For Bouncing: carries the final pass result (per-pass results come via PassEvaluated).
    /// Cast result to InputResult.
    /// </summary>
    [Signal]
    public delegate void PromptCompletedEventHandler(int result);

    // -------------------------------------------------------------------------
    // Visual constants
    // -------------------------------------------------------------------------

    // ARTIST SWAP POINT --------------------------------------------------------
    // Everything in _Draw() is placeholder geometry only.
    // When art is ready:
    //   • Replace the moving ring DrawArc with an AnimatedSprite2D scaled by
    //     (_currentRadius / StartRadius), driven from _Process.
    //   • Replace the target ring DrawArc with a static Sprite2D child node.
    //   • Drive result feedback and per-type tints via a shader uniform or
    //     AnimationPlayer rather than _movingRingColor below.
    //   • StartRadius, TargetRadius, and RingLineWidth will still be needed
    //     to configure scale and travel range.
    // --------------------------------------------------------------------------

    private const float StartRadius           = 120f;
    private const float TargetRadius          = 28f;
    private const float PerfectWindowFraction = 0.4f;

    // Shared UI colors
    private static readonly Color ColorTarget    = new Color(1.00f, 1.00f, 1.00f, 0.90f);
    private static readonly Color ColorHitWindow = new Color(0.30f, 1.00f, 0.50f, 0.40f);  // green rim

    // Per-type ring colors
    private static readonly Color ColorStandard    = new Color(1.00f, 1.00f, 1.00f, 1.00f);  // white
    private static readonly Color ColorSlow        = new Color(0.35f, 0.65f, 1.00f, 1.00f);  // blue
    private static readonly Color ColorBouncePass1 = new Color(0.20f, 0.85f, 0.80f, 1.00f);  // teal
    private static readonly Color ColorBouncePass2 = new Color(1.00f, 0.60f, 0.20f, 1.00f);  // orange
    // Pass 3 of Bouncing uses ColorStandard (white) — signals final approach to the player

    // Result flash colors (always override type color)
    private static readonly Color ColorFlashPerfect = new Color(0.30f, 1.00f, 0.40f, 1.00f);  // green
    private static readonly Color ColorFlashHit     = new Color(1.00f, 0.90f, 0.20f, 1.00f);  // yellow
    private static readonly Color ColorFlashMiss    = new Color(1.00f, 0.20f, 0.20f, 1.00f);  // red

    private const float SlowDuration   = 2.0f;

    private const float FlashDuration  = 0.15f;
    private const float ShakeDuration  = 0.30f;
    private const float ShakeIntensity = 6f;

    // -------------------------------------------------------------------------
    // Static registry — allows BattleSystem to resolve all active prompts at once
    // -------------------------------------------------------------------------

    private static readonly List<TimingPrompt> _activePrompts = new();

    // -------------------------------------------------------------------------
    // Runtime state
    // -------------------------------------------------------------------------

    private float   _t                = 0f;  // normalised time within current pass (0–1)
    private bool    _movingInward     = true;
    private int     _bouncesRemaining;
    private int     _passIndex        = 0;   // which inward pass (0-based); drives bouncing colors
    private bool    _resolved         = false;
    private float   _currentRadius;

    // Whether a valid input was registered during the current inward pass.
    // Set on press (with flash); consumed at t=1 by OnPassComplete.
    private bool        _inputRegisteredThisPass = false;
    private InputResult _lastPassResult          = InputResult.Miss;

    private float   _lockoutTimer      = 0f;  // blocks input after a failed press
    private float   _bounceStartRadius = TargetRadius;  // outward lerp origin; always TargetRadius

    private Color   _movingRingColor  = ColorStandard;
    private float   _flashTimer       = 0f;
    private bool    _showMovingRing   = true;

    // Separate flash ring drawn on input results — does not affect the moving ring's color or path.
    // Perfect: locked to TargetRadius in green. Hit: locked to _currentRadius at press in yellow.
    // Auto-miss: locked to TargetRadius - RingLineWidth in red.
    private float   _flashRingTimer      = 0f;
    private float   _flashRingRadius     = 0f;
    private Color   _flashRingColor      = ColorFlashPerfect;
    // True when the flash ring was set by an auto-miss — overrides outward-pass suppression so
    // the ring shows during the bounce that immediately follows a missed inward pass.
    private bool    _flashRingIsAutoMiss = false;

    private Vector2 _shakeOrigin;
    private float   _shakeTimer       = 0f;

    // DEBUG ONLY ---------------------------------------------------------------
    private ulong       _dbgSequenceStartMs;
    private ulong       _dbgPassStartMs;
    private ulong       _dbgPerfectWindowMs;
    private ulong       _dbgPlayerPressMs;
    private bool        _dbgPlayerPressedThisPass;
    private float       _dbgLastAccuracy;
    private InputResult _dbgLastResult;
    private ulong       _dbgResolutionMs;
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public override void _Ready()
    {
        _shakeOrigin = Position;
        _activePrompts.Add(this);
        ApplyTypeSettings();
        ResetState();
    }

    public override void _ExitTree()
    {
        _activePrompts.Remove(this);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // Flash ring timer — counts down independently; DrawDebugOverlay renders it while active.
        if (_flashRingTimer > 0f)
        {
            _flashRingTimer -= dt;
            QueueRedraw();
        }

        // Flash timer — holds result colour briefly, then either hides the ring (resolved)
        // or resets it to the current pass's base color (mid-sequence bounce).
        if (_flashTimer > 0f)
        {
            _flashTimer -= dt;
            if (_flashTimer <= 0f && !_resolved)
                _movingRingColor = GetBaseColor();
            QueueRedraw();
        }

        // Screen shake — brief random position displacement on Miss.
        if (_shakeTimer > 0f)
        {
            _shakeTimer -= dt;
            if (_shakeTimer <= 0f)
                Position = _shakeOrigin;
            else
                Position = _shakeOrigin + new Vector2(
                    GD.Randf() * 2f - 1f,
                    GD.Randf() * 2f - 1f
                ) * ShakeIntensity;
        }

        if (_resolved) return;

        // Lockout timer — decrement each frame; EvaluateInput checks this internally.
        if (_lockoutTimer > 0f)
            _lockoutTimer -= dt;

        // Always delegate to EvaluateInput — it handles lockout, direction, and zone checks.
        if (Input.IsActionJustPressed("battle_confirm"))
            EvaluateInput();

        // Inward: advances over Duration; outward: advances over BounceDuration.
        _t = Mathf.Clamp(_t + dt / (_movingInward ? Duration : BounceDuration), 0f, 1f);

        // Inward pass: StartRadius → (TargetRadius - RingLineWidth), the inner edge of the
        // valid zone. t = 1 triggers OnPassComplete regardless of whether the player hit.
        // Outward pass: always TargetRadius → StartRadius for deterministic timing.
        float inwardEndpoint = TargetRadius - RingLineWidth;
        _currentRadius = _movingInward
            ? Mathf.Lerp(StartRadius,        inwardEndpoint, SampleCurve(InCurve,  _t, easeIn: true))
            : Mathf.Lerp(_bounceStartRadius, StartRadius,    SampleCurve(OutCurve, _t, easeIn: false));

        QueueRedraw();

        if (_t >= 1f)
            OnPassComplete();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Evaluates the player's input against the current ring position.
    ///
    /// In zone (inward pass, |_currentRadius - TargetRadius| &lt;= RingLineWidth):
    ///   Hit registered, flash shown immediately, no lockout. Circle continues on its
    ///   scripted path; the result is consumed at t = 1 by OnPassComplete.
    ///
    /// Outside zone OR during outward pass:
    ///   Failed input. InputLockoutDuration lockout starts. Circle unaffected.
    ///
    /// Locked out OR already hit this pass:
    ///   Completely ignored, no effect.
    /// </summary>
    public InputResult EvaluateInput()
    {
        if (_resolved) return InputResult.Miss;

        // Locked out — ignore completely, no additional penalty.
        if (_lockoutTimer > 0f) return InputResult.Miss;

        // During outward pass: failed input, red flash at press position, lockout.
        if (!_movingInward)
        {
            _flashRingRadius     = _currentRadius;
            _flashRingColor      = ColorFlashMiss;
            _flashRingTimer      = FlashDuration;
            _flashRingIsAutoMiss = false;  // ensures outward-pass suppression applies
            QueueRedraw();
            _lockoutTimer = InputLockoutDuration;
            return InputResult.Miss;
        }

        float distance = Mathf.Abs(_currentRadius - TargetRadius);

        // Outside valid zone: failed input, red flash at press position, lockout.
        if (distance > RingLineWidth)
        {
            _flashRingRadius     = _currentRadius;
            _flashRingColor      = ColorFlashMiss;
            _flashRingTimer      = FlashDuration;
            _flashRingIsAutoMiss = false;  // ensures outward-pass suppression applies
            QueueRedraw();
            _lockoutTimer = InputLockoutDuration;
            return InputResult.Miss;
        }

        // Already registered a hit this pass — ignore.
        if (_inputRegisteredThisPass) return InputResult.Miss;

        // In zone: register hit, flash immediately, circle continues on scripted path.
        InputResult result = distance <= RingLineWidth * PerfectWindowFraction
            ? InputResult.Perfect
            : InputResult.Hit;

        _inputRegisteredThisPass = true;
        _lastPassResult          = result;

        // Flash ring appears at a fixed position independent of the moving ring.
        // Perfect: snaps to TargetRadius. Hit: locked to the exact radius at press time.
        // The moving ring itself does not change color — it continues on its scripted path.
        _flashRingRadius = (result == InputResult.Perfect) ? TargetRadius : _currentRadius;
        _flashRingColor  = FlashColorFor(result);
        _flashRingTimer  = FlashDuration;
        QueueRedraw();

        // DEBUG ONLY
        if (DebugMode && !_dbgPlayerPressedThisPass)
        {
            _dbgPlayerPressMs         = Time.GetTicksMsec();
            _dbgPlayerPressedThisPass = true;
            _dbgLastAccuracy          = Mathf.Clamp(distance / RingLineWidth, 0f, 1f);
        }

        return result;
    }

    /// <summary>
    /// Evaluates all active prompts against a single input event.
    /// Call once per input event from BattleSystem for multi-circle encounters.
    /// Each prompt's EvaluateInput handles its own zone, direction, and lockout checks.
    /// </summary>
    public static void ConfirmAll()
    {
        // Iterate over a copy in case evaluation causes prompts to exit the tree.
        foreach (var prompt in new List<TimingPrompt>(_activePrompts))
            prompt.EvaluateInput();
    }

    /// <summary>
    /// Reverses the prompt's direction, resetting t to 0.
    /// Always called at t = 1 of an inward pass (scripted bounce point).
    /// The outward lerp always starts from TargetRadius, so every bounce covers the same
    /// distance and completes in exactly BounceDuration seconds.
    /// </summary>
    public void Bounce()
    {
        _bounceStartRadius = TargetRadius;  // always start outward from the target
        _t                 = 0f;
        _movingInward      = false;
        _bouncesRemaining--;
        // Advance pass index and apply the next pass color immediately so the player
        // sees the new color for the full outward travel before the next inward pass begins.
        _passIndex++;
        _movingRingColor = GetBaseColor();
        QueueRedraw();
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------

    public override void _Draw()
    {
        // Green rim and target ring — always visible; persist after resolution.
        DrawBand(TargetRadius, RingLineWidth, ColorHitWindow);
        DrawArc(Vector2.Zero, TargetRadius, 0f, Mathf.Tau, 64, ColorTarget, RingLineWidth);

        // Moving ring — hidden after flash expires on a fully resolved prompt.
        if (_showMovingRing)
            DrawArc(Vector2.Zero, Mathf.Max(0f, _currentRadius), 0f, Mathf.Tau, 64, _movingRingColor, RingLineWidth);

        // Flash ring — wrong-input flashes are suppressed during outward passes (Bouncing only)
        // to avoid cluttering the bounce animation. Auto-miss flashes override this suppression
        // so the inner red ring is visible during the bounce that immediately follows a miss.
        bool suppressFlashRing = Type == PromptType.Bouncing && !_movingInward && !_flashRingIsAutoMiss;
        if (_flashRingTimer > 0f && !suppressFlashRing)
            DrawArc(Vector2.Zero, _flashRingRadius, 0f, Mathf.Tau, 64, _flashRingColor, RingLineWidth);

        // DEBUG ONLY
        if (DebugMode) DrawDebugOverlay();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called from OnPassComplete at t = 1 of a Bouncing inward pass.
    /// Uses _inputRegisteredThisPass to determine the result.
    /// Flash and shake only fire on auto-miss (hit was already flashed at press time).
    /// Always bounces if passes remain; resolves on the final pass.
    /// </summary>
    private void EvaluatePassAndContinue(InputResult result)
    {
        // Only flash/shake on auto-miss — hit was flashed at press time.
        // Flash is suppressed when _lockoutTimer > 0: the wrong-input red flash already fired,
        // drawing a second one is redundant. Circle resolves silently in that case.
        if (result == InputResult.Miss)
        {
            if (_lockoutTimer <= 0f)
            {
                _flashRingRadius     = TargetRadius - RingLineWidth;
                _flashRingColor      = ColorFlashMiss;
                _flashRingTimer      = FlashDuration;
                _flashRingIsAutoMiss = true;
                QueueRedraw();
            }
            _shakeOrigin = Position;
            _shakeTimer  = ShakeDuration;
            MissSoundPlayer?.Play();
        }

        EmitSignal(SignalName.PassEvaluated, (int)result, _passIndex);

        if (_bouncesRemaining > 0)
        {
            Bounce();
        }
        else
        {
            // Final pass complete — fully resolve.
            // DEBUG ONLY
            if (DebugMode) { _dbgLastResult = result; _dbgResolutionMs = Time.GetTicksMsec(); }

            _resolved       = true;
            _showMovingRing = false;
            EmitSignal(SignalName.PromptCompleted, (int)result);
            if (AutoLoop)
                GetTree().CreateTimer(1.0).Timeout += ResetPrompt;
        }
    }

    /// <summary>
    /// Called from OnPassComplete at t = 1 of a Standard or Slow inward pass.
    /// Uses _inputRegisteredThisPass to determine the result.
    /// Flash and shake only fire on auto-miss.
    /// </summary>
    private void Resolve(InputResult result)
    {
        if (_resolved) return;
        _resolved       = true;
        _showMovingRing = false;

        // Only flash/shake on auto-miss — hit was flashed at press time.
        // Flash is suppressed when _lockoutTimer > 0: the wrong-input red flash already fired.
        if (result == InputResult.Miss)
        {
            if (_lockoutTimer <= 0f)
            {
                _flashRingRadius     = TargetRadius - RingLineWidth;
                _flashRingColor      = ColorFlashMiss;
                _flashRingTimer      = FlashDuration;
                _flashRingIsAutoMiss = true;
            }
            _shakeOrigin = Position;
            _shakeTimer  = ShakeDuration;
            MissSoundPlayer?.Play();
        }

        // DEBUG ONLY
        if (DebugMode)
        {
            _dbgLastResult   = result;
            _dbgResolutionMs = Time.GetTicksMsec();
            _dbgLastAccuracy = Mathf.Clamp(Mathf.Abs(_currentRadius - TargetRadius) / RingLineWidth, 0f, 1f);
        }

        QueueRedraw();
        EmitSignal(SignalName.PromptCompleted, (int)result);

        if (AutoLoop)
            GetTree().CreateTimer(1.0).Timeout += ResetPrompt;
    }

    /// <summary>
    /// Called when a pass (inward or outward) reaches t = 1.
    ///
    /// Inward: pass is complete. Result = registered hit if _inputRegisteredThisPass,
    ///         otherwise auto-miss (no lockout). For Bouncing, always bounces.
    ///         For Standard/Slow, resolves the prompt.
    /// Outward: starts the next inward pass and resets per-pass input state.
    /// </summary>
    private void OnPassComplete()
    {
        if (_resolved) return;

        if (_movingInward)
        {
            InputResult result = _inputRegisteredThisPass ? _lastPassResult : InputResult.Miss;

            if (Type == PromptType.Bouncing)
                EvaluatePassAndContinue(result);
            else
                Resolve(result);
        }
        else
        {
            // Outward pass complete — start the next inward pass.
            // _passIndex and _movingRingColor were already updated in Bounce() at outward-pass start.
            _t                       = 0f;
            _movingInward            = true;
            _inputRegisteredThisPass = false;
            _lastPassResult          = InputResult.Miss;

            // DEBUG ONLY
            if (DebugMode) DbgStartNewInwardPass();
        }
    }

    /// <summary>
    /// Configures Duration and BounceCount from the current Type.
    /// Called on _Ready and before each AutoLoop reset when type cycling is active.
    /// </summary>
    private void ApplyTypeSettings()
    {
        switch (Type)
        {
            case PromptType.Standard:
                Duration    = 1.0f;
                BounceCount = 0;
                break;
            case PromptType.Slow:
                Duration    = SlowDuration;
                BounceCount = 0;
                break;
            case PromptType.Bouncing:
                Duration    = 1.0f;
                BounceCount = 2;
                break;
        }
    }

    /// <summary>
    /// Returns the base ring color for the current type and pass index.
    /// Result flash colors are applied separately and always override this.
    /// </summary>
    private Color GetBaseColor()
    {
        return Type switch
        {
            PromptType.Slow     => ColorSlow,
            PromptType.Bouncing => _passIndex switch
            {
                0 => ColorBouncePass1,  // teal   — first approach
                1 => ColorBouncePass2,  // orange — second approach (color shifts at start of outward pass)
                _ => ColorStandard,     // white  — final approach (color shifts at start of outward pass)
            },
            _                   => ColorStandard,
        };
    }

    /// <summary>Returns the flash color corresponding to a given result.</summary>
    private static Color FlashColorFor(InputResult result) => result switch
    {
        InputResult.Perfect => ColorFlashPerfect,
        InputResult.Hit     => ColorFlashHit,
        _                   => ColorFlashMiss,
    };

    /// <summary>Samples a Godot Curve, falling back to quadratic easing if null.</summary>
    private static float SampleCurve(Curve curve, float t, bool easeIn)
    {
        if (curve != null)
            return curve.Sample(t);

        return easeIn ? t * t : 1f - (1f - t) * (1f - t);
    }

    /// <summary>Resets all runtime state for a fresh prompt run.</summary>
    private void ResetState()
    {
        _t                       = 0f;
        _movingInward            = true;
        _passIndex               = 0;
        _bouncesRemaining        = BounceCount;
        _resolved                = false;
        _currentRadius           = StartRadius;
        _movingRingColor         = GetBaseColor();
        _flashTimer              = 0f;
        _showMovingRing          = true;
        _lockoutTimer            = 0f;
        _bounceStartRadius       = TargetRadius;
        _flashRingTimer          = 0f;
        _flashRingRadius         = 0f;
        _flashRingIsAutoMiss     = false;
        _inputRegisteredThisPass = false;
        _lastPassResult          = InputResult.Miss;
        _shakeTimer              = 0f;
        Position                 = _shakeOrigin;

        // DEBUG ONLY
        if (DebugMode)
        {
            _dbgSequenceStartMs = Time.GetTicksMsec();
            _dbgResolutionMs    = 0;
            _dbgLastAccuracy    = 0f;
            _dbgLastResult      = InputResult.Miss;
            DbgStartNewInwardPass();
        }

        QueueRedraw();
    }

    /// <summary>
    /// Resets the prompt for another pass.
    /// DEV ONLY — called by the AutoLoop timer; not part of the shipping battle flow.
    /// When AutoLoop is active, cycles through Standard → Slow → Bouncing → Standard.
    /// </summary>
    private void ResetPrompt()
    {
        if (AutoLoop)
        {
            Type = Type switch
            {
                PromptType.Standard => PromptType.Slow,
                PromptType.Slow     => PromptType.Bouncing,
                _                   => PromptType.Standard,
            };
            ApplyTypeSettings();
        }

        ResetState();
    }

    /// <summary>
    /// Draws a filled band (annulus) centered at the origin using a thick arc.
    /// halfThickness is the distance either side of centerRadius.
    /// </summary>
    private void DrawBand(float centerRadius, float halfThickness, Color color)
    {
        float mid       = Mathf.Max(0f, centerRadius);
        float thickness = halfThickness * 2f;
        DrawArc(Vector2.Zero, mid, 0f, Mathf.Tau, 64, color, thickness);
    }

    // =========================================================================
    // DEBUG ONLY — everything below this line is gated by DebugMode
    // =========================================================================

    private void DbgStartNewInwardPass()
    {
        _dbgPassStartMs           = Time.GetTicksMsec();
        float tPerfect            = DbgSolveTForRadius(TargetRadius);
        _dbgPerfectWindowMs       = _dbgPassStartMs + (ulong)(tPerfect * Duration * 1000f);
        _dbgPlayerPressedThisPass = false;
        _dbgPlayerPressMs         = 0;
    }

    private float DbgSolveTForRadius(float targetR)
    {
        float inwardEndpoint = TargetRadius - RingLineWidth;
        float lo = 0f, hi = 1f;
        for (int i = 0; i < 20; i++)
        {
            float mid = (lo + hi) * 0.5f;
            float r   = Mathf.Lerp(StartRadius, inwardEndpoint, SampleCurve(InCurve, mid, easeIn: true));
            if (r > targetR) lo = mid; else hi = mid;
        }
        return (lo + hi) * 0.5f;
    }

    private void DrawDebugOverlay()
    {
        Font font = ThemeDB.Singleton.FallbackFont;
        if (font == null) return;

        const int   fontSize = 14;
        const float lineH    = 19f;

        Color dimWhite = new Color(1.00f, 1.00f, 1.00f, 0.80f);
        Color active   = new Color(0.40f, 1.00f, 0.65f, 1.00f);
        Color heading  = new Color(1.00f, 0.85f, 0.30f, 1.00f);
        Color warn     = new Color(1.00f, 0.50f, 0.20f, 1.00f);

        float x = -_shakeOrigin.X + 12f;
        float y = -_shakeOrigin.Y + 20f;

        void Ln(string text, Color col)
        {
            DrawString(font, new Vector2(x, y), text, HorizontalAlignment.Left, -1, fontSize, col);
            y += lineH;
        }

        // ── Header ────────────────────────────────────────────────────────────
        Ln("─── TimingPrompt Debug ───────────────────", heading);

        // ── Per-frame ─────────────────────────────────────────────────────────
        Ln("  FRAME", heading);
        Ln($"    Radius      : {_currentRadius:F1} px   (target {TargetRadius:F0}, window ±{RingLineWidth:F0})", dimWhite);
        Ln($"    t           : {_t:F3}", dimWhite);
        Ln($"    Direction   : {(_movingInward ? "Inward" : "Outward")}", dimWhite);
        Ln($"    Lockout     : {(_lockoutTimer > 0f ? $"{_lockoutTimer:F2}s remaining" : "—")}", _lockoutTimer > 0f ? warn : dimWhite);
        Ln($"    Hit reg.    : {(_inputRegisteredThisPass ? _lastPassResult.ToString() : "—")}", _inputRegisteredThisPass ? active : dimWhite);

        // ── Per-pass ──────────────────────────────────────────────────────────
        int totalPasses = BounceCount + 1;
        Ln($"  PASS  {_passIndex + 1} / {totalPasses}   [{Type}]  BounceDur: {BounceDuration:F2}s", heading);

        long passStartRel = (long)(_dbgPassStartMs    - _dbgSequenceStartMs);
        long perfectRel   = (long)(_dbgPerfectWindowMs - _dbgSequenceStartMs);
        Ln($"    Started     : +{passStartRel} ms", dimWhite);
        Ln($"    Perfect @   : +{perfectRel} ms", dimWhite);

        if (_dbgPlayerPressedThisPass)
        {
            long pressRel = (long)(_dbgPlayerPressMs - _dbgSequenceStartMs);
            long offset   = (long)_dbgPlayerPressMs  - (long)_dbgPerfectWindowMs;
            string sign   = offset >= 0 ? "+" : "";
            string tag    = offset >= 0 ? "late" : "early";
            Ln($"    Pressed @   : +{pressRel} ms", active);
            Ln($"    Offset      : {sign}{offset} ms  ({tag})", active);
        }
        else
        {
            Ln("    Pressed @   : —  (no input yet)", dimWhite);
            Ln("    Offset      : —", dimWhite);
        }

        Ln($"    Accuracy    : {_dbgLastAccuracy:F3}   (0 = perfect, 1 = edge)", dimWhite);

        // ── Per-resolution ────────────────────────────────────────────────────
        if (_dbgResolutionMs > 0)
        {
            Ln("  RESULT", heading);
            Ln($"    Final       : {_dbgLastResult}", dimWhite);
            long seqDur = (long)(_dbgResolutionMs - _dbgSequenceStartMs);
            Ln($"    Seq dur     : {seqDur} ms", dimWhite);
        }

        Ln("──────────────────────────────────────────", heading);
    }
}
