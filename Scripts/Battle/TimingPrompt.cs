using Godot;

/// <summary>
/// A timing prompt visual — a ring that starts large and closes toward a smaller target
/// ring at the center. The player presses the input button when the moving ring reaches
/// the target ring; the result is evaluated by how close the two rings are at that moment.
///
/// Movement:
///   Regular  — ease-in inward (slow start, fast finish). The circle is moving fastest
///              at the hit window, giving the player time to read before urgency builds.
///              The ring continues OvershootDistance past the target before auto-missing.
///   Bouncing — ease-out outward (fast launch, decelerates to zero at peak), then
///              ease-in returning (accelerates back inward). Mimics physical bounce feel.
///
/// Both are driven by a t value (0–1) sampled through a Godot Curve, giving full
/// control over feel. Quadratic easing is used as a fallback when no curve is assigned.
/// </summary>
public partial class TimingPrompt : Node2D
{
    // -------------------------------------------------------------------------
    // Result enum
    // -------------------------------------------------------------------------

    public enum InputResult
    {
        Perfect,  // within the inner fraction of the hit window
        Hit,      // within HitWindowSize but outside the perfect zone
        Miss,     // outside HitWindowSize, or ring completes travel without input
    }

    // -------------------------------------------------------------------------
    // Exported properties
    // -------------------------------------------------------------------------

    /// <summary>Total time in seconds for one pass (inward or outward).</summary>
    [Export] public float Duration = 1.0f;

    /// <summary>
    /// Easing curve for inward movement (circle closing toward target).
    /// X = normalised time (0–1), Y = normalised position along the travel range (0–1).
    /// Should have an ease-in shape: starts low, accelerates toward 1.
    /// Falls back to quadratic ease-in (t²) if null.
    /// </summary>
    [Export] public Curve InCurve;

    /// <summary>
    /// Easing curve for outward movement (circle bouncing away from target).
    /// Should have an ease-out shape: starts high, decelerates toward 0.
    /// Falls back to quadratic ease-out (1-(1-t)²) if null.
    /// </summary>
    [Export] public Curve OutCurve;

    /// <summary>Radius tolerance around the target ring that counts as a hit.</summary>
    [Export] public float HitWindowSize = 20.0f;

    /// <summary>
    /// How far past the target ring the moving ring travels before the prompt
    /// auto-resolves as a Miss. Gives the player a brief grace window after the
    /// ideal moment without letting the ring disappear immediately.
    /// </summary>
    [Export] public float OvershootDistance = 20.0f;

    /// <summary>
    /// Number of bounces after the first inward pass.
    /// 0 = standard prompt (one inward close, no bounce).
    /// 1+ = bouncing prompt (close → bounce out → close again, repeated BounceCount times).
    /// </summary>
    [Export] public int BounceCount = 0;

    /// <summary>
    /// Assign an AudioStreamPlayer to play a sound on Miss.
    /// Leave unassigned until audio assets are ready — the call is safely no-op when null.
    /// </summary>
    [Export] public AudioStreamPlayer MissSoundPlayer;

    // DEV ONLY ----------------------------------------------------------------
    /// <summary>
    /// When true, the prompt automatically resets one second after resolving.
    /// For development testing only — disable before shipping.
    /// </summary>
    [Export] public bool AutoLoop = true;
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Signals
    // -------------------------------------------------------------------------

    /// <summary>
    /// Emitted when the prompt is fully resolved — either by a player input or by the
    /// circle completing all passes without input (auto-miss). Cast result to InputResult.
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
    //   • Drive result feedback (flash, colour shift) via a shader uniform or
    //     AnimationPlayer rather than the _flashColor tint below.
    //   • StartRadius, TargetRadius, and OvershootDistance will still be needed
    //     to configure scale and travel range.
    // --------------------------------------------------------------------------

    private const float StartRadius       = 120f;
    private const float TargetRadius      = 28f;
    private const float RingLineWidth     = 4f;
    private const float PerfectWindowFraction = 0.25f;

    private static readonly Color ColorTarget       = new Color(1.0f, 1.0f, 1.0f, 0.90f);
    private static readonly Color ColorMovingRing   = new Color(1.0f, 1.0f, 1.0f, 1.00f);
    private static readonly Color ColorHitWindow    = new Color(1.0f, 1.0f, 0.3f, 0.30f);
    private static readonly Color ColorPerfectZone  = new Color(0.3f, 1.0f, 0.5f, 0.40f);
    private static readonly Color ColorFlashPerfect = new Color(0.3f, 1.0f, 0.4f, 1.00f);  // green
    private static readonly Color ColorFlashHit     = new Color(1.0f, 0.9f, 0.2f, 1.00f);  // yellow
    private static readonly Color ColorFlashMiss    = new Color(1.0f, 0.2f, 0.2f, 1.00f);  // red

    private const float FlashDuration    = 0.25f;
    private const float ShakeDuration    = 0.30f;
    private const float ShakeIntensity   = 6f;

    // -------------------------------------------------------------------------
    // Runtime state
    // -------------------------------------------------------------------------

    private float   _t                = 0f;    // normalised time within the current pass (0–1)
    private bool    _movingInward     = true;
    private int     _bouncesRemaining;
    private bool    _resolved         = false;
    private float   _currentRadius;

    private Color   _movingRingColor  = new Color(1.0f, 1.0f, 1.0f, 1.00f);
    private float   _flashTimer       = 0f;

