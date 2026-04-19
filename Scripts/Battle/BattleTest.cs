using System;
using Godot;

/// <summary>
/// Battle prototype — core turn loop, signal routing, and combat state.
/// Split into three partial-class files:
///   BattleTest.cs     — state machine, lifecycle, turn phases, shared helpers, tween helpers
///   BattleAnimator.cs — sprite setup, animation callbacks, dead-flag guards, end-of-battle overlay
///   BattleMenu.cs     — menu construction, navigation, and input
///
/// Animation flow per turn:
///   1. Setup    — attacker hops to close stance, camera zooms in (ease-out, lunge feel).
///   2. Prompt   — per-pass slams driven by PassEvaluated (works for all prompt types).
///   3. Teardown — attacker hops back to origin, camera zooms out (ease-in, snapping away).
///
/// Core principle: input and damage are always simultaneous — the button press IS the strike.
///
/// Damage model:
///   Enemy attack — Miss per pass  → player takes 10 (unblocked strike)
///   Enemy attack — Hit or Perfect → 0 damage to player (strike blocked)
///   Perfect parry (all passes)    → enemy takes 20 (automatic counter)
///   Player attack damage is computed by ComputePlayerDamage(BaseDamage, result):
///     Perfect → RoundToInt(BaseDamage × 1.5)    Hit → BaseDamage    Miss → RoundToInt(BaseDamage × 0.5)
///   Basic attack (BaseDamage 10): Perfect=15  Hit=10  Miss=5 — single pass, always resolves
///   Combo strike (BaseDamage  6): Perfect= 9  Hit= 6  Miss=3 — per pass; combo ends on first miss
///   Magic attack (BaseDamage 10): Perfect=15  Hit=10  Miss=5 — per pass, always plays through
/// </summary>
public partial class BattleTest : Node2D
{
    // =========================================================================
    // State machine
    // =========================================================================

    private enum BattleState { EnemyAttack, PlayerMenu, PlayerAttack, GameOver }
    private BattleState _state = BattleState.EnemyAttack;

    // True during attack resolution phases (slash animations, retreat, teardown) when all
    // input must be ignored. Set when the last timing circle resolves; cleared when the
    // next input-accepting state begins (ShowMenu or BeginEnemyAttack with active prompts).
    private bool _inputLocked;

    // =========================================================================
    // HP
    // =========================================================================

    private const int PlayerMaxHP      = 100;
    private const int EnemyMaxHPDefault = 200;

    private int _playerHP    = PlayerMaxHP;
    private int _enemyHP     = EnemyMaxHPDefault;
    private int _enemyMaxHP  = EnemyMaxHPDefault;  // overridden by EnemyData.MaxHp when set

    // =========================================================================
    // MP
    // =========================================================================

    [Export] public int PlayerMaxMp = 50;

    private int _playerMp;  // initialized to PlayerMaxMp in _Ready

    // UI bar references — built in BuildStatusPanels(), updated by UpdateHPBars()/UpdateMpBar().
    private ColorRect _playerHPFill;
    private ColorRect _enemyHPFill;
    private ColorRect _playerMPFill;
    private Label     _playerHPLabel;
    private Label     _enemyHPLabel;
    private Label     _enemyNameLabel;  // rewritten on phase transition
    private Label     _playerMPLabel;

    // =========================================================================
    // Perfect parry
    // =========================================================================

    // Set true at the start of each enemy attack, cleared to false on any Miss result.
    // Checked when PromptCompleted fires to decide whether to trigger the auto counter.
    private bool _parryClean;

    // Set true when the player picks Defend from the main menu. While true, miss damage
    // from enemy passes is halved. Cleared at the start of each player menu turn.
    private bool _playerDefending;

    // True once the player has absorbed the enemy's learnable move via perfect parry.
    // Prevents the absorption moment from triggering more than once per fight.
    private bool _hasAbsorbedLearnableMove = false;

    // Set true by the Beckon ability; consumed on the next SelectEnemyAttack call to
    // force the enemy to use their LearnableAttack instead of a random pool selection.
    private bool _beckoning;

    // =========================================================================
    // Damage numbers
    // =========================================================================

    // Colors match the input result that caused the damage.
    private static readonly Color DmgColorPerfect = new Color(0.30f, 1.00f, 0.40f, 1.00f);  // green
    private static readonly Color DmgColorHit     = new Color(1.00f, 1.00f, 1.00f, 1.00f);  // white
    private static readonly Color DmgColorMiss    = new Color(0.60f, 0.60f, 0.60f, 1.00f);  // grey (weak hit)
    private static readonly Color DmgColorPlayer  = new Color(1.00f, 0.25f, 0.25f, 1.00f);  // red

    // Spawn points — centered on each sprite, just above its top edge.
    // PlayerSprite: offset_left=390, offset_right=490 → center X=440,  top Y=590
    // EnemySprite:  offset_left=1420, offset_right=1540 → center X=1480, top Y=550
    private static readonly Vector2 PlayerDamageOrigin = new Vector2(440f,  570f);
    private static readonly Vector2 EnemyDamageOrigin  = new Vector2(1480f, 530f);

    // =========================================================================
    // Prompt management
    // =========================================================================

    // When true, the enemy does not hop in to close stance before attacking.
    // Use for large/stationary enemies that hold their ground (e.g. 8 Sword Warrior).
    // Slam tweens are also skipped — the cross-screen lunge would look wrong for a non-hopping attacker.
    [Export] public bool SkipHopIn = true;

    // =========================================================================
    // Phase transition (Phase 1 → Phase 2)
    // =========================================================================

    // When assigned, the enemy's death triggers the Phase 2 reveal sequence and swaps
    // to this EnemyData instead of ending the battle. Null disables the transition.
    [Export] public EnemyData Phase2EnemyData;

    // When true, skip the reveal animation + dialogue entirely and swap directly to
    // Phase 2 once the Phase 1 death animation completes. Test hook.
    [Export] public bool SkipPhaseTransition = false;

    // When true, start the battle with the enemy at 1 HP so the first player hit
    // kills the warrior and triggers the Phase 1 → Phase 2 transition immediately.
    // Test hook — lets us exercise the full reveal sequence without playing through
    // the whole Phase 1 fight.
    [Export] public bool TestPhaseTransition = false;

    private bool             _phaseTransitionConsumed;  // point-of-no-return flag; set at the top of ApplyPhase2Sprite. IsPhaseTransitionPending returns false once true.
    private bool             _phase2SpriteApplied;      // guards the early sprite swap from running twice
    private bool             _phase2Finalised;          // guards SwapToPhase2 state-finalisation from running twice
    private AnimatedSprite2D _revealSprite;             // one-off boss reveal animation sprite
    private int              _enemyZIndexBeforeReveal;  // warrior ZIndex snapshot for restore in ApplyPhase2Sprite

    /// <summary>
    /// Inspector-assigned enemy attack resource. Overrides BattleSystem's built-in
    /// TestAttackPath when set. Leave null to use the default.
    /// </summary>
    [Export] public AttackData TestEnemyAttack;

    /// <summary>
    /// When true, the enemy always uses TestEnemyAttack regardless of any future
    /// attack selection logic. The turn loop itself is unchanged.
    /// </summary>
    [Export] public bool LoopAttack;

    /// <summary>
    /// Enemy definition — attack pool, HP, and selection strategy.
    /// When set, MaxHp overrides the default enemy HP and AttackPool drives per-turn attack selection.
    /// Assign via the inspector (e.g. 8_sword_warrior_phase2.tres).
    /// </summary>
    [Export] public EnemyData EnemyData;

    private BattleMessage  _battleMessage;
    private ShaderMaterial _enemyFlashMaterial;
    private Tween          _enemyFlashTween;

    private TimingPrompt     _activePrompt;
    private PackedScene      _timingPromptScene;
    private BattleSystem     _battleSystem;
    private AnimatedSprite2D _enemyAnimSprite;
    private AnimatedSprite2D _playerAnimSprite;
    private TargetZone       _targetZone;       // shared target ring — shown for the duration of any prompt sequence

    // =========================================================================
    // Characters and animations
    // =========================================================================

    private ColorRect _playerSprite;
    private ColorRect _enemySprite;
    private Vector2   _playerOrigin;          // ColorRect position at scene load — positioning math anchor
    private Vector2   _enemyOrigin;
    private Vector2   _playerAnimSpriteOrigin; // AnimatedSprite2D position after floor-anchoring in _Ready
    private Vector2   _enemyAnimSpriteOrigin;  // AnimatedSprite2D position after floor-anchoring in _Ready

    // Set at the start of each attack turn; used by the shared animation helpers.
    private ColorRect _attacker;
    private ColorRect _defender;
    private Vector2   _attackerClosePos;  // close-but-not-touching stance position for this turn
    private bool      _pendingGameOver;   // cached result of CheckGameOver(); read by OnFinalSlashFinished
    private bool      _playerDead;        // true once player death animation begins; guards all subsequent sprite calls
    private bool      _enemyDead;         // true once enemy death animation begins; guards all subsequent sprite calls
    private bool      _isComboAttack;       // true when the current player turn uses Combo Strike (Bouncing prompt)
    private bool      _isPlayerMagicAttack; // true when the current player turn uses a magic attack via BattleSystem
    private bool      _isPlayerHealAttack;  // true when the current player turn uses Cure (heal self instead of damage enemy)
    private int       _comboPassIndex;      // which Bouncing pass just resolved; set in OnAttackPassEvaluated

    // Loaded once in _Ready; used to restore the enemy attack after a player magic turn.
    private AttackData _enemyAttackData;
    private int        _lastAttackIndex = -1;  // tracks last AttackSelector pick for Sequential support
    private AttackData _playerMagicAttack;
    private AttackData _playerBasicAttack;   // player_basic_attack.tres — Physical, BaseDamage 10
    private AttackData _playerComboStrike;   // player_combo_strike.tres — Physical, BaseDamage 6
    private AttackData _absorbedMoveAttack;  // loaded on absorption; null until then
    private AttackData _playerCureAttack;    // player_cure.tres — Magic, BaseDamage 30 (used as heal amount)
    private AttackData _playerEtherEffect;   // player_ether_combo.tres — active variant for Ether item visual

    // Set before BeginPlayerMagicAttack() to select which magic attack the cast flow uses.
    // OnPlayerCastFinished reads this instead of _playerMagicAttack directly.
    private AttackData _activeMagicAttack;

    // Set at the start of each combo turn; cleared in BeginPlayerAttack and BeginComboMissRetreat.
    // When true, OnComboPassNSlashFinished skips the wind-up hold and triggers the retreat instead.
    private bool       _comboMissed;

    // Cached in BeginPlayerMagicAttack; consumed by OnPlayerCastFinished once the
    // cast animation completes and it is safe to start the sequence.
    private Vector2    _playerMagicDefenderCenter;
    private Vector2    _playerMagicPromptPos;

    // Hop-in coordination — two-flag rendezvous so ProceedAfterHopInAnim fires only after
    // BOTH the sequence completes AND the attack animation finishes (whichever is last).
    // Reset at the start of each hop-in turn in BeginEnemyAttack.
    private bool      _hopInSequenceCompleted; // set by OnEnemySequenceCompleted
    private bool      _hopInAnimFinished;      // set by OnEnemyAttackAnimFinished
    private bool      _hopInOver;              // cached CheckGameOver() result from OnEnemySequenceCompleted

