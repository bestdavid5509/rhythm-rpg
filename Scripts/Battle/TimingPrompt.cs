using Godot;

/// <summary>
/// A timing prompt visual — a ring that starts large and closes toward a smaller target
/// ring at the center. The player presses the input button when the moving ring reaches
/// the target ring; the result is evaluated by how close the two rings are at that moment.
///
/// Prompt types:
///   Standard — white ring, ease-in inward, standard speed and hit window.
///   Slow     — blue ring, half speed, slightly larger hit window.
///   Bouncing — pink → orange → white across three inward passes; BounceCount forced to 2.
///              The sequence is scripted — the ring always bounces on schedule regardless
///              of player input. Each pass evaluates independently: hit = player deals
///              damage, miss = player takes damage. Color shift signals the final pass.
///
/// Movement:
///   Inward  — ease-in (slow start, fast finish). The ring is moving fastest at the hit
///             window, giving the player time to read before urgency builds. Continues
///             OvershootDistance past the target before auto-missing.
///   Outward — ease-out (fast launch, decelerates to zero at peak). Mimics a bounce.
///
/// Both driven by a t value (0–1) sampled through a Godot Curve, falling back to
/// quadratic easing when no curve is assigned.
/// </summary>
public partial class TimingPrompt : Node2D
{
    // -------------------------------------------------------------------------
    // Enums
    // -------------------------------------------------------------------------

    public enum InputResult
    {
        Perfect,  // within the inner fraction of the hit window
        Hit,      // within HitWindowSize but outside the perfect zone
        Miss,     // outside HitWindowSize, or ring completes travel without input
    }

    public enum PromptType
    {
        Standard,  // white ring, default speed and window
        Slow,      // blue ring, half speed, wider window
        Bouncing,  // pink→orange→white; scripted three-pass sequence, BounceCount = 2
    }

    // -------------------------------------------------------------------------
    // Exported properties
    // -------------------------------------------------------------------------

    /// <summary>
    /// Determines the visual style, speed, and bounce behaviour of this prompt.
    /// Automatically configures Duration, HitWindowSize, and BounceCount on _Ready.
    /// </summary>
    [Export] public PromptType Type = PromptType.Standard;

    /// <summary>
    /// Total time in seconds for one pass (inward or outward).
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
    /// Radius tolerance around the target ring that counts as a hit.
    /// Set automatically by Type — override only when driving directly from BattleSystem.
    /// </summary>
    [Export] public float HitWindowSize = 20.0f;

    /// <summary>
    /// How far past the target ring the ring travels before auto-missing.
    /// Gives a brief grace window after the ideal hit moment.
    /// </summary>
    [Export] public float OvershootDistance = 20.0f;

    /// <summary>
    /// The outward pass always completes in exactly this many seconds, regardless of
    /// where in the hit window the player pressed (or whether the ring auto-missed).
    /// Because the outward lerp starts from _currentRadius at the moment of bounce,
    /// a hit near the target travels less distance and moves slower in pixels/second,
    /// while an edge hit or auto-miss travels more distance and moves faster —
    /// but both arrive back at StartRadius at the same wall-clock time.
    /// This makes Perfect@ timestamps on subsequent passes fully predictable.
    /// </summary>
    [Export] public float FixedReturnDuration = 0.5f;

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
    //   • StartRadius, TargetRadius, and OvershootDistance will still be needed
    //     to configure scale and travel range.
    // --------------------------------------------------------------------------

    private const float StartRadius           = 120f;
    private const float TargetRadius          = 28f;
    private const float RingLineWidth         = 4f;
    private const float PerfectWindowFraction = 0.25f;

    // Shared UI colors
    private static readonly Color ColorTarget      = new Color(1.00f, 1.00f, 1.00f, 0.90f);
    private static readonly Color ColorHitWindow   = new Color(1.00f, 1.00f, 0.30f, 0.30f);
    private static readonly Color ColorPerfectZone = new Color(0.30f, 1.00f, 0.50f, 0.40f);

    // Per-type ring colors
    private static readonly Color ColorStandard    = new Color(1.00f, 1.00f, 1.00f, 1.00f);  // white
    private static readonly Color ColorSlow        = new Color(0.35f, 0.65f, 1.00f, 1.00f);  // blue
    private static readonly Color ColorBouncePass1 = new Color(1.00f, 0.50f, 0.75f, 1.00f);  // pink
    private static readonly Color ColorBouncePass2 = new Color(1.00f, 0.60f, 0.20f, 1.00f);  // orange
    // Pass 3 of Bouncing uses ColorStandard (white) — signals final approach to the player