    private Vector2 _shakeOrigin;
    private float   _shakeTimer       = 0f;

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public override void _Ready()
    {
        _shakeOrigin      = Position;
        _bouncesRemaining = BounceCount;
        _currentRadius    = StartRadius;
        _movingRingColor  = ColorMovingRing;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // Flash timer — fades the result colour back to neutral.
        if (_flashTimer > 0f)
        {
            _flashTimer -= dt;
            if (_flashTimer <= 0f)
                _movingRingColor = ColorMovingRing;
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

        _t = Mathf.Clamp(_t + dt / Duration, 0f, 1f);

        // The inward pass travels from StartRadius all the way to
        // (TargetRadius - OvershootDistance), continuing past the target ring
        // so the player has a moment to input even after the ideal hit window.
        float inwardEndpoint = TargetRadius - OvershootDistance;
        _currentRadius = _movingInward
            ? Mathf.Lerp(StartRadius,    inwardEndpoint, SampleCurve(InCurve,  _t, easeIn: true))
            : Mathf.Lerp(TargetRadius,   StartRadius,    SampleCurve(OutCurve, _t, easeIn: false));

        QueueRedraw();

        if (_t >= 1f)
            OnPassComplete();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Evaluates the player's input against the current ring position.
    /// Called by BattleSystem when the player presses the input button.
    /// Resolves the prompt immediately and emits PromptCompleted.
    /// </summary>
    public InputResult EvaluateInput()
    {
        if (_resolved) return InputResult.Miss;

        float distance = Mathf.Abs(_currentRadius - TargetRadius);

        InputResult result = distance <= HitWindowSize * PerfectWindowFraction ? InputResult.Perfect
                           : distance <= HitWindowSize                          ? InputResult.Hit
                                                                                : InputResult.Miss;
        Resolve(result);
        return result;
    }

    /// <summary>
    /// Reverses the prompt's direction, swapping the active curve.
    /// Called automatically when an inward pass completes and bounces remain.
    /// Can also be called externally by BattleSystem to force an early reversal.
    /// </summary>
    public void Bounce()
    {
        _t            = 0f;
        _movingInward = !_movingInward;
        _bouncesRemaining--;
        QueueRedraw();
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------

    public override void _Draw()
    {
        // Hit window band (yellow tint) — shows the acceptable input zone.
        DrawBand(TargetRadius, HitWindowSize, ColorHitWindow);

        // Perfect window band (green tint) — inner zone within the hit window.
        DrawBand(TargetRadius, HitWindowSize * PerfectWindowFraction, ColorPerfectZone);

        // Target ring (static white).
        DrawArc(Vector2.Zero, TargetRadius, 0f, Mathf.Tau, 64, ColorTarget, RingLineWidth);

        // Moving ring — tinted by _movingRingColor to show result feedback.
        DrawArc(Vector2.Zero, Mathf.Max(0f, _currentRadius), 0f, Mathf.Tau, 64, _movingRingColor, RingLineWidth);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Samples a Godot Curve at offset t, falling back to quadratic easing if null.
    /// Ease-in fallback: t². Ease-out fallback: 1-(1-t)².
    /// </summary>
    private static float SampleCurve(Curve curve, float t, bool easeIn)
    {
        if (curve != null)
            return curve.Sample(t);

        return easeIn ? t * t : 1f - (1f - t) * (1f - t);
    }

    /// <summary>
    /// Called when a full pass (t reaches 1).
    /// Starts the next bounce if any remain; otherwise auto-resolves as Miss.
    /// </summary>
    private void OnPassComplete()
    {
        if (_movingInward)
        {
            if (_bouncesRemaining > 0)
            {
                Bounce();  // launch outward pass
            }
            else
            {
                Resolve(InputResult.Miss);  // ring passed all the way through — auto miss
            }
        }
        else
        {
            // Completed the outward (bounce) pass — begin the return inward pass.
            _t            = 0f;
            _movingInward = true;
        }
    }

    private void Resolve(InputResult result)
    {
        if (_resolved) return;
        _resolved = true;

        // Apply result-specific visual feedback.
        switch (result)
        {
            case InputResult.Perfect:
                _movingRingColor = ColorFlashPerfect;
                _flashTimer      = FlashDuration;
                break;

            case InputResult.Hit:
                _movingRingColor = ColorFlashHit;
                _flashTimer      = FlashDuration;
                break;

            case InputResult.Miss:
                _movingRingColor = ColorFlashMiss;
                _flashTimer      = FlashDuration;
                // Screen shake.
                _shakeOrigin = Position;
                _shakeTimer  = ShakeDuration;
                // Placeholder sound — assign MissSoundPlayer in the inspector and set
                // its AudioStream when audio assets are ready.
                MissSoundPlayer?.Play();
                break;
        }

        QueueRedraw();
        EmitSignal(SignalName.PromptCompleted, (int)result);

        // DEV ONLY: auto-reset for test loop.
        if (AutoLoop)
            GetTree().CreateTimer(1.0).Timeout += ResetPrompt;
    }

    /// <summary>
    /// Resets the prompt to its initial state for another pass.
    /// DEV ONLY — called by the AutoLoop timer; not part of the shipping battle flow.
    /// </summary>
    private void ResetPrompt()
    {
        _t                = 0f;
        _movingInward     = true;
        _bouncesRemaining = BounceCount;
        _resolved         = false;
        _currentRadius    = StartRadius;
        _movingRingColor  = ColorMovingRing;
        _flashTimer       = 0f;
        // Restore position in case shake was still running when reset fired.
        _shakeTimer       = 0f;
        Position          = _shakeOrigin;
        QueueRedraw();
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
}