    // Bouncing hop-in replay — subscribed during Bouncing hop-in steps to replay the
    // enemy animation on each inward pass. Unsubscribed when the sequence completes.
    private AttackStep _bouncingHopInStep;
    private float      _bouncingHopInAnimDelay;
    private bool       _bouncingHopInSubscribed;

    // Camera — created in _Ready; controls zoom and pan during combat close-ups.
    private Camera2D  _camera;
    private static readonly Vector2 CameraDefaultPos  = new Vector2(960f, 540f);
    private static readonly Vector2 CameraDefaultZoom = Vector2.One;
    // 2.0x zoom-in: visible viewport = 1920/2.0 = 960px wide, half = 480.
    // Worst-case midpoint offset from canvas center (~520 when step.Offset.X = -200)
    // still leaves a 40px margin from the left canvas edge, so the grey background
    // is never revealed during hop-in attacks regardless of attacker offset.
    private static readonly Vector2 CameraZoomIn      = new Vector2(2.0f, 2.0f);

    // Camera shake — delta-based so the offset feels organic rather than linear.
    // Shake is applied to _camera.Offset (not _camera.Position) so it operates on a
    // completely separate property from the position tweens in PlayHopIn/PlayTeardown.
    // No undo-last-frame bookkeeping needed: _camera.Offset is written fresh each frame
    // and zeroed when the shake expires.
    private float   _shakeIntensity;       // peak pixel radius of random offset
    private float   _shakeTimeRemaining;   // seconds remaining in current shake
    private float   _shakeDurationTotal;   // total duration of current shake (for fade-out)

    // Animation durations (seconds).
    private const float SetupDuration    = 0.35f;  // hop in + zoom in
    private const float TeardownDuration = 0.35f;  // hop out + zoom out
    private const float SlamInDuration   = 0.08f;  // lunge onto defender
    private const float SlamOutDuration  = 0.12f;  // pull back to close stance

    // Spacing constants (pixels).
    private const float FloorY      = 750f;  // world-space Y of the ground line; characters anchor their feet here
    private const float AttackGap   = 200f;  // gap between attacker and defender in close stance
    private const float SlamOverlap = 20f;   // how far attacker overlaps defender on a slam

    // =========================================================================
    // Lifecycle
    // =========================================================================

    public override void _Ready()
    {
        _timingPromptScene = GD.Load<PackedScene>("res://Scenes/Battle/TimingPrompt.tscn");

        _playerMp = PlayerMaxMp;
        BuildStatusPanels();
        _battleMessage = new BattleMessage(this);

        // Grab character sprites and record their original positions for teardown restoration.
        _playerSprite = GetNode<ColorRect>("PlayerSprite");
        _enemySprite  = GetNode<ColorRect>("EnemySprite");
        _playerOrigin = _playerSprite.Position;
        _enemyOrigin  = _enemySprite.Position;

        // Create a Camera2D centered at the viewport midpoint so the default view
        // matches the no-camera baseline: world (0,0)–(1920,1080) fully visible.
        _camera = new Camera2D();
        _camera.Name     = "BattleCamera";
        _camera.Position = CameraDefaultPos;
        _camera.Zoom     = CameraDefaultZoom;
        AddChild(_camera);

        // Player animated sprite — SpriteFrames built from separate per-animation PNGs.
        // BuildPlayerSpriteFrames returns the frame height (80px) for floor-anchored Y.
        // 0.5f factor: AnimatedSprite2D is center-anchored, so half the scaled height
        // offsets the center up so the bottom of the frame lands exactly on FloorY.
        _playerAnimSprite = GetNode<AnimatedSprite2D>("PlayerAnimatedSprite");
        int playerFrameH = BuildPlayerSpriteFrames();
        _playerAnimSprite.Scale    = new Vector2(3f, 3f);
        _playerAnimSprite.Position = new Vector2(_playerAnimSprite.Position.X,
                                                 FloorY - playerFrameH * 3f * 0.5f);
        _playerAnimSpriteOrigin    = _playerAnimSprite.Position;  // snapshot for teardown restoration
        _playerAnimSprite.Play("idle");  // OWNER: _Ready — scene init, no battle state yet

        // Enemy animated sprite — SpriteFrames built programmatically from the sheet.
        // Floor-anchored positioning: center Y = FloorY - (frameHeight * scale * 0.6)
        // places the sprite so its visual base sits on the ground line.
        // SpriteOffsetY is a per-enemy tuned nudge on top of the base formula.
        _enemyAnimSprite = GetNode<AnimatedSprite2D>("EnemyAnimatedSprite");
        BuildEnemySpriteFrames();
        _enemyAnimSprite.Scale    = new Vector2(3f, 3f);
        float enemyFh = EnemyData?.FrameHeight ?? 160;
        float enemyOffsetY = EnemyData?.SpriteOffsetY ?? 130f;
        _enemyAnimSprite.Position = new Vector2(_enemyAnimSprite.Position.X,
                                                FloorY - enemyFh * 3f * 0.6f + enemyOffsetY);
        _enemyAnimSpriteOrigin    = _enemyAnimSprite.Position;  // snapshot for teardown restoration
        _enemyAnimSprite.Play("idle");

        // White flash shader — used for learnable move signalling.
        var flashShader = GD.Load<Shader>("res://Assets/Shaders/WhiteFlash.gdshader");
        _enemyFlashMaterial = new ShaderMaterial();
        _enemyFlashMaterial.Shader = flashShader;
        _enemyFlashMaterial.SetShaderParameter("flash_amount", 0.0f);
        _enemyAnimSprite.Material = _enemyFlashMaterial;

        _battleSystem = new BattleSystem();
        AddChild(_battleSystem);  // triggers BattleSystem._Ready, which loads _currentAttack

        // Inspector-assigned attack overrides BattleSystem's built-in TestAttackPath.
        if (TestEnemyAttack != null)
            _battleSystem.SetAttack(TestEnemyAttack);

        _enemyAttackData  = _battleSystem.GetCurrentAttack();  // cache for restoration after player turns
        _playerMagicAttack = GD.Load<AttackData>("res://Resources/Attacks/player_magic_attack.tres");
        if (_playerMagicAttack == null)
            GD.PrintErr("[BattleTest] Failed to load player_magic_attack.tres");

        _playerBasicAttack = GD.Load<AttackData>("res://Resources/Attacks/player_basic_attack.tres");
        if (_playerBasicAttack == null)
            GD.PrintErr("[BattleTest] Failed to load player_basic_attack.tres");

        _playerComboStrike = GD.Load<AttackData>("res://Resources/Attacks/player_combo_strike.tres");
        if (_playerComboStrike == null)
            GD.PrintErr("[BattleTest] Failed to load player_combo_strike.tres");

        _playerCureAttack = GD.Load<AttackData>("res://Resources/Attacks/player_cure.tres");
        if (_playerCureAttack == null)
            GD.PrintErr("[BattleTest] Failed to load player_cure.tres");

        _playerEtherEffect = GD.Load<AttackData>("res://Resources/Attacks/player_ether_item_use.tres");
        if (_playerEtherEffect == null)
            GD.PrintErr("[BattleTest] Failed to load player_ether_item_use.tres");

        _battleSystem.StepStarted       += OnBattleSystemStepStarted;
        _battleSystem.StepPassEvaluated += OnBattleSystemStepPassEvaluated;
        _battleSystem.SequenceCompleted += OnSequenceCompleted;

        _targetZone = GetNode<TargetZone>("TargetZone");

        // Phase 2 fallback — if the scene didn't wire up Phase2EnemyData in the inspector,
        // load the default Phase 2 resource from disk so the transition works out of the box.
        if (Phase2EnemyData == null)
        {
            Phase2EnemyData = GD.Load<EnemyData>("res://Resources/Enemies/8_sword_warrior_phase2.tres");
            if (Phase2EnemyData == null)
                GD.PrintErr("[BattleTest] Failed to load default Phase2EnemyData (8_sword_warrior_phase2.tres).");
            else
                GD.Print("[BattleTest] Phase2EnemyData defaulted to 8_sword_warrior_phase2.tres.");
        }

        // EnemyData overrides the default max HP when assigned in the inspector.
        if (EnemyData != null && EnemyData.MaxHp > 0)
        {
            _enemyMaxHP = EnemyData.MaxHp;
            _enemyHP    = EnemyData.MaxHp;
            GD.Print($"[BattleTest] EnemyData \"{EnemyData.EnemyName}\" loaded — " +
                     $"MaxHp={EnemyData.MaxHp}, AttackPool={EnemyData.AttackPool?.Length ?? 0} attack(s).");
        }

        // Test hook: drop enemy HP to 1 so the first player hit triggers the phase
        // transition immediately. Applied AFTER EnemyData HP init so it isn't clobbered.
        // MaxHP is preserved so the HP bar shows the (nearly empty) correct scale.
        if (TestPhaseTransition)
        {
            _enemyHP = 1;
            GD.Print("[BattleTest] TestPhaseTransition active — enemy HP forced to 1.");
        }

        BuildMenu();
        UpdateHPBars();

        ShowMenu();
    }

    public override void _Input(InputEvent @event)
    {
        // Hard lock — active during attack resolution, retreat, and teardown.
        // No input of any kind is processed until the next input-accepting state begins.
        if (_inputLocked) return;

        // GameOver — all input dead.
        if (_state == BattleState.GameOver) return;

        switch (_state)
        {
            case BattleState.PlayerMenu:
                HandleMenuInput(@event);
                break;

            case BattleState.EnemyAttack:
            case BattleState.PlayerAttack:
                if (@event.IsActionPressed("battle_confirm"))
                    TimingPrompt.ConfirmAll();
                break;
        }
    }

    public override void _Process(double delta)
    {
        if (_shakeTimeRemaining <= 0f) return;

        _shakeTimeRemaining -= (float)delta;

        if (_shakeTimeRemaining <= 0f)
        {
            // Shake expired — zero the offset so no residual displacement remains.
            _shakeTimeRemaining = 0f;
            _camera.Offset      = Vector2.Zero;
            return;
        }

        // Scale intensity linearly down to zero as the shake decays, giving a natural
        // fade-out feel rather than cutting off abruptly.
        // _camera.Offset is written fresh each frame — no undo-last-frame step needed
        // because Offset is independent of the position tweens in PlayHopIn/PlayTeardown.
        float t = _shakeTimeRemaining / _shakeDurationTotal;
        _camera.Offset = new Vector2(
            (float)GD.RandRange(-_shakeIntensity, _shakeIntensity),
            (float)GD.RandRange(-_shakeIntensity, _shakeIntensity)
        ) * t;
    }

    // =========================================================================
    // Enemy attack phase
    // =========================================================================