    // Result flash colors (always override type color)
    private static readonly Color ColorFlashPerfect = new Color(0.30f, 1.00f, 0.40f, 1.00f);  // green
    private static readonly Color ColorFlashHit     = new Color(1.00f, 0.90f, 0.20f, 1.00f);  // yellow
    private static readonly Color ColorFlashMiss    = new Color(1.00f, 0.20f, 0.20f, 1.00f);  // red

    // Type-specific timing
    private const float SlowDuration   = 2.0f;
    private const float SlowHitWindow  = 28.0f;

    private const float FlashDuration  = 0.15f;
    private const float ShakeDuration  = 0.30f;
    private const float ShakeIntensity = 6f;

    // -------------------------------------------------------------------------
    // Runtime state
    // -------------------------------------------------------------------------

    private float   _t                = 0f;  // normalised time within current pass (0–1)
    private bool    _movingInward     = true;
    private int     _bouncesRemaining;
    private int     _passIndex        = 0;   // which inward pass (0-based); drives bouncing colors
    private bool    _resolved         = false;
    private float   _currentRadius;

    private Color   _movingRingColor       = ColorStandard;
    private float   _flashTimer            = 0f;
    private bool    _showMovingRing        = true;

    // Scales _t advancement during the outward pass so it completes in FixedReturnDuration.
    // Set by Bounce(); always Duration / FixedReturnDuration (constant for a given session).
    private float   _bounceSpeedMultiplier = 1f;

    // The ring's radius at the moment Bounce() was called — used as the outward lerp start.
    // Varies with where the player pressed (or overshoot position), giving each bounce a
    // different pixel velocity while keeping the total outward time fixed.
    private float   _bounceStartRadius     = StartRadius;

    private Vector2 _shakeOrigin;
    private float   _shakeTimer       = 0f;