    private void BeginEnemyAttack()
    {
        // Reentrancy guard — prevents cascading attack starts from stacked timer callbacks.
        if (_state == BattleState.EnemyAttack)
        {
            GD.Print("[BattleTest] BeginEnemyAttack skipped — already in EnemyAttack state.");
            return;
        }

        _state               = BattleState.EnemyAttack;
        _inputLocked         = false;  // Unlock input — enemy prompts are about to appear.
        _parryClean          = true;
        _isPlayerMagicAttack = false;
        _isPlayerHealAttack  = false;
        _isComboAttack       = false;   // Clear stale combo flag from previous player turn.
        TimingPrompt.SuppressInput = false;  // safety reset

        // Hard boundary: free any surviving player-attack prompt so its signals cannot
        // fire into the enemy sequence. The prompt may still be alive if FreeActivePrompt's
        // flash-duration timer hasn't fired yet.
        FreeActivePrompt();
        GD.Print("[BattleTest] Enemy attacks.");

        // SetAttack is always called because a preceding player magic turn may have
        // overridden _currentAttack with _playerMagicAttack.
        var selectedAttack = SelectEnemyAttack();
        _battleSystem.SetAttack(selectedAttack);

        // Signal the player when the enemy uses its learnable move (suppressed once absorbed).
        if (!_hasAbsorbedLearnableMove
            && EnemyData?.LearnableAttack != null
            && selectedAttack == EnemyData.LearnableAttack)
        {
            ShowLearnableSignal();
            FlashEnemyWhite();
        }

        if (_battleSystem.CurrentAttackIsHopIn)
        {
            // Reset rendezvous flags for this turn — both must be true before teardown runs.
            _hopInSequenceCompleted = false;
            _hopInAnimFinished      = false;
            _hopInOver              = false;

            // Melee hop-in attack — enemy lunges close, plays the melee animation timed to
            // the circle, then retreats once both the circle AND the animation have resolved.
            // Apply the first step's Offset to the close position so the .tres file can tune
            // where the enemy stands during the zoom-in (e.g. push further left to overlap the player).
            // Enemy animation is now driven per-step by OnBattleSystemStepStarted.
            // StartSequence triggers StepStarted for step 0 which plays the first animation.
            Vector2 hopInOffset = _battleSystem.GetFirstStep()?.Offset ?? Vector2.Zero;
            PlayHopIn(_enemySprite, _playerSprite, () =>
            {
                Vector2 defenderCenter = GetOrigin(_defender) + _defender.Size / 2f;
                Vector2 promptPos      = ComputeCameraMidpoint();
                _targetZone.Position   = promptPos;
                _targetZone.Visible    = true;
                _battleSystem.StartSequence(this, defenderCenter, promptPos);
            }, hopInOffset);
            return;
        }

        if (SkipHopIn)
        {
            // Enemy stays at origin — set combat context without any setup animation.
            // Camera stays at its default position and zoom; no hop-in, no zoom-in.
            // _attackerClosePos = origin so PlayTeardown is a zero-distance no-op on the attacker.
            _attacker         = _enemySprite;
            _defender         = _playerSprite;
            _attackerClosePos = GetOrigin(_enemySprite);

            // Start the cast animation and kick off the sequence immediately.
            // SafeDisconnect first — prevents stacking if BeginEnemyAttack fires more than once
            // (e.g. second turn) without the prior OnCastIntroFinished having run its own disconnect.
            SafeDisconnectEnemyAnim(OnCastIntroFinished);
            PlayEnemy("cast_intro");
            _enemyAnimSprite.AnimationFinished += OnCastIntroFinished;

            Vector2 defenderCenter  = GetOrigin(_defender) + _defender.Size / 2f;
            Vector2 promptPos        = ComputeCameraMidpoint();
            _targetZone.Position     = promptPos;
            _targetZone.Visible      = true;
            _battleSystem.StartSequence(this, defenderCenter, promptPos);
        }
        else
        {
            // SkipHopIn=false, non-hop-in attack — same as SkipHopIn path:
            // enemy stays at origin, plays cast arc. Hop-in only occurs for melee attacks.
            _attacker         = _enemySprite;
            _defender         = _playerSprite;
            _attackerClosePos = GetOrigin(_enemySprite);

            SafeDisconnectEnemyAnim(OnCastIntroFinished);
            PlayEnemy("cast_intro");
            _enemyAnimSprite.AnimationFinished += OnCastIntroFinished;

            Vector2 defenderCenter  = GetOrigin(_defender) + _defender.Size / 2f;
            Vector2 promptPos        = ComputeCameraMidpoint();
            _targetZone.Position     = promptPos;
            _targetZone.Visible      = true;
            _battleSystem.StartSequence(this, defenderCenter, promptPos);
        }
    }

    private void OnEnemyPassEvaluated(int result, int passIndex, int stepIndex)
    {
        var r = (TimingPrompt.InputResult)result;
        GD.Print($"[BattleTest] Enemy pass {passIndex + 1} resolved: {r}.");

        if (r == TimingPrompt.InputResult.Miss)
        {
            _parryClean   = false;
            int damage    = _battleSystem.GetStepBaseDamage(stepIndex);
            if (_playerDefending) damage = Mathf.Max(1, damage / 2);
            _playerHP     = Mathf.Max(0, _playerHP - damage);
            GD.Print($"[BattleTest] Pass miss — player takes {damage} damage. Player HP: {_playerHP}/{PlayerMaxHP}");
            PlaySound("player_hit.wav");
            SpawnDamageNumber(PlayerDamageOrigin, damage, DmgColorPlayer);
            UpdateHPBars();
            ShakeCamera(intensity: 8f, duration: 0.3f);  // shake — player takes a hit

            // Immediate death — play the animation now; the sequence continues silently.
            // SuppressInput blocks all further manual input and auto-miss feedback on circles.
            // Game Over label is deferred to OnEnemySequenceCompleted so the player can watch
            // the full attack pattern for future attempts.
            if (_playerHP <= 0 && !_playerDead)
            {
                GD.Print("[BattleTest] Player HP reached zero mid-sequence — death triggered immediately.");
                _playerDead = true;
                _state      = BattleState.GameOver;
                TimingPrompt.SuppressInput = true;
                _playerAnimSprite.Play("death");
            }
        }
    }

    /// <summary>
    /// BattleSystem.StepStarted — fires at the start of each step, before circles spawn.
    /// For hop-in melee attacks, plays the per-step enemy animation (e.g. melee_attack)
    /// with timing aligned so the impact frame lands when the first circle closes.
    /// </summary>
    private void OnBattleSystemStepStarted(int stepIndex)
    {
        if (!_battleSystem.CurrentAttackIsHopIn) return;
        if (_enemyDead) return;

        var step = _battleSystem.GetCurrentAttack().Steps[stepIndex];
        if (string.IsNullOrEmpty(step.EnemyAnimation)) return;

        // Compute animation start delay so the impact frame lands when circle 0 closes.
        float circleCloseDuration = TimingPrompt.DefaultDurationForType(step.CircleType);
        float rawDelay = circleCloseDuration - step.ImpactFrames[0] / step.Fps;
        float animDelay = Mathf.Max(0f, rawDelay);

        void PlayStepAnimation()
        {
            if (_enemyDead) return;
            SafeDisconnectEnemyAnim(OnEnemyAttackAnimFinished);
            PlayEnemy(step.EnemyAnimation);
            _enemyAnimSprite.AnimationFinished += OnEnemyAttackAnimFinished;
            _hopInAnimFinished = false;  // Reset for this step's animation.
        }

        if (animDelay > 0f)
            GetTree().CreateTimer(animDelay).Timeout += PlayStepAnimation;
        else
            PlayStepAnimation();

        // Bouncing hop-in: subscribe to StepPassEvaluated to replay the animation on
        // each subsequent inward pass (synced to the same animDelay so the impact frame
        // lands on each pass's close). Unsubscribed in OnEnemySequenceCompleted.
        if (step.CircleType == TimingPrompt.PromptType.Bouncing)
        {
            _bouncingHopInStep      = step;
            _bouncingHopInAnimDelay = animDelay;
            if (!_bouncingHopInSubscribed)
            {
                _battleSystem.StepPassEvaluated += OnBouncingHopInPassEvaluated;
                _bouncingHopInSubscribed = true;
            }
        }
    }

    /// <summary>
    /// Bouncing hop-in replay — subscribed in OnBattleSystemStepStarted when the step's
    /// CircleType is Bouncing. Replays the enemy animation on each subsequent pass so
    /// each impact frame lands when its circle closes. Schedules a timer BounceDuration
    /// + animDelay seconds after PassEvaluated, matching BattleSystem's effect-replay pattern.
    /// </summary>
    private void OnBouncingHopInPassEvaluated(int result, int passIndex, int stepIndex)
    {
        if (_bouncingHopInStep == null) return;
        if (_enemyDead) return;

        // Only replay if more inward passes follow.
        if (passIndex >= _bouncingHopInStep.BounceCount) return;

        var   step          = _bouncingHopInStep;
        float animDelay     = _bouncingHopInAnimDelay;
        float bounceDur     = 0.5f;  // matches TimingPrompt.BounceDuration default
        float replayDelay   = bounceDur + animDelay;

        GetTree().CreateTimer(replayDelay).Timeout += () =>
        {
            if (_enemyDead) return;
            SafeDisconnectEnemyAnim(OnEnemyAttackAnimFinished);
            PlayEnemy(step.EnemyAnimation);
            _enemyAnimSprite.AnimationFinished += OnEnemyAttackAnimFinished;
            _hopInAnimFinished = false;
        };
    }

    /// <summary>
    /// Unsubscribes the Bouncing hop-in replay handler. Called from OnEnemySequenceCompleted
    /// so the subscription doesn't carry over to the next turn.
    /// </summary>
    private void UnsubscribeBouncingHopIn()
    {
        if (_bouncingHopInSubscribed)
        {
            _battleSystem.StepPassEvaluated -= OnBouncingHopInPassEvaluated;
            _bouncingHopInSubscribed = false;
        }
        _bouncingHopInStep = null;
    }

    /// <summary>
    /// Adapter: BattleSystem.StepPassEvaluated → slam animation + per-pass damage.
    /// Called once per inward pass across all steps in the enemy's attack sequence.
    /// Slam is skipped when SkipHopIn is true — the enemy never hopped in close,
    /// so the cross-screen lunge ComputeSlamPosition() would produce looks wrong.
    /// </summary>
    private void OnBattleSystemStepPassEvaluated(int result, int passIndex, int stepIndex)
    {
        if (_isPlayerMagicAttack)
        {
            OnPlayerMagicPassEvaluated(result, passIndex, stepIndex);
            return;
        }

        // After player death, skip damage and animation reactions — circles continue silently.
        if (_playerDead) return;

        if (_battleSystem.CurrentAttackIsHopIn || !SkipHopIn)
            OnAttackPassEvaluated(result, passIndex);
        OnEnemyPassEvaluated(result, passIndex, stepIndex);

        // OWNER: OnBattleSystemStepPassEvaluated (enemy turn, per-pass reaction).
        // Pre-empt any in-flight retreat before taking ownership of the sprite.
        // If the backward run loop hasn't fired OnRetreatFinished yet, cancel it here so
        // it doesn't stomp the parry/hit animation or restore idle at the wrong moment.
        // SpeedScale must be reset regardless — it may still be 2 from the retreat.
        SafeDisconnectPlayerAnim(OnRetreatFinished);
        _playerAnimSprite.SpeedScale = 1f;  // always reset — may still be 2 from retreat hop-back

        var r = (TimingPrompt.InputResult)result;
        if (r == TimingPrompt.InputResult.Hit || r == TimingPrompt.InputResult.Perfect)
        {
            // Successful block — restart parry animation from frame 0.
            // SafeDisconnect first so a parry already in flight doesn't stack a second
            // OnParryFinished connection on top of the existing one.
            // Stop() before Play() is required because Godot 4's AnimatedSprite2D.Play()
            // is a no-op when the requested animation is already playing — Stop() halts
            // the current playback so the subsequent Play("parry") always restarts fresh.
            SafeDisconnectPlayerAnim(OnParryFinished);
            StopPlayer();
            PlaySound("parry_clash.wav");
            if (r == TimingPrompt.InputResult.Perfect)
                PlaySound("perfect_parry_instance.wav");
            PlayPlayer("parry");  // OWNER: enemy pass, player defends — always restarts from frame 0
            _playerAnimSprite.AnimationFinished += OnParryFinished;
        }
        else if (r == TimingPrompt.InputResult.Miss)
        {
            // Strike landed — flinch animation, then return to idle.
            SafeDisconnectPlayerAnim(OnHitAnimFinished);
            PlayPlayer("hit");    // OWNER: enemy pass, player takes damage — always restarts fresh
            _playerAnimSprite.AnimationFinished += OnHitAnimFinished;
        }
    }

    /// <summary>
    /// BattleSystem.SequenceCompleted — all steps in the enemy's attack have resolved.
    /// Applies the perfect-parry counter if earned, then tears down the combat close-up.
    /// BattleSystem owns its prompts and frees them internally — no FreeActivePrompt call needed.
    /// </summary>
    /// <summary>
    /// Routes BattleSystem.SequenceCompleted to the correct handler based on who is attacking.
    /// </summary>
    private void OnSequenceCompleted()
    {
        if (_isPlayerMagicAttack)
            OnPlayerMagicSequenceCompleted();
        else
            OnEnemySequenceCompleted();
    }

    private void OnEnemySequenceCompleted()
    {
        GD.Print("[BattleTest] Enemy attack sequence complete.");
        _targetZone.Visible        = false;
        TimingPrompt.SuppressInput = false;

        // Player died mid-sequence — death animation is already playing.
        // Clean up the enemy and let the death flow finish (Game Over label).
        if (_playerDead)
        {
            if (_battleSystem.CurrentAttackIsHopIn)
            {
                // Hop-in path: let ProceedAfterHopInAnim handle teardown.
                UpdateHPBars();
                UnsubscribeBouncingHopIn();
                _hopInOver              = true;
                _hopInSequenceCompleted = true;
                if (_hopInAnimFinished)
                    ProceedAfterHopInAnim();
                return;
            }

            if (HasCastEnd())
            {
                SafeDisconnectEnemyAnim(OnCastEndFinished);
                PlayEnemy("cast_end");
                _enemyAnimSprite.AnimationFinished += OnCastEndFinished;
            }
            else
                PlayEnemy("idle");
            ShowEndLabel("Game Over");
            return;
        }

        if (_battleSystem.CurrentAttackIsHopIn)
        {
            // Hop-in path continuation — runs after counter animation (if any) completes.
            void HopInContinuation()
            {
                UpdateHPBars();
                UnsubscribeBouncingHopIn();
                _hopInOver              = CheckGameOver();
                _hopInSequenceCompleted = true;

                // Don't call PlayTeardown here — OnEnemyAttackAnimFinished handles it once the
                // animation finishes (and PostAnimationDelayMs has elapsed). If the animation
                // already finished before us, fire the proceed path now.
                if (_hopInAnimFinished)
                    ProceedAfterHopInAnim();
            }

            if (_parryClean)
            {
                TryTriggerAbsorption();
                PlayParryCounter(HopInContinuation);
            }
            else
                HopInContinuation();
            return;
        }

        // Non-hop-in path continuation — runs after counter animation (if any) completes.
        // skipCastEnd: true when called from PlayParryCounter (which already played cast_end).
        void NonHopInContinuation(bool skipCastEnd)
        {
            UpdateHPBars();
            bool over = CheckGameOver();

            if (!over)
            {
                if (!skipCastEnd)
                {
                    // Normal completion — transition enemy out of cast pose.
                    if (HasCastEnd())
                    {
                        SafeDisconnectEnemyAnim(OnCastEndFinished);
                        PlayEnemy("cast_end");
                        _enemyAnimSprite.AnimationFinished += OnCastEndFinished;
                    }
                    else
                        PlayEnemy("idle");
                }
                PlayTeardown(() => GetTree().CreateTimer(0.5f).Timeout += ShowMenu);
                return;
            }

            // Game over — determine which side is dead and play the appropriate death animation.
            PlayTeardown(null);

            if (_playerHP <= 0)
            {
                if (!skipCastEnd)
                {
                    if (HasCastEnd())
                    {
                        SafeDisconnectEnemyAnim(OnCastEndFinished);
                        PlayEnemy("cast_end");
                        _enemyAnimSprite.AnimationFinished += OnCastEndFinished;
                    }
                    else
                        PlayEnemy("idle");
                }
                _playerDead = true;
                SafeDisconnectPlayerAnim(OnPlayerDeathFinished);
                _playerAnimSprite.Play("death");
                _playerAnimSprite.AnimationFinished += OnPlayerDeathFinished;
            }
            else // _enemyHP <= 0 — perfect parry counter killed the enemy
            {
                _enemyDead = true;
                PlaySound("enemy_defeat.mp3");
                SafeDisconnectEnemyAnim(OnEnemyDeathFinished);
                _enemyAnimSprite.Play("death");
                _enemyAnimSprite.AnimationFinished += OnEnemyDeathFinished;
                ScheduleBossRevealIfPhase1();
                PlayPlayer("idle");
            }
        }

        if (_parryClean)
        {
            TryTriggerAbsorption();
            PlayParryCounter(() => NonHopInContinuation(skipCastEnd: true));
        }
        else
            NonHopInContinuation(skipCastEnd: false);
    }

    // =========================================================================
    // Player attack phase
    // =========================================================================

    private void BeginPlayerAttack()
    {
        _state               = BattleState.PlayerAttack;
        _isPlayerMagicAttack = false;
        _comboMissed         = false;
        GD.Print(_isComboAttack ? "[BattleTest] Player uses Combo Strike." : "[BattleTest] Player attacks.");
        _comboPassIndex = 0;
        var promptType = _isComboAttack ? TimingPrompt.PromptType.Bouncing : TimingPrompt.PromptType.Standard;
        BeginAttack(_playerSprite, _enemySprite, promptType, OnPlayerPromptCompleted);
    }

    /// <summary>
    /// Begins a player magic attack turn using BattleSystem's sequence runner.
    /// No hop-in — the player stays at origin and casts a ranged effect at the enemy.
    /// BattleSystem handles spawning the timing circle and the effect sprite.
    /// </summary>
    private void BeginPlayerMagicAttack()
    {
        _state               = BattleState.PlayerAttack;
        _isPlayerMagicAttack = true;
        _isComboAttack       = false;
        GD.Print(_isPlayerHealAttack
            ? "[BattleTest] Player uses Cure."
            : "[BattleTest] Player uses magic attack.");

        // Set combat context so ComputeCameraMidpoint() returns a sensible midpoint.
        // No hop-in — attackerClosePos = player origin so the camera midpoint is the
        // natural center between the two combatants.
        _attacker         = _playerSprite;
        _defender         = _isPlayerHealAttack ? _playerSprite : _enemySprite;
        _attackerClosePos = GetOrigin(_playerSprite);

        Vector2 defenderCenter = _isPlayerHealAttack
            ? GetOrigin(_playerSprite) + _playerSprite.Size / 2f
            : GetOrigin(_enemySprite)  + _enemySprite.Size / 2f;
        Vector2 promptPos      = ComputeCameraMidpoint();

        // Play cast animation; defer StartSequence until it finishes so the wind-up
        // completes before the timing circle appears and the effect fires.
        SafeDisconnectPlayerAnim(OnPlayerCastFinished);
        PlayPlayer("cast");  // OWNER: BeginPlayerMagicAttack — cast wind-up before sequence
        GetTree().CreateTimer(1f / 12f).Timeout += () => PlaySound("magic_launch_4.wav");  // frame 1 at 12fps
        _playerAnimSprite.AnimationFinished += OnPlayerCastFinished;

        // Capture locals for the callback closure.
        _playerMagicDefenderCenter = defenderCenter;
        _playerMagicPromptPos      = promptPos;
    }

    private void OnPlayerPromptCompleted(int result)
    {
        var r = (TimingPrompt.InputResult)result;
        GD.Print($"[BattleTest] Player attack resolved: {r}.");
        _targetZone.Visible = false;
        _inputLocked = true;  // Lock input through slash animation, retreat, and teardown.

        if (_isComboAttack)
        {
            // Per-pass damage was already applied in OnAttackPassEvaluated for every pass.
            // _pendingGameOver was set there on a miss exit; set it here for the all-hits case.
            if (!_comboMissed)
                _pendingGameOver = CheckGameOver();
            // Free the prompt — for miss exits it is already scheduled; duplicating is safe
            // because FreeActivePrompt guards with IsInstanceValid.
            GetTree().CreateTimer(TimingPrompt.FlashDuration).Timeout += FreeActivePrompt;
            // Retreat flow is already in motion via OnFinalSlashFinished (subscribed at pass 2).
            return;
        }

        // ── Basic attack (single Standard pass) ──────────────────────────────────
        int baseDamage = _playerBasicAttack?.BaseDamage ?? 10;
        int damage     = ComputePlayerDamage(baseDamage, r);

        Color dmgColor = r switch
        {
            TimingPrompt.InputResult.Perfect => DmgColorPerfect,
            TimingPrompt.InputResult.Hit     => DmgColorHit,
            _                                => DmgColorMiss,
        };

        if (r == TimingPrompt.InputResult.Perfect)
            ShakeCamera(intensity: 6f, duration: 0.2f);  // shake — perfect timing feedback

        _enemyHP = Mathf.Max(0, _enemyHP - damage);
        GD.Print($"[BattleTest] Player deals {damage} damage. Enemy HP: {_enemyHP}/{_enemyMaxHP}");
        PlaySound("enemy_hit.wav");
        SpawnDamageNumber(EnemyDamageOrigin, damage, dmgColor);
        ShakeCamera(intensity: 8f, duration: 0.25f);  // shake — strike lands on enemy
        PlayEnemyHurtFlash();

        UpdateHPBars();
        _pendingGameOver = CheckGameOver();
        GetTree().CreateTimer(TimingPrompt.FlashDuration).Timeout += FreeActivePrompt;

        // Single attack: play the slash now that the circle has resolved.
        // combo_slash1 covers sheet frames 1–3; frame 0 was already shown as the wind-up.
        // PlayTeardown is deferred to OnFinalSlashFinished so the strike plays before retreat.
        PlaySound("player_attack_swing.wav");
        SafeDisconnectPlayerAnim(OnFinalSlashFinished);
        PlayPlayer("combo_slash1");  // OWNER: player turn, single-hit slash on resolve
        _playerAnimSprite.AnimationFinished += OnFinalSlashFinished;
    }