    // DEBUG ONLY ---------------------------------------------------------------
    private ulong       _dbgSequenceStartMs;      // ms timestamp when first pass began
    private ulong       _dbgPassStartMs;          // ms timestamp when current inward pass began
    private ulong       _dbgPerfectWindowMs;      // estimated ms when ring == TargetRadius
    private ulong       _dbgPlayerPressMs;        // ms when player pressed (0 = no press yet)
    private bool        _dbgPlayerPressedThisPass;
    private float       _dbgLastAccuracy;         // accuracy value at last Bounce() or Resolve()
    private float       _dbgLastBounceSpeed;      // _bounceSpeedMultiplier at last Bounce()
    private InputResult _dbgLastResult;           // result of the last full resolution
    private ulong       _dbgResolutionMs;         // ms when prompt fully resolved
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public override void _Ready()
    {
        _shakeOrigin = Position;
        ApplyTypeSettings();
        ResetState();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // Flash timer — holds result colour briefly, then either hides the ring (resolved)
        // or resets it to the current pass's base color (mid-sequence bounce).
        if (_flashTimer > 0f)
        {
            _flashTimer -= dt;
            if (_flashTimer <= 0f)
            {
                if (_resolved)
                    _showMovingRing = false;         // ring disappears after final flash
                else
                    _movingRingColor = GetBaseColor(); // mid-bounce: restore current pass color
            }
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

        if (Input.IsActionJustPressed("battle_confirm"))
            EvaluateInput();

        float speedMultiplier = _movingInward ? 1f : _bounceSpeedMultiplier;
        _t = Mathf.Clamp(_t + dt * speedMultiplier / Duration, 0f, 1f);

        // Inward pass travels from StartRadius through the target all the way to
        // (TargetRadius - OvershootDistance), giving the player a grace window after
        // the ideal hit moment before auto-miss triggers.
        float inwardEndpoint = TargetRadius - OvershootDistance;
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
    /// Standard / Slow: resolves the prompt immediately (hit or miss ends the sequence).
    ///
    /// Bouncing: input is only registered during inward passes and only within the hit
    /// window. A hit within the window triggers an early bounce at that moment. Input
    /// outside the window is ignored — the ring continues to the overshoot and auto-misses.
    /// Either way, the sequence always continues to the next pass.
    /// </summary>
    public InputResult EvaluateInput()
    {
        if (_resolved) return InputResult.Miss;

        // During outward (bounce) passes, input is not evaluated.
        if (Type == PromptType.Bouncing && !_movingInward)
            return InputResult.Miss;

        float distance = Mathf.Abs(_currentRadius - TargetRadius);

        if (Type == PromptType.Bouncing)
        {
            // Input outside the hit window is ignored — ring travels on to the overshoot
            // where OnPassComplete will auto-miss and continue the sequence.
            if (distance > HitWindowSize)
                return InputResult.Miss;

            InputResult result = distance <= HitWindowSize * PerfectWindowFraction
                ? InputResult.Perfect
                : InputResult.Hit;

            // DEBUG ONLY
            if (DebugMode && !_dbgPlayerPressedThisPass)
            {
                _dbgPlayerPressMs        = Time.GetTicksMsec();
                _dbgPlayerPressedThisPass = true;
            }

            EvaluatePassAndContinue(result);
            return result;
        }
        else
        {
            // Standard / Slow: any input immediately resolves the prompt.
            InputResult result = distance <= HitWindowSize * PerfectWindowFraction ? InputResult.Perfect
                               : distance <= HitWindowSize                          ? InputResult.Hit
                                                                                    : InputResult.Miss;

            // DEBUG ONLY
            if (DebugMode && !_dbgPlayerPressedThisPass)
            {
                _dbgPlayerPressMs        = Time.GetTicksMsec();
                _dbgPlayerPressedThisPass = true;
                _dbgLastAccuracy          = Mathf.Clamp(distance / HitWindowSize, 0f, 1f);
                _dbgLastBounceSpeed       = 0f;  // Standard/Slow has no bounce
            }

            Resolve(result);
            return result;
        }
    }

    /// <summary>
    /// Reverses the prompt's direction, swapping the active curve.
    /// Called automatically by OnPassComplete when bounces remain.
    /// Can also be called externally by BattleSystem to force an early reversal.
    /// </summary>
    public void Bounce()
    {
        // Capture where the ring is right now as the outward lerp start point.
        // This means a near-target hit travels less distance outward and moves slower
        // in px/s, while an edge hit or auto-miss travels more distance and moves faster —
        // but both complete in exactly FixedReturnDuration seconds because _bounceSpeedMultiplier
        // scales _t to always go 0→1 in FixedReturnDuration regardless of lerp range.
        _bounceStartRadius     = _currentRadius;
        _bounceSpeedMultiplier = Duration / FixedReturnDuration;

        // DEBUG ONLY
        if (DebugMode)
        {
            float pixelsToTravel  = StartRadius - _bounceStartRadius;
            float pixelSpeed      = pixelsToTravel / FixedReturnDuration;
            _dbgLastAccuracy      = Mathf.Clamp(Mathf.Abs(_currentRadius - TargetRadius) / HitWindowSize, 0f, 1f);
            _dbgLastBounceSpeed   = pixelSpeed;  // stored as px/s for readability in overlay
        }

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

        // Target ring (static white) — always visible.
        DrawArc(Vector2.Zero, TargetRadius, 0f, Mathf.Tau, 64, ColorTarget, RingLineWidth);

        // Moving ring — hidden after flash expires on a fully resolved prompt.
        if (_showMovingRing)
            DrawArc(Vector2.Zero, Mathf.Max(0f, _currentRadius), 0f, Mathf.Tau, 64, _movingRingColor, RingLineWidth);

        // DEBUG ONLY
        if (DebugMode) DrawDebugOverlay();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Handles the result of a single Bouncing pass and advances the sequence.
    /// Emits PassEvaluated, applies brief flash feedback, then either bounces
    /// (more passes remain) or fully resolves (final pass).
    /// The ring always continues — the sequence is scripted and consistent.
    /// </summary>
    private void EvaluatePassAndContinue(InputResult result)
    {
        // Flash — ring continues after (not resolved yet), so flash expiry restores base color.
        _movingRingColor = FlashColorFor(result);
        _flashTimer      = FlashDuration;
        QueueRedraw();

        EmitSignal(SignalName.PassEvaluated, (int)result, _passIndex);

        if (result == InputResult.Miss)
        {
            _shakeOrigin = Position;
            _shakeTimer  = ShakeDuration;
            MissSoundPlayer?.Play();
        }

        if (_bouncesRemaining > 0)
        {
            Bounce();  // more passes — launch outward
        }
        else
        {
            // Final pass complete — fully resolve.
            // DEBUG ONLY
            if (DebugMode) { _dbgLastResult = result; _dbgResolutionMs = Time.GetTicksMsec(); }

            _resolved = true;
            EmitSignal(SignalName.PromptCompleted, (int)result);
            if (AutoLoop)
                GetTree().CreateTimer(1.0).Timeout += ResetPrompt;
        }
    }

    /// <summary>
    /// Resolves a Standard or Slow prompt (single pass).
    /// Sets _resolved, applies flash, and emits PromptCompleted.
    /// </summary>
    private void Resolve(InputResult result)
    {
        if (_resolved) return;
        _resolved = true;

        _movingRingColor = FlashColorFor(result);
        _flashTimer      = FlashDuration;

        if (result == InputResult.Miss)
        {
            _shakeOrigin = Position;
            _shakeTimer  = ShakeDuration;
            MissSoundPlayer?.Play();
        }

        // DEBUG ONLY
        if (DebugMode)
        {
            _dbgLastResult    = result;
            _dbgResolutionMs  = Time.GetTicksMsec();
            _dbgLastAccuracy    = Mathf.Clamp(Mathf.Abs(_currentRadius - TargetRadius) / HitWindowSize, 0f, 1f);
            _dbgLastBounceSpeed = 0f;  // no outward pass after final resolution
        }

        QueueRedraw();
        EmitSignal(SignalName.PromptCompleted, (int)result);

        if (AutoLoop)
            GetTree().CreateTimer(1.0).Timeout += ResetPrompt;
    }

    /// <summary>
    /// Called when a pass (inward or outward) reaches t = 1.
    ///
    /// Inward: evaluates auto-miss and either continues (Bouncing) or resolves (Standard/Slow).
    /// Outward: advances to the next inward pass and updates the ring color for that pass.
    /// </summary>
    private void OnPassComplete()
    {
        if (_resolved) return;  // player input already resolved this pass

        if (_movingInward)
        {
            if (Type == PromptType.Bouncing)
            {
                // Ring reached overshoot without player input — auto-miss, sequence continues.
                EvaluatePassAndContinue(InputResult.Miss);
            }
            else
            {
                // Standard / Slow: ring passed all the way through — auto-miss resolves.
                Resolve(InputResult.Miss);
            }
        }
        else
        {
            // Outward pass complete — start the next inward pass and shift to its color.
            _t               = 0f;
            _movingInward    = true;
            _passIndex++;
            _movingRingColor = GetBaseColor();

            // DEBUG ONLY
            if (DebugMode) DbgStartNewInwardPass();
        }
    }

    /// <summary>
    /// Configures Duration, HitWindowSize, and BounceCount from the current Type.
    /// Called on _Ready and before each AutoLoop reset when type cycling is active.
    /// </summary>
    private void ApplyTypeSettings()
    {
        switch (Type)
        {
            case PromptType.Standard:
                Duration      = 1.0f;
                HitWindowSize = 20.0f;
                BounceCount   = 0;
                break;
            case PromptType.Slow:
                Duration      = SlowDuration;
                HitWindowSize = SlowHitWindow;
                BounceCount   = 0;
                break;
            case PromptType.Bouncing:
                Duration      = 1.0f;
                HitWindowSize = 20.0f;
                BounceCount   = 2;
                break;
        }
    }

    /// <summary>
    /// Returns the base ring color for the current type and pass index.
    /// Result flash colors are applied separately in Resolve/EvaluatePassAndContinue
    /// and always override this.
    /// </summary>
    private Color GetBaseColor()
    {
        return Type switch
        {
            PromptType.Slow     => ColorSlow,
            PromptType.Bouncing => _passIndex switch
            {
                0 => ColorBouncePass1,  // pink   — first approach
                1 => ColorBouncePass2,  // orange — second approach
                _ => ColorStandard,     // white  — final approach, signals last chance
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
        _t                = 0f;
        _movingInward     = true;
        _passIndex        = 0;
        _bouncesRemaining = BounceCount;
        _resolved         = false;
        _currentRadius    = StartRadius;
        _movingRingColor  = GetBaseColor();
        _flashTimer            = 0f;
        _showMovingRing        = true;
        _bounceSpeedMultiplier = 1f;
        _bounceStartRadius     = StartRadius;
        _shakeTimer            = 0f;
        Position               = _shakeOrigin;

        // DEBUG ONLY
        if (DebugMode)
        {
            _dbgSequenceStartMs      = Time.GetTicksMsec();
            _dbgResolutionMs         = 0;
            _dbgLastAccuracy         = 0f;
            _dbgLastBounceSpeed      = 1f;
            _dbgLastResult           = InputResult.Miss;
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
        // DEV ONLY: advance to next type so all three can be observed in one session.
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

    /// <summary>
    /// Records the start time and estimated perfect-window time for the new inward pass,
    /// and clears the player-press record. Called at sequence start and at each
    /// outward→inward transition.
    /// DEBUG ONLY.
    /// </summary>
    private void DbgStartNewInwardPass()
    {
        _dbgPassStartMs           = Time.GetTicksMsec();
        float tPerfect            = DbgSolveTForRadius(TargetRadius);
        _dbgPerfectWindowMs       = _dbgPassStartMs + (ulong)(tPerfect * Duration * 1000f);
        _dbgPlayerPressedThisPass = false;
        _dbgPlayerPressMs         = 0;
    }

    /// <summary>
    /// Binary-searches for the t value (0–1) at which the inward-pass lerp produces
    /// the given target radius. Used to predict the perfect input moment.
    /// DEBUG ONLY.
    /// </summary>
    private float DbgSolveTForRadius(float targetR)
    {
        float inwardEndpoint = TargetRadius - OvershootDistance;
        float lo = 0f, hi = 1f;
        for (int i = 0; i < 20; i++)
        {
            float mid = (lo + hi) * 0.5f;
            float r   = Mathf.Lerp(StartRadius, inwardEndpoint, SampleCurve(InCurve, mid, easeIn: true));
            if (r > targetR) lo = mid; else hi = mid;
        }
        return (lo + hi) * 0.5f;
    }

    /// <summary>
    /// Draws a readable debug overlay at the top-left corner of the screen,
    /// anchored to _shakeOrigin so it stays fixed during screen shake.
    /// Displays per-frame, per-pass, and per-resolution diagnostics.
    /// DEBUG ONLY — called from _Draw() only when DebugMode is true.
    /// </summary>
    private void DrawDebugOverlay()
    {
        Font font = ThemeDB.Singleton.FallbackFont;
        if (font == null) return;

        const int   fontSize = 14;
        const float lineH    = 19f;

        Color dimWhite = new Color(1.00f, 1.00f, 1.00f, 0.80f);
        Color active   = new Color(0.40f, 1.00f, 0.65f, 1.00f);
        Color heading  = new Color(1.00f, 0.85f, 0.30f, 1.00f);

        // Anchor to screen top-left regardless of node position or active shake.
        float x = -_shakeOrigin.X + 12f;
        float y = -_shakeOrigin.Y + 20f;

        // Local helper — draws one line then advances y.
        // Captures x, y, font, fontSize by reference (C# local function semantics).
        void Ln(string text, Color col)
        {
            DrawString(font, new Vector2(x, y), text, HorizontalAlignment.Left, -1, fontSize, col);
            y += lineH;
        }

        // ── Header ────────────────────────────────────────────────────────────
        Ln("─── TimingPrompt Debug ───────────────────", heading);

        // ── Per-frame ─────────────────────────────────────────────────────────
        Ln("  FRAME", heading);
        Ln($"    Radius      : {_currentRadius:F1} px   (target {TargetRadius:F0})", dimWhite);
        Ln($"    t           : {_t:F3}", dimWhite);
        Ln($"    Direction   : {(_movingInward ? "Inward" : "Outward")}", dimWhite);

        // ── Per-pass ──────────────────────────────────────────────────────────
        int totalPasses = BounceCount + 1;
        Ln($"  PASS  {_passIndex + 1} / {totalPasses}   [{Type}]", heading);

        long passStartRel  = (long)(_dbgPassStartMs    - _dbgSequenceStartMs);
        long perfectRel    = (long)(_dbgPerfectWindowMs - _dbgSequenceStartMs);
        Ln($"    Started     : +{passStartRel} ms", dimWhite);
        Ln($"    Perfect @   : +{perfectRel} ms", dimWhite);

        if (_dbgPlayerPressedThisPass)
        {
            long pressRel = (long)(_dbgPlayerPressMs  - _dbgSequenceStartMs);
            long offset   = (long)_dbgPlayerPressMs   - (long)_dbgPerfectWindowMs;
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

        Ln($"    Accuracy    : {_dbgLastAccuracy:F3}   (0 = perfect, 1 = edge/overshoot)", dimWhite);
        Ln($"    Outward spd : {_dbgLastBounceSpeed:F1} px/s   (fixed return: {FixedReturnDuration:F2}s)", dimWhite);

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