    // =========================================================================
    // Shared helpers
    // =========================================================================

    /// <summary>
    /// Called once per pass when the player's magic attack circle resolves.
    /// Applies damage to the enemy identically to the physical attack damage table.
    /// </summary>
    private void OnPlayerMagicPassEvaluated(int result, int passIndex, int stepIndex)
    {
        var r          = (TimingPrompt.InputResult)result;
        int baseDamage = _battleSystem.GetStepBaseDamage(stepIndex);
        int amount     = ComputePlayerDamage(baseDamage, r);

        if (_isPlayerHealAttack)
        {
            GD.Print($"[BattleTest] Cure pass {passIndex + 1} resolved: {r}  ({amount} HP).");
            _playerHP = Mathf.Min(PlayerMaxHP, _playerHP + amount);
            GD.Print($"[BattleTest] Cure heals {amount} HP. Player HP: {_playerHP}/{PlayerMaxHP}");
            SpawnDamageNumber(PlayerDamageOrigin, amount, DmgColorPerfect);  // green for healing
            UpdateHPBars();
            return;
        }

        GD.Print($"[BattleTest] Player magic pass {passIndex + 1} resolved: {r}  ({amount} damage).");

        Color dmgColor = r switch
        {
            TimingPrompt.InputResult.Perfect => DmgColorPerfect,
            TimingPrompt.InputResult.Hit     => DmgColorHit,
            _                                => DmgColorMiss,
        };

        if (r == TimingPrompt.InputResult.Perfect)
            ShakeCamera(intensity: 6f, duration: 0.2f);

        _enemyHP = Mathf.Max(0, _enemyHP - amount);
        GD.Print($"[BattleTest] Magic hit deals {amount} damage. Enemy HP: {_enemyHP}/{_enemyMaxHP}");
        PlaySound("enemy_hit.wav");
        SpawnDamageNumber(EnemyDamageOrigin, amount, dmgColor);
        ShakeCamera(intensity: 8f, duration: 0.25f);
        PlayEnemyHurtFlash();
        UpdateHPBars();
    }

    /// <summary>
    /// Called when the player's magic attack sequence fully resolves.
    /// Hides the target zone, checks for game over, then returns to the player menu.
    /// </summary>
    private void OnPlayerMagicSequenceCompleted()
    {
        GD.Print("[BattleTest] Player magic sequence complete.");
        _targetZone.Visible = false;
        _inputLocked = true;  // Lock input through cast transition and teardown.

        // Play cast_transition once to smoothly exit the held cast pose, then return to idle.
        SafeDisconnectPlayerAnim(OnPlayerCastTransitionFinished);
        PlayPlayer("cast_transition");  // OWNER: OnPlayerMagicSequenceCompleted — exit cast pose
        _playerAnimSprite.AnimationFinished += OnPlayerCastTransitionFinished;

        // Cure heals the player — no game-over check needed, proceed directly to enemy turn.
        if (_isPlayerHealAttack)
        {
            _isPlayerHealAttack = false;
            GetTree().CreateTimer(0.5f).Timeout += BeginEnemyAttack;
            return;
        }

        bool over = CheckGameOver();
        if (!over)
        {
            // Mirror the physical attack flow: short pause then enemy takes their turn.
            // ShowMenu is intentionally skipped here — magic attacks transition directly
            // to BeginEnemyAttack, matching the behaviour after OnFinalSlashFinished.
            GetTree().CreateTimer(0.5f).Timeout += BeginEnemyAttack;
            return;
        }

        if (_enemyHP <= 0)
        {
            _enemyDead = true;
            PlaySound("enemy_defeat.mp3");
            SafeDisconnectEnemyAnim(OnEnemyDeathFinished);
            _enemyAnimSprite.Play("death");
            _enemyAnimSprite.AnimationFinished += OnEnemyDeathFinished;
            ScheduleBossRevealIfPhase1();
        }
        else
        {
            _playerDead = true;
            SafeDisconnectPlayerAnim(OnPlayerDeathFinished);
            _playerAnimSprite.Play("death");
            _playerAnimSprite.AnimationFinished += OnPlayerDeathFinished;
        }
    }

    // =========================================================================
    // Ether item use — player animation + visual effect, no circles
    // =========================================================================

    /// <summary>
    /// Plays the item-use combo animation on the player, spawns the Ether effect
    /// sprite at the impact frame, plays the sound cue, restores MP, then returns
    /// to idle and begins the enemy turn.
    /// </summary>
    private void UseEtherItem()
    {
        if (_playerDead) { BeginEnemyAttack(); return; }
        _state       = BattleState.PlayerAttack;
        _inputLocked = true;  // block input during item use

        // Play the item-use animation on the player.
        SafeDisconnectPlayerAnim(OnEtherAnimationFinished);
        PlayPlayer("item_use");
        _playerAnimSprite.AnimationFinished += OnEtherAnimationFinished;

        // Schedule effect + sound + MP restore at the impact frame of the combo animation.
        int   impactFrame = _playerEtherEffect?.Steps?[0]?.ImpactFrames?[0] ?? 7;
        float fps         = _playerEtherEffect?.Steps?[0]?.Fps ?? 12f;
        float impactDelay = impactFrame / fps;
        GetTree().CreateTimer(impactDelay).Timeout += () =>
        {
            if (_playerDead) return;
            PlaySound("cure_spell.wav");
            RestoreMp(20);  // clamps to PlayerMaxMp and calls UpdateMpBar internally
            SpawnEtherEffect(_playerEtherEffect);
        };
    }

    private void OnEtherAnimationFinished()
    {
        SafeDisconnectPlayerAnim(OnEtherAnimationFinished);
        if (_playerDead) return;
        PlayPlayer("idle");
        GetTree().CreateTimer(0.5f).Timeout += BeginEnemyAttack;
    }

    /// <summary>
    /// Spawns a one-shot visual effect sprite centered on the player using the first step
    /// of the given AttackData as the data source. Does not use BattleSystem — no circles.
    /// </summary>
    private void SpawnEtherEffect(AttackData data)
    {
        if (data == null || data.Steps.Count == 0) return;
        var step = data.Steps[0];
        if (string.IsNullOrEmpty(step.SpritesheetPath)) return;
        var texture = GD.Load<Texture2D>(step.SpritesheetPath);
        if (texture == null)
        {
            GD.PrintErr($"[BattleTest] SpawnEtherEffect: failed to load {step.SpritesheetPath}");
            return;
        }

        int cols = texture.GetWidth()  / step.FrameWidth;
        int rows = texture.GetHeight() / step.FrameHeight;

        var frames = new SpriteFrames();
        if (frames.HasAnimation("default")) frames.RemoveAnimation("default");
        frames.AddAnimation("default");
        frames.SetAnimationSpeed("default", step.Fps);
        frames.SetAnimationLoop("default", false);
        for (int row = 0; row < rows; row++)
        for (int col = 0; col < cols; col++)
        {
            var atlas    = new AtlasTexture();
            atlas.Atlas  = texture;
            atlas.Region = new Rect2(col * step.FrameWidth, row * step.FrameHeight,
                                     step.FrameWidth, step.FrameHeight);
            frames.AddFrame("default", atlas);
        }

        Vector2 playerCenter = GetOrigin(_playerSprite) + _playerSprite.Size / 2f;
        var sprite          = new AnimatedSprite2D();
        sprite.SpriteFrames = frames;
        sprite.Centered     = true;
        sprite.Scale        = step.Scale;
        sprite.Position     = new Vector2(playerCenter.X, FloorY) + step.PlayerOffset;

        Action onFinished = null;
        onFinished = () =>
        {
            var callable = Callable.From(onFinished);
            if (sprite.IsConnected(AnimatedSprite2D.SignalName.AnimationFinished, callable))
                sprite.Disconnect(AnimatedSprite2D.SignalName.AnimationFinished, callable);
            sprite.QueueFree();
        };
        sprite.AnimationFinished += onFinished;
        AddChild(sprite);
        sprite.Play("default");
    }

    private void FreeActivePrompt()
    {
        if (_activePrompt != null && IsInstanceValid(_activePrompt))
        {
            _activePrompt.QueueFree();
            _activePrompt = null;
        }
    }

    // =========================================================================
    // Shared combat helpers
    // =========================================================================

    /// <summary>
    /// Computes damage from a base value and input quality.
    ///   Perfect → RoundToInt(baseDamage × 1.5)
    ///   Hit     → baseDamage
    ///   Miss    → RoundToInt(baseDamage × 0.5)
    /// Used by all player attack paths so a single .tres BaseDamage field drives all outcomes.
    /// </summary>
    private static int ComputePlayerDamage(int baseDamage, TimingPrompt.InputResult result) =>
        result switch
        {
            TimingPrompt.InputResult.Perfect => Mathf.RoundToInt(baseDamage * 1.5f),
            TimingPrompt.InputResult.Hit     => baseDamage,
            _                                => Mathf.RoundToInt(baseDamage * 0.5f),  // Miss
        };

    /// <summary>
    /// Called once both conditions are met for a hop-in turn:
    ///   1. The timing sequence has completed (OnEnemySequenceCompleted set _hopInSequenceCompleted).
    ///   2. The enemy attack animation has finished (OnEnemyAttackAnimFinished set _hopInAnimFinished).
    ///
    /// Applies PostAnimationDelayMs (via a timer if > 0) then runs PlayTeardown for normal
    /// completion or handles death/game-over if _hopInOver is true.
    /// </summary>
    private void ProceedAfterHopInAnim()
    {
        void DoTeardown()
        {
            if (!_hopInOver)
            {
                // Normal completion — enemy retreats then returns to idle; menu reappears.
                PlayTeardown(() =>
                {
                    PlayEnemy("idle");
                    GetTree().CreateTimer(0.5f).Timeout += ShowMenu;
                });
            }
            else
            {
                // Game over — retreat enemy without scheduling next turn, then handle death.
                PlayTeardown(null);

                if (_playerHP <= 0 && !_playerDead)
                {
                    _playerDead = true;
                    SafeDisconnectPlayerAnim(OnPlayerDeathFinished);
                    _playerAnimSprite.Play("death");
                    _playerAnimSprite.AnimationFinished += OnPlayerDeathFinished;
                }
                else if (_playerHP <= 0 && _playerDead)
                {
                    // Death was triggered mid-sequence — animation already playing.
                    ShowEndLabel("Game Over");
                }
                else  // _enemyHP <= 0 — parry counter killed the enemy
                {
                    _enemyDead = true;
                    PlaySound("enemy_defeat.mp3");
                    SafeDisconnectEnemyAnim(OnEnemyDeathFinished);
                    _enemyAnimSprite.Play("death");
                    _enemyAnimSprite.AnimationFinished += OnEnemyDeathFinished;
                    ScheduleBossRevealIfPhase1();
                    PlayPlayer("idle");
                }
            }
        }

        int delayMs = _battleSystem.GetLastStepPostAnimDelayMs();
        if (delayMs > 0)
            GetTree().CreateTimer(delayMs / 1000f).Timeout += DoTeardown;
        else
            DoTeardown();
    }

    /// <summary>
    /// Starts a camera shake that writes a random <see cref="Camera2D.Offset"/> each frame,
    /// fading the intensity linearly to zero over <paramref name="duration"/> seconds.
    /// Calling this while a shake is already in progress replaces it immediately — the new
    /// parameters take effect on the next <see cref="_Process"/> tick.
    ///
    /// Using <c>Offset</c> rather than <c>Position</c> means the shake is completely
    /// decoupled from the position tweens in <see cref="PlayHopIn"/> and
    /// <see cref="PlayTeardown"/> — neither can overwrite the other.
    /// </summary>
    private void ShakeCamera(float intensity, float duration)
    {
        GD.Print($"[BattleTest] ShakeCamera called: intensity={intensity} duration={duration}");
        _shakeIntensity     = intensity;
        _shakeDurationTotal = duration;
        _shakeTimeRemaining = duration;
    }

    /// <summary>
    /// Returns the attack to use for the current enemy turn.
    /// Priority: LoopAttack+TestEnemyAttack (testing) > EnemyData.AttackPool > _enemyAttackData (fallback).
    /// </summary>
    private AttackData SelectEnemyAttack()
    {
        if (LoopAttack && TestEnemyAttack != null)
            return TestEnemyAttack;

        // Beckon forces the learnable move for one turn. Consume the flag whether or
        // not a learnable exists; LoopAttack above still wins in dev test mode.
        if (_beckoning)
        {
            _beckoning = false;
            if (EnemyData?.LearnableAttack != null)
                return EnemyData.LearnableAttack;
        }

        if (EnemyData != null && EnemyData.AttackPool != null && EnemyData.AttackPool.Length > 0)
        {
            var attack = AttackSelector.SelectAttack(EnemyData, ref _lastAttackIndex);
            if (attack != null)
                return attack;
        }

        return _enemyAttackData;
    }

    public void ShowBattleMessage(string text) => _battleMessage.Show(text);

    /// <summary>
    /// Fire-and-forget one-shot sound playback from res://Assets/Audio/.
    /// Creates a temporary AudioStreamPlayer that frees itself when done.
    /// <paramref name="volumeDb"/> adjusts volume in decibels (0 = full, -6 ≈ 50%, -4 ≈ 60%).
    /// </summary>
    private void PlaySound(string filename, float volumeDb = 0f)
    {
        var stream = GD.Load<AudioStream>($"res://Assets/Audio/{filename}");
        if (stream == null)
        {
            GD.PrintErr($"[BattleTest] Failed to load audio: {filename}");
            return;
        }
        var player = new AudioStreamPlayer();
        player.Stream   = stream;
        player.VolumeDb = volumeDb;
        AddChild(player);
        player.Play();
        player.Finished += player.QueueFree;
    }

    private void ShowLearnableSignal()
    {
        ShowBattleMessage("If I watch carefully...");
        PlaySound("learnable_signal.wav");
    }

    /// <summary>
    /// Beckon ability — if the enemy has an unabsorbed learnable move, sets _beckoning
    /// so SelectEnemyAttack returns LearnableAttack this turn. Otherwise shows a brief
    /// message. Always hands off to the enemy turn immediately (no animation).
    /// </summary>
    /// <summary>
    /// Beckon ability — forces the enemy to use their LearnableAttack next turn.
    /// Selectability (MP, learnable present, not yet absorbed) is gated at the menu
    /// level by IsSubMenuOptionEnabled, so this method assumes all preconditions hold.
    /// </summary>
    private void PerformBeckon()
    {
        const int beckonMpCost = 10;
        _playerMp -= beckonMpCost;
        UpdateMpBar();
        _beckoning = true;
        GD.Print($"[BattleTest] Player beckons (-{beckonMpCost} MP) — enemy will use learnable move next turn.");
        BeginEnemyAttack();
    }

    /// <summary>
    /// If the just-completed enemy attack was the learnable move and the player perfect-parried it,
    /// triggers the absorption moment (message + flash). No-ops if already absorbed.
    /// Called immediately before PlayParryCounter in OnEnemySequenceCompleted.
    /// </summary>
    private void TryTriggerAbsorption()
    {
        if (_hasAbsorbedLearnableMove) return;

        var currentAttack = _battleSystem.GetCurrentAttack();
        if (EnemyData?.LearnableAttack == null || currentAttack != EnemyData.LearnableAttack) return;

        _hasAbsorbedLearnableMove = true;
        PlaySound("absorbed_ability_acquired.wav", volumeDb: 6f);
        // TODO: when player state/character system is built, add absorbed move to player's persistent move list here

        _absorbedMoveAttack = EnemyData.LearnableAttack;
        RebuildSubMenu();

        ShowBattleMessage("I've got it.");
        GD.Print("[BattleTest] Absorbed learnable move!");
    }

    /// <summary>
    /// Flashes the enemy sprite white 3 times over ~0.6s using the WhiteFlash shader.
    /// </summary>
    private void FlashEnemyWhite()
    {
        _enemyFlashTween?.Kill();
        _enemyFlashMaterial.SetShaderParameter("flash_amount", 0.0f);

        _enemyFlashTween = CreateTween();
        for (int i = 0; i < 3; i++)
        {
            _enemyFlashTween.TweenMethod(
                Callable.From((float v) => _enemyFlashMaterial.SetShaderParameter("flash_amount", v)),
                0.0f, 1.0f, 0.1f);
            _enemyFlashTween.TweenMethod(
                Callable.From((float v) => _enemyFlashMaterial.SetShaderParameter("flash_amount", v)),
                1.0f, 0.0f, 0.1f);
        }
    }

    private bool CheckGameOver()
    {
        if (_playerHP <= 0)
        {
            GD.Print("[BattleTest] Enemy wins.");
            _state = BattleState.GameOver;
            return true;
        }
        if (_enemyHP <= 0)
        {
            GD.Print("[BattleTest] Player wins.");
            _state = BattleState.GameOver;
            return true;
        }
        return false;
    }

    private void UpdateHPBars()
    {
        _playerHPFill.Size  = new Vector2(BarWidth * ((float)_playerHP / PlayerMaxHP), _playerHPFill.Size.Y);
        _playerHPLabel.Text = $"{_playerHP}/{PlayerMaxHP}";
        _enemyHPFill.Size   = new Vector2(BarWidth * ((float)_enemyHP / _enemyMaxHP), _enemyHPFill.Size.Y);
        _enemyHPLabel.Text  = $"{_enemyHP}/{_enemyMaxHP}";
        UpdateMpBar();
    }

    private void UpdateMpBar()
    {
        _playerMPFill.Size  = new Vector2(BarWidth * ((float)_playerMp / PlayerMaxMp), _playerMPFill.Size.Y);
        _playerMPLabel.Text = $"{_playerMp}/{PlayerMaxMp}";
    }

    private void RestoreMp(int amount)
    {
        _playerMp = Mathf.Min(_playerMp + amount, PlayerMaxMp);
        UpdateMpBar();
    }

    // =========================================================================
    // Status panels — enemy (top) and player party (bottom)
    // =========================================================================

    private const float BarWidth  = 220f;
    private const float BarHeight = 20f;

    private void BuildStatusPanels()
    {
        var layer = new CanvasLayer();
        layer.Name = "StatusPanels";
        AddChild(layer);

        // ── Enemy panel (top-center) ─────────────────────────────────
        BuildEnemyPanel(layer);

        // ── Player party panel (bottom-center) ──────────────────────
        BuildPlayerPanel(layer);
    }

    private void BuildEnemyPanel(CanvasLayer layer)
    {
        // Panel container anchored to top-center.
        var panel = new PanelContainer();
        panel.AnchorLeft   = 0.5f;
        panel.AnchorRight  = 0.5f;
        panel.AnchorTop    = 0f;
        panel.AnchorBottom = 0f;
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.OffsetTop    = 20f;
        panel.OffsetBottom = 10f;  // let content size it

        // Semi-transparent dark background.
        var style = new StyleBoxFlat();
        style.BgColor      = new Color(0f, 0f, 0f, 0.55f);
        style.CornerRadiusBottomLeft  = 6;
        style.CornerRadiusBottomRight = 6;
        style.CornerRadiusTopLeft     = 6;
        style.CornerRadiusTopRight    = 6;
        style.ContentMarginLeft   = 16f;
        style.ContentMarginRight  = 16f;
        style.ContentMarginTop    = 10f;
        style.ContentMarginBottom = 10f;
        panel.AddThemeStyleboxOverride("panel", style);
        layer.AddChild(panel);

        // One row per enemy — for now just one.
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        panel.AddChild(vbox);

        string enemyName = EnemyData != null ? EnemyData.EnemyName : "Enemy";
        AddEnemyRow(vbox, enemyName, out _enemyHPFill, out _enemyHPLabel);
    }

    private void AddEnemyRow(VBoxContainer parent, string name,
                              out ColorRect hpFill, out Label hpLabel)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        parent.AddChild(row);

        _enemyNameLabel = new Label();
        _enemyNameLabel.Text = name;
        _enemyNameLabel.CustomMinimumSize = new Vector2(140f, 0f);
        _enemyNameLabel.AddThemeFontSizeOverride("font_size", 18);
        row.AddChild(_enemyNameLabel);

        // HP bar — background + fill + overlaid label.
        var barContainer = new Control();
        barContainer.CustomMinimumSize = new Vector2(BarWidth, BarHeight);
        row.AddChild(barContainer);

        var bg = new ColorRect();
        bg.Size  = new Vector2(BarWidth, BarHeight);
        bg.Color = new Color(0.15f, 0.05f, 0.05f, 1f);
        barContainer.AddChild(bg);

        hpFill       = new ColorRect();
        hpFill.Size  = new Vector2(BarWidth, BarHeight);
        hpFill.Color = new Color(0.80f, 0.12f, 0.12f, 1f);
        barContainer.AddChild(hpFill);

        hpLabel = new Label();
        hpLabel.Size                = new Vector2(BarWidth, BarHeight);
        hpLabel.HorizontalAlignment = HorizontalAlignment.Center;
        hpLabel.VerticalAlignment   = VerticalAlignment.Center;
        hpLabel.AddThemeFontSizeOverride("font_size", 14);
        barContainer.AddChild(hpLabel);
    }

    private void BuildPlayerPanel(CanvasLayer layer)
    {
        // Panel container anchored to bottom-center.
        var panel = new PanelContainer();
        panel.AnchorLeft   = 0.5f;
        panel.AnchorRight  = 0.5f;
        panel.AnchorTop    = 1f;
        panel.AnchorBottom = 1f;
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical   = Control.GrowDirection.Begin;
        panel.OffsetBottom = -10f;

        var style = new StyleBoxFlat();
        style.BgColor      = new Color(0f, 0f, 0f, 0.55f);
        style.CornerRadiusBottomLeft  = 6;
        style.CornerRadiusBottomRight = 6;
        style.CornerRadiusTopLeft     = 6;
        style.CornerRadiusTopRight    = 6;
        style.ContentMarginLeft   = 16f;
        style.ContentMarginRight  = 16f;
        style.ContentMarginTop    = 10f;
        style.ContentMarginBottom = 10f;
        panel.AddThemeStyleboxOverride("panel", style);
        layer.AddChild(panel);

        // One row per party member — for now just the knight.
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        panel.AddChild(vbox);

        AddPlayerRow(vbox, "Knight",
                     out _playerHPFill, out _playerHPLabel,
                     out _playerMPFill, out _playerMPLabel);
    }

    private void AddPlayerRow(VBoxContainer parent, string name,
                               out ColorRect hpFill, out Label hpLabel,
                               out ColorRect mpFill, out Label mpLabel)
    {
        // Outer row: name on the left, bars stacked on the right.
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        parent.AddChild(row);

        var nameLabel = new Label();
        nameLabel.Text = name;
        nameLabel.CustomMinimumSize = new Vector2(140f, 0f);
        nameLabel.AddThemeFontSizeOverride("font_size", 18);
        row.AddChild(nameLabel);

        // Vertical stack of HP and MP bars.
        var bars = new VBoxContainer();
        bars.AddThemeConstantOverride("separation", 4);
        row.AddChild(bars);

        // HP bar
        var hpContainer = new Control();
        hpContainer.CustomMinimumSize = new Vector2(BarWidth, BarHeight);
        bars.AddChild(hpContainer);

        var hpBg = new ColorRect();
        hpBg.Size  = new Vector2(BarWidth, BarHeight);
        hpBg.Color = new Color(0.15f, 0.05f, 0.05f, 1f);
        hpContainer.AddChild(hpBg);

        hpFill       = new ColorRect();
        hpFill.Size  = new Vector2(BarWidth, BarHeight);
        hpFill.Color = new Color(0.80f, 0.12f, 0.12f, 1f);
        hpContainer.AddChild(hpFill);

        hpLabel = new Label();
        hpLabel.Size                = new Vector2(BarWidth, BarHeight);
        hpLabel.HorizontalAlignment = HorizontalAlignment.Center;
        hpLabel.VerticalAlignment   = VerticalAlignment.Center;
        hpLabel.AddThemeFontSizeOverride("font_size", 14);
        hpContainer.AddChild(hpLabel);

        // MP bar
        var mpContainer = new Control();
        mpContainer.CustomMinimumSize = new Vector2(BarWidth, BarHeight);
        bars.AddChild(mpContainer);

        var mpBg = new ColorRect();
        mpBg.Size  = new Vector2(BarWidth, BarHeight);
        mpBg.Color = new Color(0.05f, 0.05f, 0.15f, 1f);
        mpContainer.AddChild(mpBg);

        mpFill       = new ColorRect();
        mpFill.Size  = new Vector2(BarWidth, BarHeight);
        mpFill.Color = new Color(0.15f, 0.30f, 0.85f, 1f);
        mpContainer.AddChild(mpFill);

        mpLabel = new Label();
        mpLabel.Size                = new Vector2(BarWidth, BarHeight);
        mpLabel.HorizontalAlignment = HorizontalAlignment.Center;
        mpLabel.VerticalAlignment   = VerticalAlignment.Center;
        mpLabel.AddThemeFontSizeOverride("font_size", 14);
        mpContainer.AddChild(mpLabel);
    }

    /// <summary>
    /// Spawns a floating damage number at <paramref name="position"/> that drifts upward
    /// 80px and fades to transparent over 1 second, then frees itself.
    /// </summary>
    private void SpawnDamageNumber(Vector2 position, int amount, Color color)
    {
        SpawnDamageNumber(position, amount, color, parent: null);
    }

    /// <summary>
    /// Spawns a floating damage/heal number that drifts upward and fades out.
    /// When <paramref name="parent"/> is non-null, the label is added as a child of that node
    /// and <paramref name="position"/> is treated as a local offset — the number travels with
    /// the parent during tweens (e.g. enemy retreat after parry counter).
    /// When null, the label is added to BattleTest in world space.
    /// </summary>
    private void SpawnDamageNumber(Vector2 position, int amount, Color color, Node parent)
    {
        var label = new Label();
        label.Text                = amount.ToString();
        label.Modulate            = color;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.CustomMinimumSize   = new Vector2(80f, 0f);
        label.AddThemeFontSizeOverride("font_size", 28);

        Vector2 startPos = position - new Vector2(40f, 0f);
        Vector2 endPos   = startPos  - new Vector2(0f, 80f);
        label.Position   = startPos;

        if (parent != null)
        {
            // Counteract the parent's scale so the label renders at normal size.
            if (parent is Node2D parent2D && parent2D.Scale != Vector2.One)
                label.Scale = new Vector2(1f / parent2D.Scale.X, 1f / parent2D.Scale.Y);
            parent.AddChild(label);
        }
        else
            AddChild(label);

        var tween = CreateTween();
        tween.SetParallel(true);
        tween.TweenProperty(label, "position",   endPos, 1.0f);
        tween.TweenProperty(label, "modulate:a", 0.0f,   1.0f);
        tween.Finished += label.QueueFree;
    }

    // =========================================================================
    // Attack animation — setup, per-pass slam, teardown
    // =========================================================================

    /// <summary>
    /// Plays the hop-in animation: attacker lunges to close stance, camera zooms in.
    /// Sets <see cref="_attacker"/>, <see cref="_defender"/>, and
    /// <see cref="_attackerClosePos"/> before starting the tween, so
    /// <see cref="ComputeCameraMidpoint"/> and <see cref="ComputeSlamPosition"/> are
    /// usable immediately after this call returns. <paramref name="onComplete"/> fires
    /// when the tween finishes; safe to pass null.
    /// </summary>
    /// <param name="attackerOffset">
    /// Optional offset for hop-in melee attacks. Only the X component is added to
    /// <see cref="_attackerClosePos"/> — this keeps the camera midpoint and slam
    /// positions unaffected by vertical adjustment. The Y component is applied solely
    /// to the enemy AnimatedSprite2D tween destination so the sprite moves vertically
    /// without shifting the camera or target zone.
    /// </param>
    private void PlayHopIn(ColorRect attacker, ColorRect defender, Action onComplete,
                           Vector2 attackerOffset = default)
    {
        _attacker         = attacker;
        _defender         = defender;
        _attackerClosePos = ComputeClosePosition() + new Vector2(attackerOffset.X, 0f);

        // Raise the attacker's sprite ZIndex so it renders in front of the defender
        // during the hop-in overlap. Restored to 0 in PlayTeardown.
        if (attacker == _playerSprite)
        {
            _playerAnimSprite.ZIndex = 1;
            _enemyAnimSprite.ZIndex  = 0;
        }
        else if (attacker == _enemySprite)
        {
            _enemyAnimSprite.ZIndex  = 1;
            _playerAnimSprite.ZIndex = 0;
        }

        // Hop-in footstep sound for both player and enemy.
        if (attacker == _playerSprite && !_playerDead)
            PlaySound("short_quick_steps.wav", volumeDb: 6f);
        if (attacker == _enemySprite && !_enemyDead)
            PlaySound("short_quick_steps.wav", volumeDb: 6f);

        // Play run at double speed while the player hops in — snappy charge feel.
        // Guard: only call Play("run") if the animation has frames. A 0-frame animation
        // (caused by a missing .import file for _Run.png) hides the sprite entirely.
        if (attacker == _playerSprite)
        {
            // OWNER: PlayHopIn (player turn, charge begins).
            int runFrames = _playerAnimSprite.SpriteFrames?.GetFrameCount("run") ?? 0;
            if (runFrames > 0)
            {
                _playerAnimSprite.SpeedScale = 2f;
                PlayPlayer("run");   // OWNER: player turn, hop-in charge
            }
            else
            {
                GD.PrintErr("[BattleTest] 'run' has 0 frames — staying on idle during hop-in. " +
                            "Open the Godot editor to auto-import _Run.png.");
            }
        }

        var tween = CreateTween();
        tween.SetParallel(true);
        // Hop in — ease-out (fast start, decelerates on arrival = lunge).
        tween.TweenProperty(attacker, "position", _attackerClosePos, SetupDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        // When the player is the attacker, the visible AnimatedSprite2D must move with the
        // ColorRect. Compute the same X delta and apply it to the sprite's floor-anchored origin.
        if (attacker == _playerSprite)
        {
            float   hopDeltaX  = _attackerClosePos.X - _playerOrigin.X;
            Vector2 animTarget = new Vector2(_playerAnimSpriteOrigin.X + hopDeltaX,
                                             _playerAnimSpriteOrigin.Y);
            tween.TweenProperty(_playerAnimSprite, "position", animTarget, SetupDuration)
                 .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        }
        // When the enemy is the attacker, play run at double speed during hop-in (mirrors player pattern).
        if (attacker == _enemySprite && !_enemyDead)
        {
            _enemyAnimSprite.SpeedScale = 2f;
            PlayEnemy("run");
        }
        // Move the enemy AnimatedSprite2D by the same X delta plus the full attackerOffset
        // (X already in _attackerClosePos; Y applied here only so the sprite moves vertically
        // without affecting the camera or target zone).
        if (attacker == _enemySprite)
        {
            float   hopDeltaX  = _attackerClosePos.X - _enemyOrigin.X;
            Vector2 animTarget = new Vector2(_enemyAnimSpriteOrigin.X + hopDeltaX,
                                             _enemyAnimSpriteOrigin.Y + attackerOffset.Y);
            tween.TweenProperty(_enemyAnimSprite, "position", animTarget, SetupDuration)
                 .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        }
        // Camera zooms in centered between the two combatants.
        tween.TweenProperty(_camera, "position", ComputeCameraMidpoint(), SetupDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        tween.TweenProperty(_camera, "zoom", CameraZoomIn, SetupDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        tween.Finished += () =>
        {
            // Reset enemy SpeedScale after hop-in and hold idle until melee_attack starts.
            if (attacker == _enemySprite && !_enemyDead)
            {
                _enemyAnimSprite.SpeedScale = 1f;
                PlayEnemy("idle");
            }
            onComplete?.Invoke();
        };
    }

    /// <summary>
    /// Unified entry point for player attack turns.
    /// Plays the hop-in animation, then adds the prompt to the scene tree when
    /// the animation completes (so the first input is only possible after the hop-in).
    /// </summary>
    private void BeginAttack(
        ColorRect attacker,
        ColorRect defender,
        TimingPrompt.PromptType promptType,
        TimingPrompt.PromptCompletedEventHandler onComplete)
    {
        // Build the prompt node but defer AddChild until the hop-in tween finishes.
        var prompt = _timingPromptScene.Instantiate<TimingPrompt>();
        prompt.Type            = promptType;
        prompt.BounceCount     = 2;  // Combo Strike uses 3 passes (2 bounces); ignored for Standard
        prompt.AutoLoop        = false;
        prompt.PassEvaluated   += OnAttackPassEvaluated;
        prompt.PromptCompleted += onComplete;
        _activePrompt = prompt;

        // Player hop-in overlaps the enemy similarly to how the enemy overlaps the player
        // via warrior_melee_combo.tres's Offset = Vector2(-200, 0). Positive X pushes the
        // left-side attacker (player) further right toward the enemy's body.
        Vector2 playerHopInOffset = (attacker == _playerSprite)
            ? new Vector2(200f, 0f)
            : Vector2.Zero;
        PlayHopIn(attacker, defender, () =>
        {
            // Hop-in finished — freeze on frame 0 (wind-up pose) without playing.
            // The slash fires from frame 1 only after the timing circle resolves,
            // so the pose reads as intent-to-strike while the player waits for input.
            if (attacker == _playerSprite)
            {
                // OWNER: BeginAttack hop-in callback (player turn, awaiting input).
                // "combo" frame 0 = first wind-up pose for both single and combo attacks.
                _playerAnimSprite.SpeedScale = 1f;
                _playerAnimSprite.Animation  = "combo";
                SetPlayerFrame(0);  // OWNER: player turn, wind-up pose (sheet frame 0)
                StopPlayer();
            }
            // Position is set here so ComputeCameraMidpoint() reflects the final close stance.
            prompt.Position      = ComputeCameraMidpoint();
            _targetZone.Position = prompt.Position;
            _targetZone.Visible  = true;
            prompt.ZIndex = 20;
            AddChild(prompt);
        }, playerHopInOffset);
    }

    /// <summary>
    /// Fires on every PassEvaluated — attacker briefly lunges to overlap the defender,
    /// then pulls back to the close stance. Works identically for all prompt types
    /// because TimingPrompt now emits PassEvaluated for Standard and Slow too.
    /// </summary>
    private void OnAttackPassEvaluated(int result, int passIndex)
    {
        // Slam tween on every pass — attacker lunges forward then snaps back to close stance.
        Vector2 slamPos = ComputeSlamPosition();
        var tween = CreateTween();
        tween.TweenProperty(_attacker, "position", slamPos, SlamInDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        tween.TweenProperty(_attacker, "position", _attackerClosePos, SlamOutDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);

        if (!_isComboAttack) return;  // single: animation driven entirely in OnPlayerPromptCompleted

        // Combo Strike — drive animation per pass using _AttackComboNoMovement.png frame layout.
        // Slashes alternate: slash1 (frames 1–3) and slash2 (frames 6–9).
        // The hold after each non-final slash is the wind-up for the NEXT slash:
        //   Pass 0: combo_slash1 → hold frame 5 (wind-up for slash2)
        //   Pass 1: combo_slash2 → hold frame 0 (wind-up for slash1 again)
        //   Pass 2: combo_slash1 → hold last frame of slash1 (final hit); retreat via OnFinalSlashFinished
        _comboPassIndex = passIndex;
        switch (_comboPassIndex)
        {
            case 0:
                PlaySound("player_attack_swing.wav");
                SafeDisconnectPlayerAnim(OnComboPass0SlashFinished);
                PlayPlayer("combo_slash1");  // OWNER: combo pass 0, first strike (frames 1–3)
                _playerAnimSprite.AnimationFinished += OnComboPass0SlashFinished;
                break;
            case 1:
                PlaySound("player_attack_swing.wav");
                SafeDisconnectPlayerAnim(OnComboPass1SlashFinished);
                PlayPlayer("combo_slash2");  // OWNER: combo pass 1, second strike (frames 6–9)
                _playerAnimSprite.AnimationFinished += OnComboPass1SlashFinished;
                break;
            case 2:
                // Final strike — OnFinalSlashFinished handles the 0.3s hold and retreat.
                // _pendingGameOver is set in OnPlayerPromptCompleted (PromptCompleted fires in
                // the same frame as the last PassEvaluated) for the all-hits case, or here when
                // the miss branch runs.
                PlaySound("player_attack_swing.wav");
                SafeDisconnectPlayerAnim(OnFinalSlashFinished);
                PlayPlayer("combo_slash1");  // OWNER: combo pass 2, final strike (frames 1–3)
                _playerAnimSprite.AnimationFinished += OnFinalSlashFinished;
                break;
        }

        // ── Combo per-pass damage ─────────────────────────────────────────────────
        // Apply damage on every pass. On a miss, cancel the remaining bounces:
        //   • Free the active prompt after the flash (stops the circle visually).
        //   • Hide the target zone — PromptCompleted will not fire for a mid-combo miss.
        //   • OnComboPassNSlashFinished detects _comboMissed and calls BeginComboMissRetreat
        //     instead of holding the wind-up pose (pass 0 and 1 only; pass 2 uses OnFinalSlashFinished).
        if (_comboMissed) return;  // already cancelled; ignore subsequent pass evaluations

        var comboDmgResult = (TimingPrompt.InputResult)result;
        int comboBase      = _playerComboStrike?.BaseDamage ?? 6;
        int comboDamage    = ComputePlayerDamage(comboBase, comboDmgResult);
        Color comboDmgColor = comboDmgResult switch
        {
            TimingPrompt.InputResult.Perfect => DmgColorPerfect,
            TimingPrompt.InputResult.Hit     => DmgColorHit,
            _                                => DmgColorMiss,
        };
        if (comboDmgResult == TimingPrompt.InputResult.Perfect)
            ShakeCamera(intensity: 6f, duration: 0.2f);
        _enemyHP = Mathf.Max(0, _enemyHP - comboDamage);
        GD.Print($"[BattleTest] Combo pass {passIndex + 1} {comboDmgResult}: {comboDamage} damage. " +
                 $"Enemy HP: {_enemyHP}/{_enemyMaxHP}");
        PlaySound("enemy_hit.wav");
        SpawnDamageNumber(EnemyDamageOrigin, comboDamage, comboDmgColor);
        ShakeCamera(intensity: 8f, duration: 0.25f);
        PlayEnemyHurtFlash();
        UpdateHPBars();

        if (comboDmgResult == TimingPrompt.InputResult.Miss)
        {
            _comboMissed        = true;
            _pendingGameOver    = CheckGameOver();
            _targetZone.Visible = false;  // PromptCompleted won't fire — hide zone here
            // TODO: dismiss remaining active circles with grey flash (stop-on-miss visual)
            GetTree().CreateTimer(TimingPrompt.FlashDuration).Timeout += FreeActivePrompt;
        }
    }

    /// <summary>
    /// Animates the attacker back to their origin (ease-in = slow start, accelerates away)
    /// and the camera back to its default position and zoom.
    /// Calls <paramref name="onComplete"/> when done; safe to pass null.
    /// </summary>
    private void PlayTeardown(Action onComplete)
    {
        var tween = CreateTween();
        tween.SetParallel(true);
        // Hop out — ease-in (slow start, accelerates = snapping away).
        tween.TweenProperty(_attacker, "position", GetOrigin(_attacker), TeardownDuration)
             .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
        // Return the visible player sprite to its scene origin alongside the ColorRect.
        if (_attacker == _playerSprite)
        {
            if (!_playerDead)
                PlaySound("short_quick_steps.wav", volumeDb: 0f);
            tween.TweenProperty(_playerAnimSprite, "position", _playerAnimSpriteOrigin, TeardownDuration)
                 .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
        }
        // Return the visible enemy sprite to its scene origin alongside the ColorRect.
        if (_attacker == _enemySprite)
        {
            tween.TweenProperty(_enemyAnimSprite, "position", _enemyAnimSpriteOrigin, TeardownDuration)
                 .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
            // Play run backwards at double speed during hop-back — only if the enemy
            // actually moved from origin (hop-in melee). Cast attacks stay at origin
            // so _attackerClosePos == origin; skip the run animation in that case.
            bool enemyMoved = _attackerClosePos != GetOrigin(_enemySprite);
            if (!_enemyDead && enemyMoved)
            {
                PlaySound("short_quick_steps.wav", volumeDb: 0f);
                _enemyAnimSprite.SpeedScale = 2f;
                _enemyAnimSprite.PlayBackwards("run");
            }
        }
        // Camera zooms back out to default.
        tween.TweenProperty(_camera, "position", CameraDefaultPos, TeardownDuration)
             .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
        tween.TweenProperty(_camera, "zoom", CameraDefaultZoom, TeardownDuration)
             .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
        bool enemyDidMove = _attacker == _enemySprite && _attackerClosePos != GetOrigin(_enemySprite);
        tween.Finished += () =>
        {
            // Reset enemy SpeedScale and return to idle after hop-back (only if enemy moved).
            if (enemyDidMove && !_enemyDead)
            {
                _enemyAnimSprite.SpeedScale = 1f;
                PlayEnemy("idle");
            }
            // Restore default ZIndex on both sprites now that the attack is over.
            // Exception: if the enemy is dying (phase transition in progress), leave
            // its ZIndex alone — SpawnBossReveal bumped it up so the reveal stays
            // strictly behind, and SwapToPhase2 restores the original snapshot value.
            _playerAnimSprite.ZIndex = 0;
            if (!_enemyDead)
                _enemyAnimSprite.ZIndex = 0;
            onComplete?.Invoke();
        };
    }

    // =========================================================================
    // Animation position helpers
    // =========================================================================

    /// <summary>Returns the stored world-space origin for the given sprite.</summary>
    private Vector2 GetOrigin(ColorRect sprite) =>
        sprite == _playerSprite ? _playerOrigin : _enemyOrigin;

    /// <summary>
    /// Returns the position where the attacker stands in the close stance —
    /// <see cref="AttackGap"/> pixels from the defender's near edge, same Y as origin.
    /// Calculated from stored origins so it is independent of any animation in progress.
    /// </summary>
    private Vector2 ComputeClosePosition()
    {
        Vector2 attackerOrigin = GetOrigin(_attacker);
        Vector2 defenderOrigin = GetOrigin(_defender);
        bool    onLeft         = attackerOrigin.X < defenderOrigin.X;

        float closeX = onLeft
            ? defenderOrigin.X - _attacker.Size.X - AttackGap   // attacker right edge = defender left - gap
            : defenderOrigin.X + _defender.Size.X + AttackGap;  // attacker left edge  = defender right + gap

        return new Vector2(closeX, attackerOrigin.Y);
    }

    /// <summary>
    /// Returns the slam position — attacker overlaps the defender by <see cref="SlamOverlap"/> pixels.
    /// Also calculated from stored origins so slam depth is always the same regardless of
    /// where the attacker currently is.
    /// </summary>
    private Vector2 ComputeSlamPosition()
    {
        Vector2 attackerOrigin = GetOrigin(_attacker);
        Vector2 defenderOrigin = GetOrigin(_defender);
        bool    onLeft         = attackerOrigin.X < defenderOrigin.X;

        float slamX = onLeft
            ? defenderOrigin.X - _attacker.Size.X + SlamOverlap   // right edge overlaps defender by SlamOverlap
            : defenderOrigin.X + _defender.Size.X - SlamOverlap;  // left edge overlaps defender by SlamOverlap

        return new Vector2(slamX, _attackerClosePos.Y);
    }

    /// <summary>
    /// Returns the world-space midpoint between the attacker's close stance center
    /// and the defender's center — the point the camera zooms in on.
    /// </summary>
    private Vector2 ComputeCameraMidpoint()
    {
        Vector2 attackerCenter = _attackerClosePos    + _attacker.Size / 2f;
        Vector2 defenderCenter = GetOrigin(_defender) + _defender.Size / 2f;
        return (attackerCenter + defenderCenter) / 2f;
    }
}
