using System;
using System.Collections.Generic;
using System.Linq;
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

    private enum BattleState { EnemyAttack, PlayerMenu, SelectingTarget, PlayerAttack, GameOver, Victory }
    private BattleState _state = BattleState.EnemyAttack;

    // True during attack resolution phases (slash animations, retreat, teardown) when all
    // input must be ignored. Set when the last timing circle resolves; cleared when the
    // next input-accepting state begins (ShowMenu or BeginEnemyAttack with active prompts).
    private bool _inputLocked;

    // =========================================================================
    // HP / MP defaults — used to seed Combatant fields at BuildInitialParties time.
    // Per-unit state lives on the Combatant now; see _playerParty / _enemyParty below.
    // =========================================================================

    private const int PlayerMaxHP = 100;

    // =========================================================================
    // MP
    // =========================================================================

    [Export] public int PlayerMaxMp = 50;

    [Export] public int EtherCount = 1;

    // UI bar references — built in BuildStatusPanels(), updated by UpdateHPBars()/UpdateMpBar().
    // Type is Control (not ColorRect) because the fill uses 3-part TextureRect children
    // and relies on ClipContents for the drain-from-right visual. The existing
    // `.Size = new Vector2(BarWidth * pct, ...)` update logic continues to work.
    // Per-combatant HP/MP status panels — one PartyPanel per combatant. Lists are
    // populated in BuildStatusPanels by looping _playerParty / _enemyParty (built in
    // BuildInitialParties). Replaces the pre-Phase-6 singleton fields
    // (_playerHPFill / _playerHPLabel / _playerMPFill / _playerMPLabel /
    // _enemyHPFill / _enemyHPLabel / _enemyNameLabel) so multi-unit combat
    // (4 / 5 at TestFullParty) renders each combatant's HP / MP / name independently.
    //
    // Player side: each panel is its own PanelContainer (PartyPanel.Panel != null)
    //              arranged in a count-aware centered strip at the bottom-center.
    // Enemy side : all enemies share a single combined panel (_enemyCombinedPanel)
    //              with one row per combatant inside its VBoxContainer. Each enemy's
    //              PartyPanel has Panel == null and ModulateTarget == the row HBox.
    private System.Collections.Generic.List<PartyPanel> _playerPanels = new();
    private System.Collections.Generic.List<PartyPanel> _enemyPanels  = new();
    private PanelContainer                              _enemyCombinedPanel;  // shared outer panel; rows live inside

    // C7 turn-order strip — vertical column at top-left showing the next
    // LookaheadCount combatants from _queue.Lookahead(). Cards are
    // instance-bound (persist across Refresh) so the slide animation moves
    // real card identities between slots. The single in-flight Tween handle
    // is Killed at the start of any new Refresh(animate:true) so successive
    // Advances during an in-progress slide fall back to hard-rebind for that
    // one Advance (no animation overlap).
    private System.Collections.Generic.List<TurnOrderCard> _turnOrderCards = new();
    private CanvasLayer                                    _turnOrderLayer;
    private Tween                                          _turnOrderTween;

    // =========================================================================
    // Perfect parry
    // =========================================================================

    // Per-sequence parry-outcome state. Stays on BattleTest — this is BattleTest's
    // control-flow state for the sequence, not sequence identity/data (which lives
    // on SequenceContext). Reset to true on BeginEnemyAttack; cleared to false on
    // any Miss in OnEnemyPassEvaluated; read at OnEnemySequenceCompleted to decide
    // whether to trigger the auto parry-counter.
    private bool _parryClean;

    // =========================================================================
    // Absorb tracking — party-level, per-move-type
    // =========================================================================

    // Set of AttackData references the Absorber has already learned. Absorption is
    // per-move-type, not per-enemy-instance — once a move is in this set, any enemy
    // with the same LearnableAttack offers no further absorption opportunity.
    //
    // Prototype simplification: lives on BattleTest as a party-level field. The
    // long-term correct home is per-Absorber-character skill data, pending Phase 2+
    // character persistence work. Migration is a mechanical call-site rename
    // (_absorbedMoves.Contains(x) → absorber.Skills.HasAbsorbed(x)).
    // See docs/combatant-abstraction-design.md Q1 addendum.
    private HashSet<AttackData> _absorbedMoves = new();

    // =========================================================================
    // Damage numbers
    // =========================================================================

    // Colors match the input result that caused the damage.
    private static readonly Color DmgColorPerfect = new Color(0.30f, 1.00f, 0.40f, 1.00f);  // green
    private static readonly Color DmgColorHit     = new Color(1.00f, 1.00f, 1.00f, 1.00f);  // white
    private static readonly Color DmgColorMiss    = new Color(0.60f, 0.60f, 0.60f, 1.00f);  // grey (weak hit)
    private static readonly Color DmgColorPlayer  = new Color(1.00f, 0.25f, 0.25f, 1.00f);  // red

    /// <summary>
    /// Returns the world-space spawn position for a damage number floating above the
    /// given combatant's sprite — top-center of the unit's ColorRect minus a 20px upward
    /// offset. Reads per-combatant <see cref="Combatant.Origin"/>, so multi-unit combat
    /// correctly anchors damage numbers to each combatant's home location. Uses rest
    /// position (not live <c>sprite.GlobalPosition</c>) so the number stays anchored
    /// even during slam / hop-in tweens.
    /// </summary>
    private static Vector2 ComputeDamageOrigin(Combatant unit) =>
        unit.Origin + new Vector2(unit.PositionRect.Size.X / 2f, -20f);

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

    // When true, start the battle with the enemy at 1 HP so the first player hit
    // kills the warrior and triggers the Phase 1 → Phase 2 transition immediately.
    // Test hook — lets us exercise the full reveal sequence without playing through
    // the whole Phase 1 fight.
    [Export] public bool TestPhaseTransition = false;

    // Development scaffolding — skip straight to the Phase 2 enemy at 1 HP so the first
    // player hit triggers Victory. Also skips the intro dialogue. Forgiving scaffolding;
    // takes priority over TestGameOverScreen and TestPhaseTransition when multiple are set.
    [Export] public bool TestVictoryScreen = false;

    // Development scaffolding — start with the player at 1 HP so the first enemy-attack miss
    // triggers Game Over. Also skips the intro dialogue. Takes priority over TestPhaseTransition
    // but is overridden by TestVictoryScreen.
    [Export] public bool TestGameOverScreen = false;

    /// <summary>
    /// Development scaffolding — populates the parties at 4 players
    /// vs 5 enemies for multi-unit density testing. All players are
    /// Knight copies; all enemies are Warrior Phase 1 copies. Phase 1
    /// → Phase 2 transition is suppressed when active because the
    /// transition assumes a single enemy. Lowest priority among test
    /// flags: TestVictoryScreen, TestGameOverScreen, TestPhaseTransition
    /// all override TestFullParty.
    /// </summary>
    [Export] public bool TestFullParty = false;

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

    // =========================================================================
    // Party rosters (Phase 6 C3)
    // =========================================================================

    /// <summary>
    /// Number of player combatants to construct at scene start. Default 1 preserves
    /// the pre-Phase-6 1v1 shape; the tscn-placed PlayerAnimatedSprite/PlayerSprite
    /// pair serves as slot 0 and additional slots (1..N-1) are spawned at runtime by
    /// BuildInitialParties. C5 (queue) wires active-player rotation; until then only
    /// slot 0 receives turns — additional slots are idle scenery.
    /// </summary>
    [Export] public int PlayerPartySize = 1;

    /// <summary>
    /// Number of enemy combatants to construct at scene start. Default 1 preserves
    /// the pre-Phase-6 1v1 shape. Additional slots are Warrior Phase 1 copies sharing
    /// slot 0's EnemyData/SpriteFrames reference. At default (1) the Phase 1 → Phase 2
    /// transition works as today; at multi-enemy rosters (TestFullParty in C4 sets 5)
    /// the transition is explicitly suppressed.
    /// </summary>
    [Export] public int EnemyPartySize = 1;

    private BattleMessage  _battleMessage;
    // Flash material and tween live on enemyCombatant (see Combatant.FlashMaterial /
    // FlashTween). The material is created in _Ready, attached to the enemy AnimatedSprite2D,
    // and then referenced on the Combatant in BuildInitialParties.

    // Music playback — dedicated AudioStreamPlayer, separate from one-shot SFX players
    // created by PlaySound. Loops via Finished signal so format-specific loop flags aren't
    // relied on. _musicStopping suppresses the loop during a fade-out so FadeOutMusic → Stop
    // is authoritative.
    private const string Phase1MusicPath = "res://Assets/Audio/Music/Batalha #1.ogg";
    private const string Phase2MusicPath = "res://Assets/Audio/Music/colossal_3_looped.mp3";
    private AudioStreamPlayer _musicPlayer;
    private AudioStream       _currentMusicStream;
    private Tween             _musicFadeTween;
    private bool              _musicStopping;

    private TimingPrompt     _activePrompt;
    private PackedScene      _timingPromptScene;
    private BattleSystem     _battleSystem;
    private AnimatedSprite2D _enemyAnimSprite;
    private AnimatedSprite2D _playerAnimSprite;
    private TargetZone       _targetZone;       // shared target ring — shown for the duration of any prompt sequence
    private TargetPointer    _targetPointer;    // selection-phase pointer; shown during SelectingTarget state only (Phase 4)
    private BattleDialogue   _introDialogue;    // owned narrative-dialogue component; QueueFree'd after DialogueCompleted

    // Phase 4 — target selection. Between a menu pick and the Begin* that launches
    // the attack, the player confirms (or cancels) the target via the pointer.
    // _selectedTarget is the currently-highlighted combatant (read by Begin* once
    // the player confirms); _pendingActionLauncher is the closure that fires the
    // attack (including MP deduction) when the player presses battle_confirm.
    // _selectingTargetMenuContext remembers which menu context the target-select
    // was entered from so Cancel returns to the correct menu (main / Absorbed
    // Moves submenu / Items submenu). All three fields are cleared on confirm
    // and on cancel.
    private enum MenuContext { Main, Skills, Items }
    private Combatant     _selectedTarget;
    private System.Action _pendingActionLauncher;
    private MenuContext   _selectingTargetMenuContext;

    // Party lists owned by BattleTest. Single-entry for the 1v1 prototype; the
    // scaffolding exercise grows them to 4/5. Source of truth for combat-universal
    // state (HP, MP, defending, dead flags, beckoning) and player/enemy-specific
    // fields (MP on player, LearnableAttack Data on enemy, FlashMaterial on enemy).
    //
    // Sprite/material refs are held by the Combatant but the scene tree owns the
    // node lifecycle — Combatant just points at the existing nodes.
    // See docs/combatant-abstraction-design.md.
    private List<Combatant> _playerParty = new();
    private List<Combatant> _enemyParty  = new();

    // Phase 5 — threat-reveal target list, populated each enemy turn in BeginEnemyAttack
    // and read by the threat-reveal fire loop. Single entry today (enemy always targets
    // the single player); multi-target attacks post-Phase-6 populate more entries. Cleared
    // + repopulated each turn rather than accumulated so stale entries from previous turns
    // don't bleed into the current threat reveal.
    private List<Combatant> _threatenedCombatants = new();

    // =========================================================================
    // Characters and animations
    // =========================================================================

    private ColorRect _playerSprite;
    private ColorRect _enemySprite;
    private Vector2   _playerOrigin;          // ColorRect position at scene load — positioning math anchor
    private Vector2   _enemyOrigin;
    // Per-slot AnimatedSprite2D origins (post-floor-anchor) live on the Combatant
    // (Combatant.AnimSpriteOrigin), set in BuildPlayerCombatantForSlot /
    // BuildEnemyCombatantForSlot. The pre-Phase-6 singleton snapshots
    // _playerAnimSpriteOrigin / _enemyAnimSpriteOrigin only tracked slot 0.

    // Per-sequence combat context — set at the start of each attack turn, read by
    // positioning helpers and animation-callback continuations throughout the sequence.
    // These are Option Y from the Phase 3.4 migration: sequence-scoped fields rather
    // than parameter-threaded values. Parameter threading would require lambdas at every
    // AnimationFinished subscription site (`+= () => OnFinalSlashFinished(attacker, ...)`),
    // breaking the SafeDisconnectAnim(_playerParty[0], methodName) reference-equality pattern used
    // throughout. Option Y keeps the existing per-sequence storage pattern; the refactor
    // is the type change (ColorRect → Combatant) and the rename.
    // Active player whose menu is currently being shown / acted on. Assigned by
    // AdvanceTurn before ShowMenu fires for the player branch. Read by
    // RebuildSubMenu (gates Beckon and absorbed moves on IsAbsorber), the
    // launchers in ConfirmMenuSelection / ConfirmSubMenuSelection (MP deduction,
    // Defend toggle, default target for Cure / Items), and IsSubMenuOptionEnabled
    // (per-active-player MP-affordability checks).
    private Combatant _activePlayer;

    // Round-order queue (Phase 6 C5). Rebuilt at scene start, on Phase 2
    // transition, and on round exhaustion inside AdvanceTurn. Owns turn-order
    // semantics — call sites that previously fired ShowMenu / BeginEnemyAttack
    // directly now route through AdvanceTurn, which advances the queue and
    // dispatches to the appropriate side.
    private TurnOrderQueue _queue = new();

    // Set true at the end of intro dialogue; consumed once by AdvanceTurn to apply
    // the post-intro menu fade-in (ShowMenuWithFadeIn instead of ShowMenu) on the
    // first player turn. Avoids carrying a separate fade-aware AdvanceTurn variant.
    private bool _firstTurnAfterIntro;

    // Random source for enemy target selection (uniform random over alive players
    // when no Beckon redirect is active). Seeded in _Ready via Randomize().
    private Godot.RandomNumberGenerator _rng = new();

    private Combatant _sequenceAttacker;
    private Combatant _sequenceDefender;
    private Vector2   _sequenceAttackerClosePos;  // close-but-not-touching stance position for this sequence
    // Dying combatant for the in-flight death animation. Set immediately before each
    // death-subscription site (enemy or player death) so OnEnemyDeathFinished /
    // OnPlayerDeathFinished can route their disconnects/teardown through the correct
    // combatant without relying on _sequenceAttacker/_sequenceDefender (which can point
    // at either side depending on whether a player attack, enemy attack, or parry counter
    // killed the combatant). Sequence-scoped: overwritten on each death; retention past
    // handler fire is harmless.
    private Combatant _sequenceDeathTarget;
    // Target of the most recent PlayCombatantHurtFlash call. Set at PlayCombatantHurtFlash
    // entry; read by OnEnemyHurtFlashFinished for its disconnect + idle reset. Decouples
    // the hurt-flash callback from the sequence-scoped fields since the flash can fire
    // outside of a normal enemy-sequence context (e.g. learnable-move selection on the
    // enemy at the start of a turn, before the attack sequence begins).
    private Combatant _lastHurtFlashTarget;
    private bool      _pendingGameOver;   // cached result of CheckGameOver(); read by OnFinalSlashFinished
    // Death flags live on Combatant.IsDead now (_playerParty[0].IsDead / _enemyParty[0].IsDead).
    // Once set, no further animation calls can override the death pose — see the five
    // dead-flag guard helpers in BattleAnimator.cs (PlayAnim, StopAnim, etc.).
    private bool      _endLabelShown;     // idempotent guard for ShowEndLabel — lets death-start sites trigger the
                                          // Game Over overlay + music fade immediately while OnPlayerDeathFinished's
                                          // own ShowEndLabel call (at death-anim completion) becomes a no-op.
    private bool      _reloadPending;     // true once the Retry fade-to-black begins. Documentation flag — actual
                                          // callback guards use IsInsideTree() so they work without accessing
                                          // BattleTest state from BattleSystem (a separate class).

    // Game Over options panel — populated by AddGameOverOptions from ShowEndLabel.
    // Navigation handled by HandleGameOverInput when _state == BattleState.GameOver.
    private int       _gameOverOptionIndex;    // 0 = Retry, 1 = Quit
    private Label[]   _gameOverTextLabels;     // centered option text; yellow when selected, white otherwise
    private Label[]   _gameOverArrows;         // ► cursor absolutely anchored left; Visible toggled per selection
    private ulong     _gameOverInputUnlockedAtMsec;  // Game Over input buffer — 150ms held-input drain set
                                                     // at ShowGameOverOptionsPanel entry (mirrors Victory's
                                                     // pattern). The 2.0s beat before that is implicit in the
                                                     // timer cascade from ShowEndLabel; cumulative end-to-end
                                                     // lockout is 2150ms — matches Victory exactly.
                                                     // Input rejected while Time.GetTicksMsec() < this value.
    private static readonly string[] GameOverOptionLabels = { "Retry", "Quit" };

    // Victory options panel — parallel to the Game Over panel. Populated by AddVictoryOptions
    // from ShowEndLabel's Victory branch after the 1.5s post-label beat. Navigation handled by
    // HandleVictoryInput when _state == BattleState.Victory. Separate fields from the Game Over
    // panel keep the two screens independent even though they look similar.
    private int       _victoryOptionIndex;        // 0 = Retry, 1 = Close
    private Label[]   _victoryTextLabels;
    private Label[]   _victoryArrows;
    private ulong     _victoryInputUnlockedAtMsec; // 150ms input buffer; input rejected while ticks_msec < this value
    private static readonly string[] VictoryOptionLabels = { "Retry", "Close" };
    private bool      _isComboAttack;       // true when the current player turn uses Combo Strike (Bouncing prompt)
    private bool      _isPlayerMagicAttack; // true when the current player turn uses a magic attack via BattleSystem
    private int       _comboPassIndex;      // which Bouncing pass just resolved; set in OnAttackPassEvaluated

    // Loaded once in _Ready; used to restore the enemy attack after a player magic turn.
    private AttackData _enemyAttackData;
    private int        _lastAttackIndex = -1;  // tracks last AttackSelector pick for Sequential support
    private AttackData _playerMagicAttack;
    private AttackData _playerBasicAttack;   // player_basic_attack.tres — Physical, BaseDamage 10
    private AttackData _playerComboStrike;   // player_combo_strike.tres — Physical, BaseDamage 6
    private AttackData _playerCureAttack;    // player_cure.tres — Magic, BaseDamage 30 (used as heal amount)
    private AttackData _playerEtherEffect;   // player_ether_combo.tres — active variant for Ether item visual

    // Set before BeginPlayerMagicAttack() to select which magic attack the cast flow uses.
    // OnPlayerCastFinished reads this instead of _playerMagicAttack directly.
    private AttackData _activeMagicAttack;

    // Set at the start of each combo turn; cleared in BeginPlayerAttack and BeginComboMissRetreat.
    // When true, OnComboPassNSlashFinished skips the wind-up hold and triggers the retreat instead.
    private bool       _comboMissed;

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

        ApplyBackgroundGradient();
        _battleMessage = new BattleMessage(this);
        BuildMusicPlayer();
        // Phase 1 music is deferred — started silent and faded in when the intro dialogue
        // begins its fade-out. See PlayIntroDialogue and OnIntroDialogueFadeOutStarted.

        // Phase 2 fallback — hoisted up from its original position later in _Ready so
        // TestVictoryScreen can swap EnemyData over before the enemy sprite is built.
        if (Phase2EnemyData == null)
        {
            Phase2EnemyData = GD.Load<EnemyData>("res://Resources/Enemies/8_sword_warrior_phase2.tres");
            if (Phase2EnemyData == null)
                GD.PrintErr("[BattleTest] Failed to load default Phase2EnemyData (8_sword_warrior_phase2.tres).");
            else
                GD.Print("[BattleTest] Phase2EnemyData defaulted to 8_sword_warrior_phase2.tres.");
        }

        // Resolve end-of-battle test flags. Priority: Victory > GameOver > PhaseTransition >
        // FullParty. Test flags are forgiving scaffolding — if multiple are set, the higher-
        // priority one wins and the others are logged-and-ignored rather than erroring out.
        bool testVictory     = TestVictoryScreen;
        bool testGameOver    = TestGameOverScreen && !testVictory;
        bool testPhaseTrans  = TestPhaseTransition && !testVictory && !testGameOver;
        bool testFullParty   = TestFullParty && !testVictory && !testGameOver && !testPhaseTrans;
        if (TestVictoryScreen && (TestGameOverScreen || TestPhaseTransition))
            GD.PrintErr("[TEST] TestVictoryScreen overrides TestGameOverScreen and TestPhaseTransition.");
        else if (TestGameOverScreen && TestPhaseTransition)
            GD.PrintErr("[TEST] TestGameOverScreen overrides TestPhaseTransition.");
        if (TestFullParty && (testVictory || testGameOver || testPhaseTrans))
            GD.PrintErr("[TEST] Victory/GameOver/PhaseTransition overrides TestFullParty.");
        bool skipIntro = testVictory || testGameOver;

        // TestVictoryScreen: swap EnemyData to Phase 2 before sprite build so the
        // scene renders the 8 Sword Warrior from the start.
        if (testVictory && Phase2EnemyData != null)
        {
            EnemyData = Phase2EnemyData;
            SkipHopIn = true;  // 8 Sword Warrior is stationary — hop-in would look wrong.
            GD.Print("[TEST] TestVictoryScreen active — skipping intro, starting Phase 2 with enemy at 1 HP.");
        }
        else if (testGameOver)
        {
            GD.Print("[TEST] TestGameOverScreen active — skipping intro, player HP set to 1.");
        }
        else if (testFullParty)
        {
            // Multi-unit roster scaffolding. Override the inspector PartySize values; null
            // out Phase2EnemyData so the Phase 1 → Phase 2 transition is suppressed at 4v5
            // (the transition logic assumes a single enemy and would corrupt state when
            // slots 1-4 are still alive at warrior death).
            PlayerPartySize = 4;
            EnemyPartySize  = 5;
            Phase2EnemyData = null;
            GD.Print("[TEST] TestFullParty active — 4 players vs 5 enemies.");
            GD.Print("[TEST] TestFullParty suppresses Phase 1 → Phase 2 transition.");
        }

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
        // Per-slot AnimSpriteOrigin snapshots are taken in BuildPlayerCombatantForSlot /
        // BuildEnemyCombatantForSlot — the slot-0 sprite's snapshot reads the position
        // set on the line above.
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
        // Slot-0 enemy AnimSpriteOrigin snapshot taken in BuildEnemyCombatantForSlot.
        _enemyAnimSprite.Play("idle");

        // Combatant-overlay shader — composites two independent effects (white flash
        // for learnable-move signalling, red tint for Phase 5 threat reveal) on a
        // single material slot. Each combatant gets its own ShaderMaterial instance so
        // the uniforms are per-combatant; both sprites get one attached here and are
        // later referenced by Combatant.FlashMaterial in BuildInitialParties.
        var overlayShader = GD.Load<Shader>("res://Assets/Shaders/CombatantOverlay.gdshader");

        var enemyOverlayMaterial = new ShaderMaterial();
        enemyOverlayMaterial.Shader = overlayShader;
        enemyOverlayMaterial.SetShaderParameter("flash_amount", 0.0f);
        enemyOverlayMaterial.SetShaderParameter("tint_amount",  0.0f);
        _enemyAnimSprite.Material = enemyOverlayMaterial;

        var playerOverlayMaterial = new ShaderMaterial();
        playerOverlayMaterial.Shader = overlayShader;
        playerOverlayMaterial.SetShaderParameter("flash_amount", 0.0f);
        playerOverlayMaterial.SetShaderParameter("tint_amount",  0.0f);
        _playerAnimSprite.Material = playerOverlayMaterial;

        _battleSystem = new BattleSystem();
        AddChild(_battleSystem);

        // Fallback attack used as a last-resort in SelectEnemyAttack when no EnemyData
        // attack pool is configured. Inspector-assigned TestEnemyAttack wins when set.
        _enemyAttackData = TestEnemyAttack
                           ?? GD.Load<AttackData>("res://Resources/Attacks/fire_and_ice_sword_combo.tres");
        if (_enemyAttackData == null)
            GD.PrintErr("[BattleTest] Failed to load fallback enemy attack.");
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

        // TargetPointer is code-instantiated (no .tscn). Added as a child of BattleTest so
        // it inherits scene-tree lifecycle; world-space Node2D so its position tracks
        // combatant sprites without manual world-to-screen conversion.
        _targetPointer = new TargetPointer();
        AddChild(_targetPointer);

        // Construct party lists before HP init / test-flag overrides. Combatant fields
        // are the source of truth; the HP init + test-flag blocks below write directly
        // into playerCombatant / enemyCombatant, not into retired singleton fields.
        // C3: loops PlayerPartySize / EnemyPartySize to support multi-unit rosters.
        // overlayShader threads through so per-slot ShaderMaterial instances share the
        // shader resource without re-loading from disk.
        BuildInitialParties(overlayShader);

        var playerCombatant = _playerParty[0];
        var enemyCombatant  = _enemyParty[0];

        // EnemyData overrides the default max HP when assigned in the inspector.
        if (EnemyData != null && EnemyData.MaxHp > 0)
        {
            enemyCombatant.MaxHp     = EnemyData.MaxHp;
            enemyCombatant.CurrentHp = EnemyData.MaxHp;
            GD.Print($"[BattleTest] EnemyData \"{EnemyData.EnemyName}\" loaded — " +
                     $"MaxHp={EnemyData.MaxHp}, AttackPool={EnemyData.AttackPool?.Length ?? 0} attack(s).");
        }

        // Test hooks — applied AFTER EnemyData HP init so they aren't clobbered. MaxHp is
        // preserved so HP bars show the correct scale. Priority order resolved above.
        // Victory/GameOver flags iterate the full party so the aggregate wipe predicate
        // fires on the first triggering hit even at multi-unit rosters. At the default
        // PartySize=1 these loops are single-iteration no-ops. PhaseTransition stays
        // slot-0-only because its intent is the Phase 1 → Phase 2 transition, not
        // an aggregate test.
        if (testVictory)
        {
            foreach (var e in _enemyParty)
                e.CurrentHp = 1;
            // Consume the phase-transition gate so enemy death goes straight to Victory
            // instead of retriggering the Phase 1 → Phase 2 reveal cutscene.
            _phaseTransitionConsumed = true;
        }
        else if (testPhaseTrans)
        {
            enemyCombatant.CurrentHp = 1;
            GD.Print("[BattleTest] TestPhaseTransition active — enemy HP forced to 1.");
        }
        if (testGameOver)
        {
            foreach (var p in _playerParty)
                p.CurrentHp = 1;
        }

        // Status panels iterate _playerParty / _enemyParty (built above) — must run AFTER
        // BuildInitialParties + EnemyData HP init + test-flag HP overrides so each per-slot
        // panel binds to a fully-initialised Combatant.
        BuildStatusPanels();
        BuildMenu();
        UpdateHPBars();

        // Seed the enemy-target RNG and zero the queue's AP state. The queue
        // exists from scene start regardless of intro path; the first
        // AdvanceTurn invocation simulates ticks until the highest-Agility
        // combatant crosses threshold (C7-prerequisite tick-based scheduler).
        _rng.Randomize();
        _queue.Reset(_playerParty, _enemyParty);
        // C7: build the turn-order strip from the seeded Lookahead. The first
        // AdvanceTurn fires Refresh(animate:true), which slides the top card
        // off as the first turn resolves.
        BuildTurnOrderStrip();

        if (skipIntro)
        {
            // Test-flag path — skip the intro dialogue entirely and go straight to the
            // normal turn flow. Start the phase-appropriate music at full volume so the
            // scene doesn't open silent.
            if (testVictory) StartPhase2Music();
            else             StartPhase1Music();
            AdvanceTurn();
        }
        else
        {
            // Intro dialogue runs before any turn flow. Menu stays hidden and music stays
            // silent until OnIntroDialogueFadeOutStarted (music fade-in) and
            // OnIntroDialogueCompleted (ShowMenu fade-in) fire. Menu _menuLayer.Visible
            // is already false from BuildMenu.
            _inputLocked = true;
            PlayIntroDialogue();
        }
    }

    public override void _Input(InputEvent @event)
    {
        // End-screen states (GameOver, Victory) route BEFORE the _inputLocked guard.
        // _inputLocked is a combat-phase signal that blocks input during slash animations,
        // retreats, and teardowns. End-screens are post-combat and exist precisely to
        // accept player input for option selection — gating them on a combat-phase flag
        // is a category error. This caused a silent non-responsive Victory panel when
        // the killing-blow attack left _inputLocked = true and no code path cleared it
        // before the Victory panel faded in. Do not move these below the _inputLocked
        // check; the same bug is latent on the Game Over path under any future trigger
        // site where _inputLocked happens to be true at state transition.
        if (_state == BattleState.GameOver)
        {
            HandleGameOverInput(@event);
            return;
        }
        if (_state == BattleState.Victory)
        {
            HandleVictoryInput(@event);
            return;
        }

        // Combat-phase hard lock — active during attack resolution, retreat, and teardown.
        // No combat input is processed until the next input-accepting state begins.
        if (_inputLocked) return;

        switch (_state)
        {
            case BattleState.PlayerMenu:
                HandleMenuInput(@event);
                break;

            case BattleState.SelectingTarget:
                HandleSelectingTargetInput(@event);
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
    // Music playback — Phase 1 / Phase 2 tracks, loop via Finished signal,
    // fade-out for phase transition and end-of-battle
    // =========================================================================

    /// <summary>
    /// Creates the dedicated music AudioStreamPlayer and wires the Finished signal to
    /// seamlessly restart the current track. Called once from _Ready.
    /// </summary>
    private void BuildMusicPlayer()
    {
        _musicPlayer      = new AudioStreamPlayer();
        _musicPlayer.Name = "MusicPlayer";
        _musicPlayer.Bus  = "Master";
        AddChild(_musicPlayer);
        _musicPlayer.Finished += OnMusicFinished;
    }

    /// <summary>
    /// Loops the currently-assigned stream by replaying on Finished — unless a fade-out
    /// has set <see cref="_musicStopping"/>, in which case we let the player stay stopped.
    /// </summary>
    private void OnMusicFinished()
    {
        if (_musicStopping || _musicPlayer == null || _currentMusicStream == null) return;
        _musicPlayer.Play();
    }

    /// <summary>
    /// Starts Phase 1 battle music at 0 dB. Cancels any pending fade-out and resets the
    /// stopping flag so the Finished-signal loop resumes.
    /// </summary>
    private void StartPhase1Music() => PlayMusicStream(Phase1MusicPath, volumeDb: 0f);

    /// <summary>
    /// Starts Phase 2 battle music at +2 dB — roughly 20% louder than Phase 1 — so the
    /// boss theme reads as more intense than the Phase 1 track. Same semantics as
    /// <see cref="StartPhase1Music"/> otherwise.
    /// </summary>
    private void StartPhase2Music() => PlayMusicStream(Phase2MusicPath, volumeDb: 2f);

    /// <summary>
    /// Shared implementation for StartPhaseXMusic — loads the stream, resets volume/flag,
    /// and plays at the requested <paramref name="volumeDb"/>. Silently no-ops if the
    /// player isn't built yet or the stream fails to load.
    /// </summary>
    private void PlayMusicStream(string resPath, float volumeDb)
    {
        if (_musicPlayer == null) return;

        var stream = GD.Load<AudioStream>(resPath);
        if (stream == null)
        {
            GD.PrintErr($"[BattleTest] Music failed to load: {resPath}");
            return;
        }

        _musicFadeTween?.Kill();
        _musicStopping      = false;
        _currentMusicStream = stream;
        _musicPlayer.Stream = stream;
        _musicPlayer.VolumeDb = volumeDb;
        _musicPlayer.Play();
        GD.Print($"[BattleTest] Music started: {resPath} @ {volumeDb}dB");
    }

    /// <summary>
    /// Fades the music player's VolumeDb from current to -80 dB (effectively silent) over
    /// <paramref name="durationSec"/> seconds, then calls Stop(). Sets _musicStopping first
    /// so the Finished-signal loop handler doesn't resurrect the track mid-fade.
    /// Safe to call when no music is playing — becomes a no-op.
    /// </summary>
    private void FadeOutMusic(float durationSec)
    {
        if (_musicPlayer == null || !_musicPlayer.Playing) return;

        _musicStopping = true;
        _musicFadeTween?.Kill();
        _musicFadeTween = CreateTween();
        _musicFadeTween.TweenProperty(_musicPlayer, "volume_db", -80f, durationSec);
        _musicFadeTween.TweenCallback(Callable.From(() =>
        {
            if (_musicPlayer != null) _musicPlayer.Stop();
        }));
        GD.Print($"[BattleTest] Music fading out over {durationSec}s.");
    }

    /// <summary>
    /// Starts Phase 1 music silent and tweens VolumeDb to 0 dB over <paramref name="durationSec"/>
    /// seconds — the fade-in counterpart to <see cref="FadeOutMusic"/>. Used for the post-intro
    /// handoff where dialogue ends and battle music enters under the menu fade-in.
    /// </summary>
    private void FadeInPhase1Music(float durationSec)
    {
        if (_musicPlayer == null) return;
        PlayMusicStream(Phase1MusicPath, volumeDb: -80f);
        _musicFadeTween?.Kill();
        _musicFadeTween = CreateTween();
        _musicFadeTween.TweenProperty(_musicPlayer, "volume_db", 0f, durationSec);
        GD.Print($"[BattleTest] Phase 1 music fading in over {durationSec}s.");
    }

    // =========================================================================
    // Intro dialogue — plays once at scene load before any turn flow begins.
    // =========================================================================

    /// <summary>
    /// Constructs the intro-dialogue component, hardcodes the opening lines, connects signals,
    /// and kicks the sequence off after a 0.3s scene-settle beat. Called from _Ready with
    /// _inputLocked already set so the player cannot trigger menu input before dialogue starts.
    /// </summary>
    private void PlayIntroDialogue()
    {
        _introDialogue = new BattleDialogue();
        _introDialogue.Name = "BattleDialogue";
        AddChild(_introDialogue);
        _introDialogue.FadeOutStarted    += OnIntroDialogueFadeOutStarted;
        _introDialogue.DialogueCompleted += OnIntroDialogueCompleted;

        var lines = new BattleDialogue.DialogueLine[]
        {
            new BattleDialogue.DialogueLine { Speaker = "The Harbinger", Text = "...another one. and already, it squirms.", AutoAdvanceSeconds = 2.0f },
            new BattleDialogue.DialogueLine { Speaker = "Knight",        Text = "Grip firm...",                               AutoAdvanceSeconds = 1.2f },
            new BattleDialogue.DialogueLine { Speaker = "Knight",        Text = "...breathe out on the strike...",            AutoAdvanceSeconds = 1.2f },
            new BattleDialogue.DialogueLine { Speaker = "The Harbinger", Text = "Stand, if you wish.",                        AutoAdvanceSeconds = 1.2f },
            new BattleDialogue.DialogueLine { Speaker = "The Harbinger", Text = "It changes nothing.",                        AutoAdvanceSeconds = 2.0f },
        };

        var timer = GetTree().CreateTimer(0.3);
        timer.Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(this) || !GodotObject.IsInstanceValid(_introDialogue)) return;
            _introDialogue.PlayDialogue(lines);
        };
    }

    /// <summary>
    /// Fires when the intro dialogue begins its fade-out tween (final line advanced). Starts
    /// the music fade-in at the same moment so audio rises during the visual handoff rather
    /// than after it completes — a smoother transition than strict sequencing.
    /// </summary>
    private void OnIntroDialogueFadeOutStarted()
    {
        FadeInPhase1Music(1.5f);
    }

    /// <summary>
    /// Fires after the intro dialogue's panel fade-out plus post-dialogue input buffer. Shows
    /// the battle menu with a 0.5s fade-in, unlocks input via ShowMenu, and frees the dialogue
    /// node (future dialogue — e.g. Phase 2 taunts — constructs a fresh BattleDialogue).
    /// </summary>
    private void OnIntroDialogueCompleted()
    {
        // Flag consumed once inside AdvanceTurn — if the first turn goes to a
        // player, AdvanceTurn applies the post-intro fade-in via
        // ShowMenuWithFadeIn instead of ShowMenu. Single AdvanceTurn entry
        // point; no separate fade-aware variant needed.
        _firstTurnAfterIntro = true;
        AdvanceTurn();

        if (GodotObject.IsInstanceValid(_introDialogue))
        {
            _introDialogue.QueueFree();
            _introDialogue = null;
        }
    }

    // =========================================================================
    // Game Over options panel — Retry / Quit shown under the "Game Over" text
    // =========================================================================

    /// <summary>
    /// Builds the Retry/Quit options rows under the Game Over title. Called from ShowEndLabel
    /// with the layered panel's content VBox as host. Each row is a Control containing two
    /// siblings:
    ///   • a full-rect-anchored text Label (centered, so text is always horizontally centered
    ///     within the row)
    ///   • an absolutely-positioned ► arrow Label on the left (fixed offset, not part of the
    ///     text flow, so text width doesn't shift centering and arrow position is identical
    ///     across rows regardless of which option is longer)
    /// Selected row: arrow visible, text tinted yellow. Unselected: arrow hidden, text white.
    /// Options fade in with the rest of the overlay via the wrapper's Modulate tween.
    /// </summary>
    private void AddGameOverOptions(VBoxContainer host)
    {
        _gameOverOptionIndex  = 0;
        _gameOverTextLabels   = new Label[GameOverOptionLabels.Length];
        _gameOverArrows       = new Label[GameOverOptionLabels.Length];

        const float RowWidth         = 320f;
        const float RowHeight        = 56f;   // accommodates larger option font (~44px) + drop shadow
        const int   OptionFontSize   = 44;
        const int   ArrowFontSize    = 24;    // more proportional to the 44px option text while still
                                              // reading as a cursor mark, not a peer of the label text
        const float ArrowOffsetLeft  = 72f;   // fixed distance from row's left edge — identical per row
        const float ArrowWidth       = 24f;
        const float ArrowBoxHeight   = 32f;   // explicit height anchored to row midline for predictable
                                              // vertical centering regardless of font metric differences
                                              // between the small arrow glyph and the large option text

        // Nested options stack inside the host — keeps option-to-option separation (12) tighter
        // than the host's title/options separation (24 from ShowEndLabel).
        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 12);
        stack.MouseFilter = Control.MouseFilterEnum.Ignore;
        host.AddChild(stack);

        for (int i = 0; i < GameOverOptionLabels.Length; i++)
        {
            // Row container — fixed size. Houses text + arrow as independent siblings.
            var row = new Control();
            row.CustomMinimumSize = new Vector2(RowWidth, RowHeight);
            row.MouseFilter       = Control.MouseFilterEnum.Ignore;
            stack.AddChild(row);

            // Text label — full-rect anchored so HorizontalAlignment.Center centers within
            // the full row width. Arrow is a sibling, not part of this label, so text width
            // doesn't affect arrow position and arrow doesn't shift the text.
            var textLabel = new Label();
            textLabel.Text                = GameOverOptionLabels[i];
            textLabel.HorizontalAlignment = HorizontalAlignment.Center;
            textLabel.VerticalAlignment   = VerticalAlignment.Center;
            textLabel.AnchorLeft          = 0f;
            textLabel.AnchorRight         = 1f;
            textLabel.AnchorTop           = 0f;
            textLabel.AnchorBottom        = 1f;
            textLabel.MouseFilter         = Control.MouseFilterEnum.Ignore;
            StyleLabel(textLabel, fontSize: OptionFontSize);
            row.AddChild(textLabel);
            _gameOverTextLabels[i] = textLabel;

            // Arrow label — absolutely positioned at a fixed left offset. Width/position are
            // identical for every row so the cursor snaps cleanly when moving between options.
            // Height is a small explicit box anchored to the row midline (AnchorTop/Bottom=0.5)
            // so the arrow glyph's visual center aligns with the row center regardless of the
            // font metric difference between 18px arrow and 44px option text.
            var arrow = new Label();
            arrow.Text                = "▶";
            arrow.HorizontalAlignment = HorizontalAlignment.Center;
            arrow.VerticalAlignment   = VerticalAlignment.Center;
            arrow.AnchorLeft          = 0f;
            arrow.AnchorRight         = 0f;
            arrow.AnchorTop           = 0.5f;
            arrow.AnchorBottom        = 0.5f;
            arrow.OffsetLeft          = ArrowOffsetLeft;
            arrow.OffsetRight         = ArrowOffsetLeft + ArrowWidth;
            arrow.OffsetTop           = -ArrowBoxHeight * 0.5f;
            arrow.OffsetBottom        =  ArrowBoxHeight * 0.5f;
            arrow.MouseFilter         = Control.MouseFilterEnum.Ignore;
            StyleLabel(arrow, fontSize: ArrowFontSize);
            arrow.Modulate            = ColorMenuSelected;  // always yellow; visibility toggled by selection
            row.AddChild(arrow);
            _gameOverArrows[i] = arrow;
        }

        RefreshGameOverOptions();
    }

    /// <summary>
    /// Repaints the Retry/Quit options based on <see cref="_gameOverOptionIndex"/>.
    /// Selected row: arrow visible, text tinted yellow. Unselected: arrow hidden, text white.
    /// </summary>
    private void RefreshGameOverOptions()
    {
        if (_gameOverTextLabels == null) return;
        for (int i = 0; i < _gameOverTextLabels.Length; i++)
        {
            bool selected = (i == _gameOverOptionIndex);
            _gameOverTextLabels[i].Modulate = selected ? ColorMenuSelected : ColorMenuNormal;
            _gameOverArrows[i].Visible      = selected;
        }
    }

    /// <summary>
    /// Handles input while the Game Over overlay is active. Called from <see cref="_Input"/>
    /// when _state is BattleState.GameOver. No-op until AddGameOverOptions has populated the
    /// labels (brief window between _state transition and overlay construction).
    /// </summary>
    private void HandleGameOverInput(InputEvent @event)
    {
        if (_gameOverTextLabels == null) return;

        // Input buffer (set at ShowEndLabel entry for Game Over) — lets the emotional
        // beat land and drains held battle_confirm presses from the killing blow
        // before Retry/Quit become interactive. Matches the Victory screen's combined
        // 2.0s beat + 150ms held-input buffer (total 2150ms from end-label dispatch).
        if (Time.GetTicksMsec() < _gameOverInputUnlockedAtMsec) return;

        if (@event.IsActionPressed("ui_up") || @event.IsActionPressed("ui_down"))
        {
            int direction = @event.IsActionPressed("ui_up") ? -1 : 1;
            int count     = GameOverOptionLabels.Length;
            _gameOverOptionIndex = (_gameOverOptionIndex + direction + count) % count;
            RefreshGameOverOptions();
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed("battle_confirm"))
        {
            GetViewport().SetInputAsHandled();
            GD.Print($"[BattleTest] Game Over → {GameOverOptionLabels[_gameOverOptionIndex]}");
            switch (_gameOverOptionIndex)
            {
                case 0: FadeToBlackAndReload(); break;  // Retry
                case 1: GetTree().Quit();       break;  // Quit
            }
        }
    }

    // =========================================================================
    // Victory options panel — Retry / Close shown below the "Victory!" text
    // =========================================================================

    /// <summary>
    /// Mirrors <see cref="AddGameOverOptions"/> — builds identically-styled Retry/Close rows
    /// in the host VBox. Separate field set (<see cref="_victoryTextLabels"/>, <see cref="_victoryArrows"/>,
    /// <see cref="_victoryOptionIndex"/>) keeps the two end-screen panels independent even
    /// though they share a visual vocabulary.
    /// </summary>
    private void AddVictoryOptions(VBoxContainer host)
    {
        _victoryOptionIndex  = 0;
        _victoryTextLabels   = new Label[VictoryOptionLabels.Length];
        _victoryArrows       = new Label[VictoryOptionLabels.Length];

        const float RowWidth         = 320f;
        const float RowHeight        = 56f;
        const int   OptionFontSize   = 44;
        const int   ArrowFontSize    = 24;
        const float ArrowOffsetLeft  = 72f;
        const float ArrowWidth       = 24f;
        const float ArrowBoxHeight   = 32f;

        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 12);
        stack.MouseFilter = Control.MouseFilterEnum.Ignore;
        host.AddChild(stack);

        for (int i = 0; i < VictoryOptionLabels.Length; i++)
        {
            var row = new Control();
            row.CustomMinimumSize = new Vector2(RowWidth, RowHeight);
            row.MouseFilter       = Control.MouseFilterEnum.Ignore;
            stack.AddChild(row);

            var textLabel = new Label();
            textLabel.Text                = VictoryOptionLabels[i];
            textLabel.HorizontalAlignment = HorizontalAlignment.Center;
            textLabel.VerticalAlignment   = VerticalAlignment.Center;
            textLabel.AnchorLeft          = 0f;
            textLabel.AnchorRight         = 1f;
            textLabel.AnchorTop           = 0f;
            textLabel.AnchorBottom        = 1f;
            textLabel.MouseFilter         = Control.MouseFilterEnum.Ignore;
            StyleLabel(textLabel, fontSize: OptionFontSize);
            row.AddChild(textLabel);
            _victoryTextLabels[i] = textLabel;

            var arrow = new Label();
            arrow.Text                = "▶";
            arrow.HorizontalAlignment = HorizontalAlignment.Center;
            arrow.VerticalAlignment   = VerticalAlignment.Center;
            arrow.AnchorLeft          = 0f;
            arrow.AnchorRight         = 0f;
            arrow.AnchorTop           = 0.5f;
            arrow.AnchorBottom        = 0.5f;
            arrow.OffsetLeft          = ArrowOffsetLeft;
            arrow.OffsetRight         = ArrowOffsetLeft + ArrowWidth;
            arrow.OffsetTop           = -ArrowBoxHeight * 0.5f;
            arrow.OffsetBottom        =  ArrowBoxHeight * 0.5f;
            arrow.MouseFilter         = Control.MouseFilterEnum.Ignore;
            StyleLabel(arrow, fontSize: ArrowFontSize);
            arrow.Modulate            = ColorMenuSelected;
            row.AddChild(arrow);
            _victoryArrows[i] = arrow;
        }

        RefreshVictoryOptions();
    }

    /// <summary>
    /// Repaints the Retry/Close options based on <see cref="_victoryOptionIndex"/>.
    /// Mirrors <see cref="RefreshGameOverOptions"/> with the victory-specific field set.
    /// </summary>
    private void RefreshVictoryOptions()
    {
        if (_victoryTextLabels == null) return;
        for (int i = 0; i < _victoryTextLabels.Length; i++)
        {
            bool selected = (i == _victoryOptionIndex);
            _victoryTextLabels[i].Modulate = selected ? ColorMenuSelected : ColorMenuNormal;
            _victoryArrows[i].Visible      = selected;
        }
    }

    /// <summary>
    /// Handles input while the Victory overlay is active. 150ms input buffer from panel
    /// fade-in start prevents a held final-strike input from immediately selecting an option.
    /// Retry mirrors Game Over's Retry (FadeToBlackAndReload); Close mirrors Game Over's Quit
    /// (GetTree().Quit()).
    /// </summary>
    private void HandleVictoryInput(InputEvent @event)
    {
        if (_victoryTextLabels == null) return;
        if (Time.GetTicksMsec() < _victoryInputUnlockedAtMsec) return;

        if (@event.IsActionPressed("ui_up") || @event.IsActionPressed("ui_down"))
        {
            int direction = @event.IsActionPressed("ui_up") ? -1 : 1;
            int count     = VictoryOptionLabels.Length;
            _victoryOptionIndex = (_victoryOptionIndex + direction + count) % count;
            RefreshVictoryOptions();
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed("battle_confirm"))
        {
            GetViewport().SetInputAsHandled();
            GD.Print($"[BattleTest] Victory → {VictoryOptionLabels[_victoryOptionIndex]}");
            switch (_victoryOptionIndex)
            {
                case 0: FadeToBlackAndReload(); break;  // Retry
                case 1: GetTree().Quit();       break;  // Close
            }
        }
    }

    /// <summary>
    /// Builds the Game Over Retry/Quit panel and fades it in, then transitions state to
    /// BattleState.GameOver so HandleGameOverInput takes over. Called from ShowEndLabel's
    /// shared 2.0s timer after the "Game Over" fullscreen label appears. Mirror of
    /// <see cref="ShowVictoryOptionsPanel"/> — same layout, anchoring, spacers, tween;
    /// the two end-screens are structurally symmetric post-parity-refactor.
    ///
    /// Positioned below viewport center (+200px) so the "Game Over" label at center
    /// stays legible. 150ms input buffer set at fade-in start drains held battle_confirm
    /// presses from the killing blow. Combined with the 2.0s timer delay from
    /// ShowEndLabel, the cumulative input lockout is 2.15s — matching Victory exactly.
    /// </summary>
    private void ShowGameOverOptionsPanel()
    {
        if (!GodotObject.IsInstanceValid(this)) return;

        var layer = new CanvasLayer();
        layer.Name = "GameOverOptionsLayer";
        AddChild(layer);

        var wrapper = new Control();
        wrapper.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        wrapper.MouseFilter = Control.MouseFilterEnum.Ignore;
        wrapper.Modulate    = new Color(1f, 1f, 1f, 0f);
        layer.AddChild(wrapper);

        var panel = MakeLayeredPanel(minWidth: 400f, out var content);
        panel.AnchorLeft     = 0.5f;
        panel.AnchorRight    = 0.5f;
        panel.AnchorTop      = 0.5f;
        panel.AnchorBottom   = 0.5f;
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical   = Control.GrowDirection.Both;
        // Offset the panel down 100px from viewport center so it sits clearly below the
        // lifted "Game Over" title (title's OffsetBottom = -200f lifts its center 100px
        // above viewport middle; this 100f offset keeps the panel 100px below middle,
        // preserving the same title-to-panel spacing while the whole composition reads
        // higher on screen). Matches Victory's lifted offset.
        panel.OffsetTop      = 100f;
        panel.OffsetBottom   = 100f;
        wrapper.AddChild(panel);

        content.AddThemeConstantOverride("separation", 24);

        // 8px top/bottom spacers, options, no divider — same content layout as the
        // Victory options panel. The free-floating "Game Over" label above already
        // serves as the headline, so a divider inside would be decorative without purpose.
        var topSpacer = new Control();
        topSpacer.CustomMinimumSize = new Vector2(0, 8);
        content.AddChild(topSpacer);

        AddGameOverOptions(content);

        var bottomSpacer = new Control();
        bottomSpacer.CustomMinimumSize = new Vector2(0, 8);
        content.AddChild(bottomSpacer);

        _state                       = BattleState.GameOver;
        _gameOverInputUnlockedAtMsec = Time.GetTicksMsec() + 150;

        var tween = CreateTween();
        tween.TweenProperty(wrapper, "modulate:a", 1.0f, 0.5f);
    }

    /// <summary>
    /// Builds the Victory Close/Retry panel and fades it in, then transitions state to
    /// BattleState.Victory so HandleVictoryInput takes over. Called from ShowEndLabel's
    /// shared 2.0s timer after the "Victory!" fullscreen label appears.
    /// Positioned below viewport center (+200px) so the Victory! label at center stays legible.
    /// 150ms input buffer set at fade-in start prevents final-strike input bleed.
    /// </summary>
    private void ShowVictoryOptionsPanel()
    {
        if (!GodotObject.IsInstanceValid(this)) return;

        var layer = new CanvasLayer();
        layer.Name = "VictoryOptionsLayer";
        AddChild(layer);

        var wrapper = new Control();
        wrapper.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        wrapper.MouseFilter = Control.MouseFilterEnum.Ignore;
        wrapper.Modulate    = new Color(1f, 1f, 1f, 0f);
        layer.AddChild(wrapper);

        var panel = MakeLayeredPanel(minWidth: 400f, out var content);
        panel.AnchorLeft     = 0.5f;
        panel.AnchorRight    = 0.5f;
        panel.AnchorTop      = 0.5f;
        panel.AnchorBottom   = 0.5f;
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical   = Control.GrowDirection.Both;
        // Offset the panel down 100px from viewport center so it sits clearly below the
        // lifted "Victory!" title (title's OffsetBottom = -200f lifts its center 100px
        // above viewport middle; this 100f offset keeps the panel 100px below middle,
        // preserving the same title-to-panel spacing while the whole composition reads
        // higher on screen).
        panel.OffsetTop      = 100f;
        panel.OffsetBottom   = 100f;
        wrapper.AddChild(panel);

        content.AddThemeConstantOverride("separation", 24);

        var topSpacer = new Control();
        topSpacer.CustomMinimumSize = new Vector2(0, 8);
        content.AddChild(topSpacer);

        AddVictoryOptions(content);

        var bottomSpacer = new Control();
        bottomSpacer.CustomMinimumSize = new Vector2(0, 8);
        content.AddChild(bottomSpacer);

        _state                      = BattleState.Victory;
        _victoryInputUnlockedAtMsec = Time.GetTicksMsec() + 150;

        var tween = CreateTween();
        tween.TweenProperty(wrapper, "modulate:a", 1.0f, 0.5f);
    }

    /// <summary>
    /// Adds a full-screen black overlay on its own top-most CanvasLayer and tweens its
    /// alpha from 0 → 1 over 0.5s. On tween completion, calls ReloadCurrentScene() so the
    /// battle starts fresh with everything reset. _inputLocked is set immediately so the
    /// player can't retrigger the retry or navigate the options during the fade.
    /// </summary>
    private void FadeToBlackAndReload()
    {
        _inputLocked   = true;
        _reloadPending = true;

        var fadeLayer   = new CanvasLayer();
        fadeLayer.Name  = "GameOverFadeLayer";
        fadeLayer.Layer = 100;  // above every other CanvasLayer (status panels, menu, message, end overlay)
        AddChild(fadeLayer);

        var fadeRect = new ColorRect();
        fadeRect.Color       = new Color(0f, 0f, 0f, 1f);  // opaque black; Modulate drives the fade
        fadeRect.Modulate    = new Color(1f, 1f, 1f, 0f);  // start fully transparent
        fadeRect.MouseFilter = Control.MouseFilterEnum.Ignore;
        fadeRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        fadeRect.OffsetLeft   = 0f;
        fadeRect.OffsetTop    = 0f;
        fadeRect.OffsetRight  = 0f;
        fadeRect.OffsetBottom = 0f;
        fadeLayer.AddChild(fadeRect);

        var tween = CreateTween();
        tween.TweenProperty(fadeRect, "modulate:a", 1.0f, 0.5f);
        tween.TweenInterval(0.5f);  // hold fully black before reloading so the transition feels deliberate
        tween.TweenCallback(Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(this)) return;  // scene already disposed — skip double-reload
            GetTree().ReloadCurrentScene();
        }));
    }

    // =========================================================================
    // Turn-order queue dispatch
    // =========================================================================

    /// <summary>
    /// Advances the turn-order queue by one and dispatches to the appropriate
    /// turn entry point: <see cref="ShowMenu"/> for player turns,
    /// <see cref="BeginEnemyAttack"/> for enemy turns. Wins-out on the
    /// aggregate <see cref="CheckGameOver"/> predicate at the top — if the
    /// battle has ended, returns silently (the death-handler / mid-sequence
    /// path already triggered the end-screen).
    ///
    /// Tick-based AP scheduler (C7-prerequisite): <c>_queue.Advance</c>
    /// simulates ticks until a combatant crosses the AP threshold and returns
    /// that combatant directly. No round-exhaustion / rebuild dance — the
    /// tick model has no rounds. Defensive null check is the only end-of-
    /// queue case (would imply all combatants dead, which CheckGameOver
    /// catches first).
    ///
    /// All turn-transition call sites (post-action, post-Defend, post-magic,
    /// post-Beckon, etc.) call this method directly with no preceding
    /// <c>_queue.Advance()</c> — queue advancement is fully encapsulated here.
    ///
    /// Initial invocation: <see cref="_Ready"/> (skip-intro path) and
    /// <see cref="OnIntroDialogueCompleted"/>; both pre-Reset the queue.
    /// </summary>
    private void AdvanceTurn()
    {
        if (CheckGameOver()) return;

        // C7 follow-up: slide the previous turn's card off BEFORE Advance so
        // the strip's top card matches the actor whose turn is currently
        // resolving (rather than showing the next-next). On the very first
        // AdvanceTurn (no previous turn), _queue.Current is null — skip the
        // slide; the initial BuildTurnOrderStrip put first-to-act at slot 0,
        // which is already correct.
        //
        // After the slide kicks off (non-blocking), Advance returns the new
        // current actor. By the time the slide's post-callback rotates the
        // list (t = TurnOrderSlideDur ≈ 180ms), the new actor's card is at
        // slot 0 — matching what _queue.Advance returned. The slide
        // animation overlaps with the dispatch below (ShowMenu /
        // BeginEnemyAttack), which is fine — no input is gated on the
        // animation.
        if (_queue.Current != null)
            RefreshTurnOrderStrip(animate: true);

        var current = _queue.Advance();
        if (current == null)
        {
            GD.PrintErr("[BattleTest] AdvanceTurn: queue.Advance returned null — all combatants dead?");
            return;
        }

        if (current.Side == CombatantSide.Player)
        {
            _activePlayer = current;
            // Refresh status panels so the new active player's panel picks up the
            // PanelActiveModulate highlight (and the previous active player's panel
            // returns to PanelAliveModulate). UpdateHPBars iterates all panels and
            // calls ApplyDeadOrActiveStyling per panel — single source of truth for
            // panel-state transitions.
            UpdateHPBars();
            RefreshMenuHeader();
            if (_firstTurnAfterIntro)
            {
                _firstTurnAfterIntro = false;
                ShowMenuWithFadeIn(0.5f);
            }
            else
            {
                ShowMenu();
            }
        }
        else
        {
            BeginEnemyAttack(current);
        }
    }

    // =========================================================================
    // Enemy attack phase
    // =========================================================================

    private void BeginEnemyAttack(Combatant enemyAttacker)
    {
        // C5: the queue is the single source of turn flow; every AdvanceTurn call
        // site is explicit, so there's no event-driven double-fire risk that would
        // require a reentrancy guard here. The pre-C5 guard mistakenly tripped on
        // legitimate consecutive enemy turns (E1 → E2 in 4v5 rotation).
        _state               = BattleState.EnemyAttack;
        // Refresh panels so the previous player's PanelActiveModulate highlight
        // clears now that we're past the player-input phase. ApplyDeadOrActiveStyling
        // gates the highlight on _state being a player-input state — _state is set
        // above, so the very next refresh returns the panel to PanelAliveModulate.
        UpdateHPBars();
        _inputLocked         = false;  // Unlock input — enemy prompts are about to appear.
        _parryClean          = true;
        _isPlayerMagicAttack = false;
        _isComboAttack       = false;   // Clear stale combo flag from previous player turn.
        TimingPrompt.SuppressInput = false;  // safety reset

        // Hard boundary: free any surviving player-attack prompt so its signals cannot
        // fire into the enemy sequence. The prompt may still be alive if FreeActivePrompt's
        // flash-duration timer hasn't fired yet.
        FreeActivePrompt();
        GD.Print("[BattleTest] Enemy attacks.");

        // Resolve the defender first (read-only) so SelectEnemyAttack's
        // BeckoningTarget consumption doesn't clear the field before the
        // redirect scan runs. Both functions consult the same field for
        // the same enemyAttacker; SelectEnemyAttack is the single-clear
        // site, SelectEnemyTarget is read-only. Beckon redirect wins when
        // any player has BeckoningTarget == enemyAttacker; otherwise a
        // uniform-random pick from alive players. Threaded through to
        // ExecuteEnemyAttack so the threat-reveal flash and the actual
        // sequence agree on the defender.
        var playerDefender = SelectEnemyTarget(enemyAttacker);
        var selectedAttack = SelectEnemyAttack(enemyAttacker);

        // Signal the player when the enemy uses its learnable move (suppressed once absorbed).
        // Per-move-type absorb tracking: the signal is suppressed if THIS specific LearnableAttack
        // is already in _absorbedMoves (not if any move has been absorbed).
        if (EnemyData?.LearnableAttack != null
            && selectedAttack == EnemyData.LearnableAttack
            && !_absorbedMoves.Contains(EnemyData.LearnableAttack))
        {
            ShowLearnableSignal();
            FlashCombatantWhite(enemyAttacker);
        }

        // Phase 5 — threat reveal. Populated with the resolved defender post-
        // redirect so the red-tint flash lands on the correct combatant. AOE-
        // ready via the list so multi-target attacks can add more entries once
        // AttackData gains target-pool metadata.
        _threatenedCombatants.Clear();
        _threatenedCombatants.Add(playerDefender);
        foreach (var target in _threatenedCombatants)
            FlashCombatantThreatened(target);

        GetTree().CreateTimer(0.6f).Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            ExecuteEnemyAttack(enemyAttacker, playerDefender, selectedAttack);
        };
    }

    /// <summary>
    /// Second half of the enemy turn — runs 1.0s after BeginEnemyAttack, once the
    /// threat-reveal tint pulse has completed. Builds the SequenceContext and
    /// dispatches to the hop-in or cast path. Split out from BeginEnemyAttack so the
    /// pre-attack threat-reveal beat doesn't entangle with timer-callback plumbing
    /// on the hop-in branch's early-return.
    /// </summary>
    private void ExecuteEnemyAttack(Combatant enemyAttacker, Combatant playerDefender, AttackData selectedAttack)
    {

        // Build the sequence context once — the same reference threads through every
        // StepStarted / StepPassEvaluated / SequenceCompleted signal for this sequence.
        var ctx = new SequenceContext
        {
            Attacker      = enemyAttacker,
            Target        = playerDefender,
            CurrentAttack = selectedAttack,
            SequenceId    = _battleSystem.NextSequenceId(),
        };

        if (selectedAttack.IsHopIn)
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
            Vector2 hopInOffset = selectedAttack.Steps.Count > 0 ? selectedAttack.Steps[0].Offset : Vector2.Zero;
            PlayHopIn(enemyAttacker, playerDefender, () =>
            {
                Vector2 promptPos    = ComputeCameraMidpoint(enemyAttacker, playerDefender);
                _targetZone.Position = promptPos;
                _targetZone.Visible  = true;
                _battleSystem.StartSequence(this, ctx, promptPos);
            }, hopInOffset);
            return;
        }

        // Non-hop-in path (both SkipHopIn=true and SkipHopIn=false + non-hop-in attack):
        // enemy stays at origin and plays the cast arc. Hop-in only occurs for melee attacks.
        // _sequenceAttackerClosePos = origin so PlayTeardown is a zero-distance no-op.
        _sequenceAttacker         = enemyAttacker;
        _sequenceDefender         = playerDefender;
        _sequenceAttackerClosePos = enemyAttacker.Origin;

        // Start the cast animation and kick off the sequence immediately.
        // SafeDisconnect first — prevents stacking if BeginEnemyAttack fires more than once
        // (e.g. second turn) without the prior OnCastIntroFinished having run its own disconnect.
        SafeDisconnectAnim(enemyAttacker, OnCastIntroFinished);
        PlayAnim(enemyAttacker, "cast_intro");
        ConnectAnim(enemyAttacker, OnCastIntroFinished);

        Vector2 promptPosition = ComputeCameraMidpoint(enemyAttacker, playerDefender);
        _targetZone.Position   = promptPosition;
        _targetZone.Visible    = true;
        _battleSystem.StartSequence(this, ctx, promptPosition);
    }

    private void OnEnemyPassEvaluated(int result, int passIndex, int stepIndex)
    {
        var r = (TimingPrompt.InputResult)result;
        GD.Print($"[BattleTest] Enemy pass {passIndex + 1} resolved: {r}.");

        if (r == TimingPrompt.InputResult.Miss)
        {
            var player = _sequenceDefender;  // resolved defender — slot 0 at 1v1, random/redirected at multi-unit
            _parryClean   = false;
            int damage    = _battleSystem.GetStepBaseDamage(stepIndex);
            if (player.IsDefending) damage = Mathf.Max(1, damage / 2);
            player.TakeDamage(damage);
            GD.Print($"[BattleTest] Pass miss — player takes {damage} damage. Player HP: {player.CurrentHp}/{player.MaxHp}");
            PlaySound("player_hit.wav");
            SpawnDamageNumber(ComputeDamageOrigin(player), damage, DmgColorPlayer);
            UpdateHPBars();
            ShakeCamera(intensity: 8f, duration: 0.3f);  // shake — player takes a hit

            // Immediate death — play the animation now; the sequence continues silently.
            // SuppressInput (when aggregate wipe) blocks all further manual input and auto-miss
            // feedback on circles. Game Over label is deferred to OnEnemySequenceCompleted so
            // the player can watch the full attack pattern for future attempts.
            //
            // C3 multi-unit: this combatant really died — mark IsDead + play death anim
            // unconditionally. The Game Over overlay and SuppressInput only fire if the
            // aggregate CheckGameOver predicate confirms the whole party is down.
            if (player.CurrentHp <= 0 && !player.IsDead)
            {
                GD.Print("[BattleTest] Player HP reached zero mid-sequence.");
                player.IsDead = true;
                player.AnimSprite.Play("death");
                if (CheckGameOver())
                {
                    // _state transition deferred to ShowGameOverOptionsPanel (2.0s later,
                    // matching Victory's pattern). During the beat, player.IsDead and
                    // SuppressInput below already suppress combat-path input and damage;
                    // _state == EnemyAttack routes battle_confirm to TimingPrompt.ConfirmAll
                    // which no-ops under SuppressInput. HandleGameOverInput only routes once
                    // _state actually flips.
                    TimingPrompt.SuppressInput = true;
                    // Show Game Over overlay + fade music immediately; enemy sequence continues playing out.
                    // No OnPlayerDeathFinished is wired at this mid-sequence site, so this is the only
                    // place the overlay can be triggered.
                    ShowEndLabel("Game Over");
                }
            }
        }
    }

    /// <summary>
    /// BattleSystem.StepStarted — fires at the start of each step, before circles spawn.
    /// For hop-in melee attacks, plays the per-step enemy animation (e.g. melee_attack)
    /// with timing aligned so the impact frame lands when the first circle closes.
    /// </summary>
    private void OnBattleSystemStepStarted(int stepIndex, SequenceContext ctx)
    {
        if (!ctx.CurrentAttack.IsHopIn) return;
        var enemy = ctx.Attacker;
        if (enemy.IsDead) return;

        var step = ctx.CurrentAttack.Steps[stepIndex];
        if (string.IsNullOrEmpty(step.EnemyAnimation)) return;

        // Compute animation start delay so the impact frame lands when circle 0 closes.
        float circleCloseDuration = TimingPrompt.DefaultDurationForType(step.CircleType);
        float rawDelay = circleCloseDuration - step.ImpactFrames[0] / step.Fps;
        float animDelay = Mathf.Max(0f, rawDelay);

        void PlayStepAnimation()
        {
            if (enemy.IsDead) return;
            SafeDisconnectAnim(enemy, OnEnemyAttackAnimFinished);
            PlayAnim(enemy, step.EnemyAnimation);
            ConnectAnim(enemy, OnEnemyAttackAnimFinished);
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
    private void OnBouncingHopInPassEvaluated(int result, int passIndex, int stepIndex, SequenceContext ctx)
    {
        if (_bouncingHopInStep == null) return;
        var enemy = ctx.Attacker;
        if (enemy.IsDead) return;

        // Only replay if more inward passes follow.
        if (passIndex >= _bouncingHopInStep.BounceCount) return;

        var   step          = _bouncingHopInStep;
        float animDelay     = _bouncingHopInAnimDelay;
        float bounceDur     = 0.5f;  // matches TimingPrompt.BounceDuration default
        float replayDelay   = bounceDur + animDelay;

        GetTree().CreateTimer(replayDelay).Timeout += () =>
        {
            if (enemy.IsDead) return;
            SafeDisconnectAnim(enemy, OnEnemyAttackAnimFinished);
            PlayAnim(enemy, step.EnemyAnimation);
            ConnectAnim(enemy, OnEnemyAttackAnimFinished);
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
    private void OnBattleSystemStepPassEvaluated(int result, int passIndex, int stepIndex, SequenceContext ctx)
    {
        if (_isPlayerMagicAttack)
        {
            OnPlayerMagicPassEvaluated(result, passIndex, stepIndex);
            return;
        }

        // After the target's death, skip damage and animation reactions — circles
        // continue silently. ctx.Target is the resolved defender (slot 0 at 1v1,
        // random/redirected at multi-unit) — only their death should suppress further
        // hit reactions on this sequence.
        if (ctx.Target.IsDead) return;

        if (ctx.CurrentAttack.IsHopIn || !SkipHopIn)
            OnAttackPassEvaluated(result, passIndex);
        OnEnemyPassEvaluated(result, passIndex, stepIndex);

        // OWNER: OnBattleSystemStepPassEvaluated (enemy turn, per-pass reaction).
        // Pre-empt any in-flight retreat before taking ownership of the sprite.
        // If the backward run loop hasn't fired OnRetreatFinished yet, cancel it here so
        // it doesn't stomp the parry/hit animation or restore idle at the wrong moment.
        // SpeedScale must be reset regardless — it may still be 2 from the retreat.
        var defender = _sequenceDefender;  // player defends in enemy-attack sequences
        SafeDisconnectAnim(defender, OnRetreatFinished);
        defender.AnimSprite.SpeedScale = 1f;  // always reset — may still be 2 from retreat hop-back

        var r = (TimingPrompt.InputResult)result;
        if (r == TimingPrompt.InputResult.Hit || r == TimingPrompt.InputResult.Perfect)
        {
            // Successful block — restart parry animation from frame 0.
            // SafeDisconnect first so a parry already in flight doesn't stack a second
            // OnParryFinished connection on top of the existing one.
            // Stop() before Play() is required because Godot 4's AnimatedSprite2D.Play()
            // is a no-op when the requested animation is already playing — Stop() halts
            // the current playback so the subsequent Play("parry") always restarts fresh.
            SafeDisconnectAnim(defender, OnParryFinished);
            StopAnim(defender);
            PlaySound("parry_clash.wav");
            if (r == TimingPrompt.InputResult.Perfect)
                PlaySound("perfect_parry_instance.wav");
            PlayAnim(defender, "parry");  // OWNER: enemy pass, player defends — always restarts from frame 0
            ConnectAnim(defender, OnParryFinished);
        }
        else if (r == TimingPrompt.InputResult.Miss)
        {
            // Strike landed — flinch animation, then return to idle.
            SafeDisconnectAnim(defender, OnHitAnimFinished);
            PlayAnim(defender, "hit");    // OWNER: enemy pass, player takes damage — always restarts fresh
            ConnectAnim(defender, OnHitAnimFinished);
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
    private void OnSequenceCompleted(SequenceContext ctx)
    {
        if (_isPlayerMagicAttack)
            OnPlayerMagicSequenceCompleted();
        else
            OnEnemySequenceCompleted(ctx);
    }

    private void OnEnemySequenceCompleted(SequenceContext ctx)
    {
        GD.Print("[BattleTest] Enemy attack sequence complete.");
        _targetZone.Visible        = false;
        TimingPrompt.SuppressInput = false;

        bool isHopIn = ctx.CurrentAttack.IsHopIn;

        // Defender died mid-sequence — death animation is already playing.
        // Clean up the enemy and let the death flow finish. At multi-unit, a defender
        // dying doesn't imply aggregate game-over; the ShowEndLabel below is gated
        // by CheckGameOver, and the hop-in branch below gates via _hopInOver which is
        // itself sourced from CheckGameOver.
        if (_sequenceDefender.IsDead)
        {
            if (isHopIn)
            {
                // Hop-in path: let ProceedAfterHopInAnim handle teardown.
                UpdateHPBars();
                UnsubscribeBouncingHopIn();
                _hopInOver              = CheckGameOver();
                _hopInSequenceCompleted = true;
                if (_hopInAnimFinished)
                    ProceedAfterHopInAnim();
                return;
            }

            var enemyAttacker = _sequenceAttacker;
            if (HasCastEnd())
            {
                SafeDisconnectAnim(enemyAttacker, OnCastEndFinished);
                PlayAnim(enemyAttacker, "cast_end");
                ConnectAnim(enemyAttacker, OnCastEndFinished);
            }
            else
                PlayAnim(enemyAttacker, "idle");
            if (CheckGameOver())
            {
                ShowEndLabel("Game Over");
                return;
            }
            // C5 multi-unit: a defender dying doesn't end the round. Tear down the
            // enemy's pose and advance the turn queue so the next combatant gets to act.
            // (Pre-Phase-6, this branch could rely on game-over being the only defender-
            // death outcome; at 4v5 a single player death leaves the round still in flight.)
            PlayTeardown(() => GetTree().CreateTimer(0.5f).Timeout += AdvanceTurn);
            return;
        }

        if (isHopIn)
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
                TryTriggerAbsorption(ctx);
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

            var attacker = _sequenceAttacker;  // enemy — original sequence's attacker (no swap per C2 §3)
            var defender = _sequenceDefender;  // player
            if (!over)
            {
                if (!skipCastEnd)
                {
                    // Normal completion — transition enemy out of cast pose.
                    if (HasCastEnd())
                    {
                        SafeDisconnectAnim(attacker, OnCastEndFinished);
                        PlayAnim(attacker, "cast_end");
                        ConnectAnim(attacker, OnCastEndFinished);
                    }
                    else
                        PlayAnim(attacker, "idle");
                }
                PlayTeardown(() => GetTree().CreateTimer(0.5f).Timeout += AdvanceTurn);
                return;
            }

            // Game over — determine which side is dead and play the appropriate death animation.
            PlayTeardown(null);

            var player = defender;  // alias for readability below (defender == player in enemy sequences)
            var enemy  = attacker;

            if (player.CurrentHp <= 0)
            {
                if (!skipCastEnd)
                {
                    if (HasCastEnd())
                    {
                        SafeDisconnectAnim(attacker, OnCastEndFinished);
                        PlayAnim(attacker, "cast_end");
                        ConnectAnim(attacker, OnCastEndFinished);
                    }
                    else
                        PlayAnim(attacker, "idle");
                }
                player.IsDead = true;
                _sequenceDeathTarget = player;
                SafeDisconnectAnim(player, OnPlayerDeathFinished);
                player.AnimSprite.Play("death");
                ConnectAnim(player, OnPlayerDeathFinished);
                // Early overlay + music fade so the Game Over reads the moment the knight falls.
                // OnPlayerDeathFinished will still call ShowEndLabel at anim completion — no-ops via guard.
                ShowEndLabel("Game Over");
            }
            else // enemy.CurrentHp <= 0 — perfect parry counter killed the enemy
            {
                enemy.IsDead = true;
                PlaySound("enemy_defeat.mp3");
                _sequenceDeathTarget = enemy;
                SafeDisconnectAnim(enemy, OnEnemyDeathFinished);
                enemy.AnimSprite.Play("death");
                ConnectAnim(enemy, OnEnemyDeathFinished);
                ScheduleBossRevealIfPhase1();
                PlayAnim(defender, "idle");  // player returns to idle (defender == player pre-C2 §3 no-swap semantics)
            }
        }

        if (_parryClean)
        {
            TryTriggerAbsorption(ctx);
            PlayParryCounter(() => NonHopInContinuation(skipCastEnd: true));
        }
        else
            NonHopInContinuation(skipCastEnd: false);
    }

    // =========================================================================
    // Target selection (Phase 4)
    // =========================================================================
    // State machine glue between a menu pick and the attack launch. Menu handlers
    // in BattleMenu.cs build a launcher closure (captures attack-identity state +
    // MP deduction + the Begin* call) and hand off to EnterSelectingTarget with a
    // default target. The player confirms with battle_confirm (invokes the
    // launcher) or cancels with ui_cancel (restores the menu, no MP spent).
    //
    // Target cycling is stubbed — single-target today; scaffolding phase will
    // wire ui_left / ui_right to iterate valid targets once multi-enemy / party
    // selection density exists.

    private void EnterSelectingTarget(Combatant defaultTarget, MenuContext fromMenu)
    {
        _state                       = BattleState.SelectingTarget;
        _selectedTarget              = defaultTarget;
        _selectingTargetMenuContext  = fromMenu;

        // Auto-confirm when target is unambiguous. Today every attack has exactly
        // one valid target (offensive → single enemy, Cure → self); the pointer
        // appears when friendly-fire / ally-heal support introduces multi-target
        // pools. Until then, skip the confirmation ceremony — selecting a known
        // single target adds input friction without giving the player a choice.
        if (IsTargetPoolSingleton(defaultTarget))
        {
            ConfirmTargetSelection();
            return;
        }

        _targetPointer.SnapTo(defaultTarget);
        _targetPointer.Visible = true;
        GD.Print($"[BattleTest] Selecting target — default: {defaultTarget.Name}.");
    }

    /// <summary>
    /// True when the valid-target pool for the current attack contains exactly one
    /// combatant (i.e. <paramref name="defaultTarget"/> is the only choice). Stub
    /// today — every offensive attack targets the single enemy; Cure targets self;
    /// no attack yet allows ally-target or friendly-fire. When those land (post
    /// Phase 6, pending AttackData target-pool metadata), this checks the actual
    /// alive-combatants-on-the-appropriate-side count instead of hard-returning true.
    /// </summary>
    private bool IsTargetPoolSingleton(Combatant defaultTarget) => true;

    private void ConfirmTargetSelection()
    {
        _targetPointer.Visible       = false;
        _selectingTargetMenuContext  = MenuContext.Main;  // defensive clear; mirrors cancel's reset pattern
        var launcher = _pendingActionLauncher;
        _pendingActionLauncher = null;
        launcher?.Invoke();
        // C6 follow-up: refresh panels so the post-launcher state transition
        // (BeginPlayer*Attack sets _state = PlayerAttack) and any MP deduction
        // inside the launcher are reflected immediately, rather than lagging
        // until first-circle-resolve fires the next reactive refresh.
        //
        // Assumes synchronous launcher execution: all state mutations are
        // committed by the time launcher.Invoke() returns. Future launchers
        // that defer state changes (coroutines, timers) would need their own
        // post-mutation refresh.
        UpdateHPBars();
    }

    private void CancelTargetSelection()
    {
        _targetPointer.Visible = false;
        _selectedTarget         = null;
        _pendingActionLauncher  = null;
        // Defensive flag reset — stale attack-identity state shouldn't linger
        // between cancel and the next menu pick. Next pick will set these anyway,
        // but an explicit clear prevents future regression if a new code path
        // reads them in the interval.
        _isComboAttack          = false;
        _activeMagicAttack      = null;

        // Return to the menu context the target-select was entered from so the
        // player's prior navigation isn't lost. Three contexts today: main menu
        // (Basic Attack), Absorbed Moves submenu (Combo Strike, magic), Items
        // submenu (Ether). Flag reset to Main after dispatch so the next
        // SelectingTarget entry sets it fresh.
        //
        // ShowSubMenu / ShowItemMenu don't set _state / _inputLocked / _menuLayer
        // (they assume they're called from within the menu flow, not from
        // SelectingTarget). Set them explicitly for those branches; ShowMenu
        // handles all three for its branch.
        var fromMenu = _selectingTargetMenuContext;
        _selectingTargetMenuContext = MenuContext.Main;
        switch (fromMenu)
        {
            case MenuContext.Main:
                ShowMenu();
                break;
            case MenuContext.Skills:
                _state             = BattleState.PlayerMenu;
                _inputLocked       = false;
                _menuLayer.Visible = true;
                ShowSubMenu();
                break;
            case MenuContext.Items:
                _state             = BattleState.PlayerMenu;
                _inputLocked       = false;
                _menuLayer.Visible = true;
                ShowItemMenu();
                break;
        }
    }

    private void HandleSelectingTargetInput(InputEvent @event)
    {
        if (@event.IsActionPressed("battle_confirm"))
        {
            ConfirmTargetSelection();
            return;
        }
        if (@event.IsActionPressed("battle_cancel"))
        {
            CancelTargetSelection();
            return;
        }
        if (@event.IsActionPressed("ui_left") || @event.IsActionPressed("ui_right"))
        {
            // Stub: target cycling lands with the scaffolding phase once multiple
            // enemies/allies exist. Single-target today; no-op.
        }
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
        // Defender comes from SelectingTarget (Phase 4). Single-target today so the
        // fallback to _enemyParty[0] is equivalent; the fallback also covers any call
        // path that skips SelectingTarget (none today, but defensive).
        var defender = _selectedTarget ?? _enemyParty[0];
        BeginAttack(_activePlayer, defender, promptType, OnPlayerPromptCompleted);
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

        // Attack-identity predicate — answers "which target should this attack pick?"
        // Used at dispatch (target not yet chosen). Downstream sites inspect the chosen
        // pair via _sequenceAttacker.Side == _sequenceDefender.Side instead, which
        // generalizes to ally-target scenarios beyond the specific Cure-on-self case.
        bool isHealAttack = _activeMagicAttack == _playerCureAttack;

        GD.Print(isHealAttack
            ? "[BattleTest] Player uses Cure."
            : "[BattleTest] Player uses magic attack.");

        // Set sequence context so ComputeCameraMidpoint returns a sensible midpoint.
        // No hop-in — _sequenceAttackerClosePos = attacker origin so PlayTeardown is a
        // zero-distance no-op on the attacker. Target comes from SelectingTarget
        // (Phase 4); the ?? fallback uses the attack-identity check to keep behaviour
        // correct if any caller skips SelectingTarget (defensive — all current paths
        // route through the target-selection pipeline).
        var playerAttacker = _activePlayer;
        var magicDefender  = _selectedTarget ?? (isHealAttack ? playerAttacker : _enemyParty[0]);
        _sequenceAttacker         = playerAttacker;
        _sequenceDefender         = magicDefender;
        _sequenceAttackerClosePos = playerAttacker.Origin;

        // Play cast animation; defer StartSequence until it finishes so the wind-up
        // completes before the timing circle appears and the effect fires.
        // OnPlayerCastFinished recomputes the prompt position from the sequence
        // fields set above — no separate cached Vector2 needed.
        SafeDisconnectAnim(playerAttacker, OnPlayerCastFinished);
        PlayAnim(playerAttacker, "cast");  // OWNER: BeginPlayerMagicAttack — cast wind-up before sequence
        GetTree().CreateTimer(1f / 12f).Timeout += () => PlaySound("magic_launch_4.wav");  // frame 1 at 12fps
        ConnectAnim(playerAttacker, OnPlayerCastFinished);
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

        var enemy = _enemyParty[0];  // single target in the current UI
        enemy.TakeDamage(damage);
        GD.Print($"[BattleTest] Player deals {damage} damage. Enemy HP: {enemy.CurrentHp}/{enemy.MaxHp}");
        PlaySound("enemy_hit.wav");
        SpawnDamageNumber(ComputeDamageOrigin(enemy), damage, dmgColor);
        ShakeCamera(intensity: 8f, duration: 0.25f);  // shake — strike lands on enemy
        PlayCombatantHurtFlash(enemy);

        UpdateHPBars();
        _pendingGameOver = CheckGameOver();
        GetTree().CreateTimer(TimingPrompt.FlashDuration).Timeout += FreeActivePrompt;

        // Single attack: play the slash now that the circle has resolved.
        // combo_slash1 covers sheet frames 1–3; frame 0 was already shown as the wind-up.
        // PlayTeardown is deferred to OnFinalSlashFinished so the strike plays before retreat.
        PlaySound("player_attack_swing.wav");
        var playerAttacker = _sequenceAttacker;  // player (physical attack)
        SafeDisconnectAnim(playerAttacker, OnFinalSlashFinished);
        PlayAnim(playerAttacker, "combo_slash1");  // OWNER: player turn, single-hit slash on resolve
        ConnectAnim(playerAttacker, OnFinalSlashFinished);
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

        // Side-equality receiver predicate — true whenever the attacker and target
        // share a side (self-heal, ally-heal, self-damage, friendly-fire). Today only
        // Cure self-targets, so this reduces to "player casting heal on player," but
        // the predicate is friendly-fire-ready without further refactor.
        if (_sequenceAttacker.Side == _sequenceDefender.Side)
        {
            var player = _sequenceDefender;  // heal target (self for Cure; ally-heal in future)
            GD.Print($"[BattleTest] Cure pass {passIndex + 1} resolved: {r}  ({amount} HP).");
            player.Heal(amount);
            GD.Print($"[BattleTest] Cure heals {amount} HP. Player HP: {player.CurrentHp}/{player.MaxHp}");
            SpawnDamageNumber(ComputeDamageOrigin(player), amount, DmgColorPerfect);  // green for healing
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

        var enemy = _enemyParty[0];  // single target in the current UI
        enemy.TakeDamage(amount);
        GD.Print($"[BattleTest] Magic hit deals {amount} damage. Enemy HP: {enemy.CurrentHp}/{enemy.MaxHp}");
        PlaySound("enemy_hit.wav");
        SpawnDamageNumber(ComputeDamageOrigin(enemy), amount, dmgColor);
        ShakeCamera(intensity: 8f, duration: 0.25f);
        PlayCombatantHurtFlash(enemy);
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
        var playerAttacker = _sequenceAttacker;  // player (magic attacker)
        SafeDisconnectAnim(playerAttacker, OnPlayerCastTransitionFinished);
        PlayAnim(playerAttacker, "cast_transition");  // OWNER: OnPlayerMagicSequenceCompleted — exit cast pose
        ConnectAnim(playerAttacker, OnPlayerCastTransitionFinished);

        // Same-side attacker/target (self-heal today, ally-heal in future) — skip the
        // game-over check and proceed directly to the enemy turn. Sequence fields still
        // point at the just-completed sequence until the next StartSequence overwrites.
        if (_sequenceAttacker.Side == _sequenceDefender.Side)
        {
            GetTree().CreateTimer(0.5f).Timeout += AdvanceTurn;
            return;
        }

        bool over = CheckGameOver();
        if (!over)
        {
            // Mirror the physical attack flow: short pause then enemy takes their turn.
            // ShowMenu is intentionally skipped here — magic attacks transition directly
            // to BeginEnemyAttack, matching the behaviour after OnFinalSlashFinished.
            GetTree().CreateTimer(0.5f).Timeout += AdvanceTurn;
            return;
        }

        // Magic sequences: attacker = player, defender = target (usually enemy; self for Cure).
        var magicDefender = _sequenceDefender;
        var magicAttacker = _sequenceAttacker;
        if (magicDefender.CurrentHp <= 0 && magicDefender.Side == CombatantSide.Enemy)
        {
            magicDefender.IsDead = true;
            PlaySound("enemy_defeat.mp3");
            _sequenceDeathTarget = magicDefender;
            SafeDisconnectAnim(magicDefender, OnEnemyDeathFinished);
            magicDefender.AnimSprite.Play("death");
            ConnectAnim(magicDefender, OnEnemyDeathFinished);
            ScheduleBossRevealIfPhase1();
        }
        else
        {
            // Player died from a self-damage magic sequence (hypothetical; no current
            // attack does self-damage that can reach 0 HP, but the branch stays defensive).
            magicAttacker.IsDead = true;
            _sequenceDeathTarget = magicAttacker;
            SafeDisconnectAnim(magicAttacker, OnPlayerDeathFinished);
            magicAttacker.AnimSprite.Play("death");
            ConnectAnim(magicAttacker, OnPlayerDeathFinished);
            // Early overlay + music fade so the Game Over reads the moment the knight falls.
            // OnPlayerDeathFinished will still call ShowEndLabel at anim completion — no-ops via guard.
            ShowEndLabel("Game Over");
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
        var player = _activePlayer;  // item user — the rotating active player (slot 0 at 1v1)
        if (player.IsDead) { AdvanceTurn(); return; }
        _state       = BattleState.PlayerAttack;
        _inputLocked = true;  // block input during item use

        // Ether is a self-targeted pseudo-sequence (no BattleSystem circles). Set the
        // sequence-scoped fields so OnEtherAnimationFinished can route through them
        // instead of reaching into _playerParty[0] directly — the same pattern as
        // every other sequence-driven handler post-C2.
        _sequenceAttacker = player;
        _sequenceDefender = player;  // self-targeted item use

        // Play the item-use animation on the player.
        SafeDisconnectAnim(player, OnEtherAnimationFinished);
        PlayAnim(player, "item_use");
        ConnectAnim(player, OnEtherAnimationFinished);

        // Schedule effect + sound + MP restore at the impact frame of the combo animation.
        int   impactFrame = _playerEtherEffect?.Steps?[0]?.ImpactFrames?[0] ?? 7;
        float fps         = _playerEtherEffect?.Steps?[0]?.Fps ?? 12f;
        float impactDelay = impactFrame / fps;
        GetTree().CreateTimer(impactDelay).Timeout += () =>
        {
            if (player.IsDead) return;
            PlaySound("cure_spell.wav");
            RestoreMp(20);  // clamps to MaxMp and calls UpdateMpBar internally
            SpawnEtherEffect(player, _playerEtherEffect);
        };
    }

    private void OnEtherAnimationFinished()
    {
        var attacker = _sequenceAttacker;  // self-targeted; player is both attacker and defender
        SafeDisconnectAnim(attacker, OnEtherAnimationFinished);
        if (attacker.IsDead) return;
        PlayAnim(attacker, "idle");
        GetTree().CreateTimer(0.5f).Timeout += AdvanceTurn;
    }

    /// <summary>
    /// Spawns a one-shot visual effect sprite centered on <paramref name="user"/> using
    /// the first step of the given AttackData as the data source. Does not use
    /// BattleSystem — no circles. Target-agnostic — works for any Combatant that
    /// should be the visual anchor for the effect (item user today, any ally/self
    /// with an Ether-style buff in the future).
    /// </summary>
    private void SpawnEtherEffect(Combatant user, AttackData data)
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

        // Ether spawns on the supplied user. Frame anchor is the combatant's visual
        // center (Origin + PositionRect/2 — the same geometric-center formula used
        // across the refactor; carries the pre-existing ColorRect-vs-character-body
        // quirk noted elsewhere).
        Vector2 playerCenter = user.Origin + user.PositionRect.Size / 2f;
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
                var enemyAttacker = _sequenceAttacker;
                PlayTeardown(() =>
                {
                    PlayAnim(enemyAttacker, "idle");
                    GetTree().CreateTimer(0.5f).Timeout += AdvanceTurn;
                });
            }
            else
            {
                // Game over — retreat enemy without scheduling next turn, then handle death.
                PlayTeardown(null);

                // Roles in the hop-in enemy-attack sequence: attacker = enemy, defender = player.
                // No swap per C2 §3; parry counter that may have killed the enemy still sees
                // _sequenceAttacker = enemy / _sequenceDefender = player.
                var player = _sequenceDefender;
                var enemy  = _sequenceAttacker;

                if (player.CurrentHp <= 0 && !player.IsDead)
                {
                    player.IsDead = true;
                    _sequenceDeathTarget = player;
                    SafeDisconnectAnim(player, OnPlayerDeathFinished);
                    player.AnimSprite.Play("death");
                    ConnectAnim(player, OnPlayerDeathFinished);
                    // Early overlay + music fade so the Game Over reads the moment the knight falls.
                    // OnPlayerDeathFinished will still call ShowEndLabel at anim completion — no-ops via guard.
                    ShowEndLabel("Game Over");
                }
                else if (player.CurrentHp <= 0 && player.IsDead)
                {
                    // Death was triggered mid-sequence — animation already playing.
                    ShowEndLabel("Game Over");
                }
                else  // enemy.CurrentHp <= 0 — parry counter killed the enemy
                {
                    enemy.IsDead = true;
                    PlaySound("enemy_defeat.mp3");
                    _sequenceDeathTarget = enemy;
                    SafeDisconnectAnim(enemy, OnEnemyDeathFinished);
                    enemy.AnimSprite.Play("death");
                    ConnectAnim(enemy, OnEnemyDeathFinished);
                    ScheduleBossRevealIfPhase1();
                    PlayAnim(player, "idle");
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
    /// Returns the attack to use for the current enemy turn. The
    /// <paramref name="enemyAttacker"/> parameter scopes the Beckon force-learnable
    /// scan: only a player whose <c>BeckoningTarget</c> equals the attacking enemy
    /// triggers the redirect. Priority: LoopAttack+TestEnemyAttack (testing) >
    /// Beckon force-learnable > EnemyData.AttackPool > _enemyAttackData (fallback).
    /// </summary>
    private AttackData SelectEnemyAttack(Combatant enemyAttacker)
    {
        if (LoopAttack && TestEnemyAttack != null)
            return TestEnemyAttack;

        // Beckon force-learnable: scan all players for one whose BeckoningTarget
        // matches this attacking enemy. Consume the redirect on match. Single-clear
        // invariant — this is the consumption site; SelectEnemyTarget reads only.
        foreach (var p in _playerParty)
        {
            if (p.BeckoningTarget == enemyAttacker && !p.IsDead)
            {
                p.BeckoningTarget = null;
                if (EnemyData?.LearnableAttack != null)
                    return EnemyData.LearnableAttack;
                break;  // matched player but no learnable available; fall through to pool
            }
        }

        if (EnemyData != null && EnemyData.AttackPool != null && EnemyData.AttackPool.Length > 0)
        {
            var attack = AttackSelector.SelectAttack(EnemyData, ref _lastAttackIndex);
            if (attack != null)
                return attack;
        }

        return _enemyAttackData;
    }

    /// <summary>
    /// Resolves an enemy attacker's defender. Beckon redirect wins when any
    /// alive player has <c>BeckoningTarget == attacker</c>; otherwise a
    /// uniform-random pick from alive players (via <see cref="_rng"/>).
    /// Read-only — does not consume <c>BeckoningTarget</c>; consumption
    /// happens in <see cref="SelectEnemyAttack"/> when the redirected
    /// enemy commits to its learnable.
    /// </summary>
    private Combatant SelectEnemyTarget(Combatant attacker)
    {
        // Beckon redirect — first alive beckoner wins.
        foreach (var p in _playerParty)
        {
            if (p.BeckoningTarget == attacker && !p.IsDead)
                return p;
        }

        // Uniform random over alive players.
        var alive = new List<Combatant>();
        foreach (var p in _playerParty)
            if (!p.IsDead) alive.Add(p);
        if (alive.Count == 0)
        {
            GD.PrintErr("[BattleTest] SelectEnemyTarget: no alive players; defaulting to slot 0.");
            return _playerParty[0];
        }
        return alive[_rng.RandiRange(0, alive.Count - 1)];
    }

    public void ShowBattleMessage(string text) => _battleMessage.Show(text);

    /// <summary>
    /// Fire-and-forget one-shot sound playback from res://Assets/Audio/SFX/.
    /// Creates a temporary AudioStreamPlayer that frees itself when done.
    /// <paramref name="volumeDb"/> adjusts volume in decibels (0 = full, -6 ≈ 50%, -4 ≈ 60%).
    /// </summary>
    private void PlaySound(string filename, float volumeDb = 0f)
    {
        var stream = GD.Load<AudioStream>($"res://Assets/Audio/SFX/{filename}");
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
    /// Beckon ability — if the enemy has an unabsorbed learnable move, sets
    /// playerCombatant.BeckoningTarget so SelectEnemyAttack returns LearnableAttack this turn.
    /// Otherwise shows a brief message. Always hands off to the enemy turn immediately (no animation).
    /// </summary>
    /// <summary>
    /// Beckon ability — forces the enemy to use their LearnableAttack next turn.
    /// Selectability (MP, learnable present, not yet absorbed) is gated at the menu
    /// level by IsSubMenuOptionEnabled, so this method assumes all preconditions hold.
    /// </summary>
    private void PerformBeckon()
    {
        // Defense-in-depth — the menu only renders Beckon for the Absorber, but a
        // future code path that bypasses the menu would otherwise allow non-Absorbers
        // to beckon. Reject and log; no MP deducted, no enemy turn started.
        if (_activePlayer == null || !_activePlayer.IsAbsorber)
        {
            GD.PrintErr("[BattleTest] PerformBeckon called on non-Absorber — ignoring.");
            return;
        }
        const int beckonMpCost = 10;
        var player = _activePlayer;
        // Target picked by the Beckoner via SelectingTarget; falls back to
        // _enemyParty[0] defensively if the launcher fired without a target.
        var beckonTarget = _selectedTarget ?? _enemyParty[0];
        player.CurrentMp -= beckonMpCost;
        UpdateMpBar();
        player.BeckoningTarget = beckonTarget;
        GD.Print($"[BattleTest] Player beckons {beckonTarget.Name} (-{beckonMpCost} MP) — enemy will use learnable move next turn.");
        AdvanceTurn();
    }

    /// <summary>
    /// If the just-completed enemy attack was the learnable move and the player perfect-parried it,
    /// triggers the absorption moment (message + flash). No-ops if this move is already in
    /// <see cref="_absorbedMoves"/> — absorption is per-move-type, not per-enemy-instance.
    /// Called immediately before PlayParryCounter in OnEnemySequenceCompleted.
    /// </summary>
    private void TryTriggerAbsorption(SequenceContext ctx)
    {
        // Only the Absorber learns moves from a perfect parry. ctx.Target is the
        // parrying combatant (the player being attacked); the Side check is defensive
        // (this method is only called from enemy-sequence completion, so Target.Side
        // should always be Player) and the IsAbsorber check prevents non-Absorber
        // parries from accidentally enrolling moves into _absorbedMoves once C5's
        // queue lets non-Absorbers parry.
        if (ctx.Target.Side != CombatantSide.Player) return;
        if (!ctx.Target.IsAbsorber) return;

        var currentAttack = ctx.CurrentAttack;
        if (EnemyData?.LearnableAttack == null || currentAttack != EnemyData.LearnableAttack) return;
        if (_absorbedMoves.Contains(EnemyData.LearnableAttack)) return;

        _absorbedMoves.Add(EnemyData.LearnableAttack);
        PlaySound("absorbed_ability_acquired.wav", volumeDb: 6f);
        // TODO: when player state/character system is built, migrate _absorbedMoves to the
        // Absorber character's persistent skill storage (see Combatant design doc).

        RebuildSubMenu();

        ShowBattleMessage("I've got it.");
        GD.Print("[BattleTest] Absorbed learnable move!");
    }

    /// <summary>
    /// Flashes <paramref name="target"/>'s sprite white 3 times over ~0.6s using the
    /// <c>flash_amount</c> uniform on <c>CombatantOverlay.gdshader</c>. Reads / writes the
    /// flash material and tween on the Combatant so per-target flashes remain
    /// independently trackable at multi-combat density. Target-agnostic — works for any
    /// Combatant with a populated FlashMaterial.
    /// </summary>
    private void FlashCombatantWhite(Combatant target)
    {
        target.FlashTween?.Kill();
        target.FlashMaterial.SetShaderParameter("flash_amount", 0.0f);

        target.FlashTween = CreateTween();
        var material = target.FlashMaterial;
        for (int i = 0; i < 3; i++)
        {
            target.FlashTween.TweenMethod(
                Callable.From((float v) => material.SetShaderParameter("flash_amount", v)),
                0.0f, 1.0f, 0.1f);
            target.FlashTween.TweenMethod(
                Callable.From((float v) => material.SetShaderParameter("flash_amount", v)),
                1.0f, 0.0f, 0.1f);
        }
    }

    /// <summary>
    /// Plays a ~1s red-tint pulse on <paramref name="target"/>'s sprite to signal that
    /// an incoming enemy attack is about to hit this combatant (Phase 5 threat reveal).
    /// Tweens the <c>tint_amount</c> uniform on <c>CombatantOverlay.gdshader</c> —
    /// independent of <c>flash_amount</c>, so the white-flash and threat-reveal effects
    /// can run concurrently on the same sprite (rare today, more common post-Phase-6).
    /// Target-agnostic — works for any Combatant with a populated FlashMaterial.
    /// </summary>
    private void FlashCombatantThreatened(Combatant target)
    {
        if (target.IsDead) return;

        target.ThreatTween?.Kill();
        target.FlashMaterial.SetShaderParameter("tint_amount", 0.0f);

        // 0.12s fade in → 0.36s hold at 0.85 → 0.12s fade out. 0.6s total — matches
        // the BeginEnemyAttack pause duration so the tint fades out exactly as the
        // attack animation begins. Peak capped at 0.85 rather than 1.0 so some
        // original colour bleeds through even at peak hold, softening the tint's
        // aggressiveness while still reading clearly as a threat signal.
        target.ThreatTween = CreateTween();
        var material = target.FlashMaterial;
        target.ThreatTween.TweenMethod(
            Callable.From((float v) => material.SetShaderParameter("tint_amount", v)),
            0.0f, 0.85f, 0.12f);
        target.ThreatTween.TweenInterval(0.36f);
        target.ThreatTween.TweenMethod(
            Callable.From((float v) => material.SetShaderParameter("tint_amount", v)),
            0.85f, 0.0f, 0.12f);
    }

    // Party-wipe semantics (C3): Game Over fires when every player is at 0 HP; Victory
    // fires when every enemy is at 0 HP. At 1v1 (default PartySize=1) this is equivalent
    // to the pre-C3 slot-0 check. Callers that subsequently discriminate "which side won"
    // still pattern-match on slot-0 HP — correct at C3's intermediate state where only
    // slot 0 takes damage; C5's queue migration re-evaluates those discriminators when
    // multi-unit damage lands.
    private bool CheckGameOver()
    {
        // Returns whether the battle has ended. Callers drive downstream behaviour
        // (death animation, ShowEndLabel) off the bool. State transition to
        // BattleState.GameOver or .Victory is deferred to the respective
        // options-panel method (ShowGameOverOptionsPanel / ShowVictoryOptionsPanel)
        // where it fires after the 2.0s end-label beat — mirrors how Victory already
        // handled state timing before the parity refactor and fixes a pre-refactor
        // latent bug where the player-wins branch briefly set _state = GameOver on
        // the Victory path before ShowVictoryOptionsPanel corrected it.
        if (_playerParty.All(p => p.CurrentHp <= 0))
        {
            GD.Print("[BattleTest] Enemy wins.");
            return true;
        }
        if (_enemyParty.All(e => e.CurrentHp <= 0))
        {
            GD.Print("[BattleTest] Player wins.");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Modulate constants for the active-player highlight + dead-slot grayout. Applied
    /// to <see cref="PartyPanel.ModulateTarget"/> in <see cref="ApplyDeadOrActiveStyling"/>:
    /// dead branch dominates; active-player branch only fires for the player whose
    /// turn is currently in flight (gated on <c>_state in {PlayerMenu, SelectingTarget}</c>).
    /// </summary>
    private static readonly Color PanelAliveModulate  = Colors.White;                          // alive, not active
    private static readonly Color PanelActiveModulate = new(1.5f,  1.5f,  1.2f, 1.0f);         // active — bright white-warm boost (visible at a glance)
    private static readonly Color PanelDeadModulate   = new(0.5f,  0.5f,  0.5f, 0.6f);         // dead — desaturated + transparent

    /// <summary>
    /// Refreshes every status panel from its bound combatant. Iterates _playerPanels +
    /// _enemyPanels (built in BuildStatusPanels per-slot). Reads HP / MP / Name on each
    /// call — name re-read covers Phase 2 transition (slot-0 enemy combatant.Name is
    /// reassigned in SwapToPhase2 before this method is called). Active-player +
    /// dead-slot Modulate also refreshes on every call so the highlight tracks the
    /// queue advance and dead-state transitions.
    /// </summary>
    private void UpdateHPBars()
    {
        foreach (var pp in _playerPanels) RefreshPanel(pp);
        foreach (var pp in _enemyPanels)  RefreshPanel(pp);
    }

    /// <summary>
    /// Player-side MP-only refresh — kept as a separate entry-point for the call sites
    /// that mutate MP without HP (BeckonConfirm, RestoreMp). Iterates only the player
    /// panels; enemy panels carry no MP bar.
    /// </summary>
    private void UpdateMpBar()
    {
        foreach (var pp in _playerPanels) RefreshMp(pp);
    }

    /// <summary>
    /// Refreshes a single panel's HP bar + label, name label (re-read to catch Phase 2
    /// transitions and any future name change), MP bar (player panels only), and
    /// active-player / dead-slot Modulate styling. Bar width comes from the fill
    /// Control's existing Size.X — set at construction by BuildStyledBar from the
    /// barWidth parameter (player = BarWidth=232; enemy row = EnemyRowBarWidth=150).
    /// </summary>
    private void RefreshPanel(PartyPanel pp)
    {
        var c = pp.BoundCombatant;
        // Read the bar's authored width from its parent's CustomMinimumSize so the
        // refresh works for both 232-wide player bars and 150-wide enemy-row bars.
        float barWidth = pp.HpFill.GetParent<Control>().CustomMinimumSize.X;
        pp.HpFill.Size = new Vector2(barWidth * ((float)c.CurrentHp / c.MaxHp), pp.HpFill.Size.Y);
        pp.HpLabel.Text = $"{c.CurrentHp}/{c.MaxHp}";
        pp.NameLabel.Text = c.Name;
        if (pp.MpFill != null) RefreshMp(pp);
        ApplyDeadOrActiveStyling(pp);
    }

    private void RefreshMp(PartyPanel pp)
    {
        var c = pp.BoundCombatant;
        float barWidth = pp.MpFill.GetParent<Control>().CustomMinimumSize.X;
        pp.MpFill.Size = new Vector2(barWidth * ((float)c.CurrentMp / c.MaxMp), pp.MpFill.Size.Y);
        pp.MpLabel.Text = $"{c.CurrentMp}/{c.MaxMp}";
    }

    /// <summary>
    /// Applies dead-slot grayout or active-player highlight to <c>pp.ModulateTarget</c>.
    /// Mutually exclusive: dead branch dominates (the queue's <c>Advance</c> filters
    /// dead combatants, so active-player + dead is unreachable in steady state, but
    /// the dead branch wins defensively). Active-player branch fires only during the
    /// player input phases (<c>PlayerMenu</c> / <c>SelectingTarget</c>) so the highlight
    /// clears automatically when the enemy turn begins.
    /// </summary>
    private void ApplyDeadOrActiveStyling(PartyPanel pp)
    {
        if (pp.ModulateTarget == null) return;
        var c = pp.BoundCombatant;
        if (c.IsDead)
        {
            pp.ModulateTarget.Modulate = PanelDeadModulate;
            return;
        }
        bool playerInputPhase = _state == BattleState.PlayerMenu
                             || _state == BattleState.SelectingTarget;
        bool isActivePlayer   = playerInputPhase
                             && c.Side == CombatantSide.Player
                             && c == _activePlayer;
        pp.ModulateTarget.Modulate = isActivePlayer ? PanelActiveModulate : PanelAliveModulate;
    }

    private void RestoreMp(int amount)
    {
        var player = _activePlayer;  // MP recipient — the active player (item user, etc.)
        player.CurrentMp = Mathf.Min(player.CurrentMp + amount, player.MaxMp);
        UpdateMpBar();
    }

    // =========================================================================
    // Status panels — enemy (top-right) and player party (bottom-left)
    // Kenney Fantasy UI borders + RPG-expansion 3-part bars
    // =========================================================================

    // Bars — status panel width minus content padding (260 - 2*14 = 232).
    private const float BarWidth          = 232f;
    private const float BarHeight         = 20f;
    private const float BarCapDisplayWidth = 8f;  // width each end-cap occupies inside the bar

    // Panel layout constants (swap panel numbers here to retune the whole UI).
    // Kenney fantasy-ui-borders panels are 48×48. PatchMargin=16 gives 16×16 corner regions
    // and a 16-wide stretchable center — standard for this pack.
    internal const int   PanelPatchMargin      = 16;
    internal const int   PanelContentPad       = 18;  // inner padding between border art and content
    internal const float PanelMinWidthStatus   = 260f;  // player panel (kept at 260)
    internal const float PanelMinWidthEnemy    = 220f;  // enemy panel (smaller — less content)
    internal const float PanelMinWidthMenu     = 180f;  // battle menu panels
    internal const float PanelMinWidthMessage  = 400f;  // battle message panel

    // Fill layer tint — applied as Modulate on a NinePatchRect of panel-transparent-center-000.png.
    // RGB-only; do not manipulate alpha (leave at 1.0) — the Kenney texture carries its own
    // alpha at 0x7f (~50%) via its tRNS chunk, which we keep so the gradient shows through.
    internal static readonly Color PanelFillTint = new Color(0.22f, 0.28f, 0.45f, 1.00f);

    internal const float UiEdgeMargin   = 20f;  // distance from viewport edge for status/menu panels
    internal const float UiPanelSpacing = 8f;   // gap between menu panel and player panel

    // Minimum bottom-inset for any UI overlay (BattleMessage / BattleDialogue / future
    // narrative bubbles) that needs to clear the player panel strip at the bottom.
    // = UiEdgeMargin (20) + PlayerPanelHeight (104) + UiPanelSpacing (8) + breathing
    // room (12) = 144. The +12 leaves visible whitespace between the strip's top edge
    // and the overlay bottom rather than touching at the seam. Shared across
    // BattleMessage and BattleDialogue so a single change flows to all overlay sites.
    internal const float OverlayBottomInset = 144f;

    // Panel texture picks — const strings so a single swap retunes the whole UI.
    internal const string UiPanelFillPath    = "res://Assets/UI/kenney_fantasy-ui-borders/PNG/Default/Transparent center/panel-transparent-center-000.png";
    internal const string UiPanelBorderPath  = "res://Assets/UI/kenney_fantasy-ui-borders/PNG/Default/Border/panel-border-019.png";
    internal const string UiPanelMessagePath = "res://Assets/UI/kenney_fantasy-ui-borders/PNG/Default/Transparent border/panel-transparent-border-000.png";
    internal const string UiDividerPath      = "res://Assets/UI/kenney_fantasy-ui-borders/PNG/Default/Divider Fade/divider-fade-000.png";

    // Text — white with subtle drop shadow; every status/menu Label passes through StyleLabel.
    internal const int   UiFontSize            = 14;
    internal const float UiFontShadowAlpha     = 0.7f;
    internal const int   UiFontShadowOffset    = 1;

    // Background gradient shader.
    internal const string UiBackgroundShaderPath = "res://Assets/Shaders/UiBackgroundGradient.gdshader";

    private const string UiBarBackLeftPath  = "res://Assets/UI/kenney_ui-pack-rpg-expansion/PNG/barBack_horizontalLeft.png";
    private const string UiBarBackMidPath   = "res://Assets/UI/kenney_ui-pack-rpg-expansion/PNG/barBack_horizontalMid.png";
    private const string UiBarBackRightPath = "res://Assets/UI/kenney_ui-pack-rpg-expansion/PNG/barBack_horizontalRight.png";
    private const string UiBarRedLeftPath   = "res://Assets/UI/kenney_ui-pack-rpg-expansion/PNG/barRed_horizontalLeft.png";
    private const string UiBarRedMidPath    = "res://Assets/UI/kenney_ui-pack-rpg-expansion/PNG/barRed_horizontalMid.png";
    private const string UiBarRedRightPath  = "res://Assets/UI/kenney_ui-pack-rpg-expansion/PNG/barRed_horizontalRight.png";
    // Note: the blue bar pack names its mid piece `barBlue_horizontalBlue.png` rather than
    // the `barBlue_horizontalMid.png` convention the other bars use.
    private const string UiBarBlueLeftPath  = "res://Assets/UI/kenney_ui-pack-rpg-expansion/PNG/barBlue_horizontalLeft.png";
    private const string UiBarBlueMidPath   = "res://Assets/UI/kenney_ui-pack-rpg-expansion/PNG/barBlue_horizontalBlue.png";
    private const string UiBarBlueRightPath = "res://Assets/UI/kenney_ui-pack-rpg-expansion/PNG/barBlue_horizontalRight.png";

    // Per-slot column width + gap for the count-aware centered player strip at the
    // bottom of the screen. The strip's left edge is recomputed each construction
    // pass from PlayerPartySize so the group stays centered for any party size.
    // 1920×1080 logical viewport (project.godot:20-21).
    private const float PlayerPanelWidth = PanelMinWidthStatus;  // 260f — player panel column width
    private const float PanelStackGap    =   8f;                 // horizontal gap between adjacent panels
    private const float ViewportWidth    = 1920f;                // matches project.godot viewport_width

    // Combined enemy panel constants — Octopath-style single panel containing one
    // compact row per enemy. Bar shrinks from BarWidth (232) to EnemyRowBarWidth
    // (150) so name + bar + HP text fit inline within the row HBox.
    private const float EnemyCombinedPanelMinWidth = 320f;       // wider than per-slot 220 to fit row HBox
    private const float EnemyRowNameWidth          =  90f;       // fixed name-label width so bars line up across rows
    private const float EnemyRowBarWidth           = 150f;       // compact HP bar (vs player 232)

    // C7 turn-order strip constants. Vertical column at top-left, 8 cards
    // visible at all times (full Lookahead width). Cards are compact (36 px
    // tall) so 8 stacked + 7 gaps fit in 330 px from y=20. "Warrior 5" still
    // fits at fontSize 13. Slide animation runs at 0.18s — JRPG-snappy;
    // tunable in interactive review. Side-coded fill tints mirror PanelFillTint
    // navy at similar brightness so player and enemy cards have equal visual
    // weight, only hue differs.
    private const int   LookaheadCount         = 8;
    private const float TurnOrderCardWidth     = 130f;
    private const float TurnOrderCardHeight    =  36f;
    private const float TurnOrderCardGap       =   6f;
    private const float TurnOrderSlideDur      =   0.18f;        // slide-out / slide-up / slide-in animation duration
    private const float TurnOrderSlideOffset   = 200f;           // slide-out distance to the right
    internal static readonly Color PlayerCardFillTint = new Color(0.22f, 0.28f, 0.65f, 1.0f);  // cool blue
    internal static readonly Color EnemyCardFillTint  = new Color(0.55f, 0.28f, 0.22f, 1.0f);  // warm burgundy

    /// <summary>
    /// Applies the subtle vertical navy gradient shader to the existing Background ColorRect
    /// defined in BattleTest.tscn. The ColorRect's own `color` property still contributes —
    /// the shader overrides it by assigning directly to COLOR.
    /// </summary>
    private void ApplyBackgroundGradient()
    {
        var bg = GetNodeOrNull<ColorRect>("Background");
        if (bg == null) return;

        var shader = GD.Load<Shader>(UiBackgroundShaderPath);
        if (shader == null) { GD.PrintErr("[BattleTest] Failed to load background gradient shader."); return; }

        var material = new ShaderMaterial();
        material.Shader = shader;
        bg.Material     = material;
    }

    /// <summary>
    /// Builds a layered Kenney 9-slice panel: a semi-transparent fill NinePatchRect with
    /// an ornate border NinePatchRect overlaid on top, both anchored full-rect inside a
    /// PanelContainer that auto-sizes from a MarginContainer's content.
    ///
    /// Returns the outer PanelContainer (for anchoring/positioning) and the VBoxContainer
    /// where content should be added.
    /// </summary>
    internal static PanelContainer MakeLayeredPanel(float minWidth, out VBoxContainer content, float minHeight = 0f, Color? fillTint = null, int? contentPad = null)
    {
        var panel = new PanelContainer();
        panel.CustomMinimumSize = new Vector2(minWidth, minHeight);
        // StyleBoxEmpty with no content margin — the MarginContainer inside provides padding,
        // and the NinePatchRect backgrounds fill the entire panel rect.
        var empty = new StyleBoxEmpty();
        panel.AddThemeStyleboxOverride("panel", empty);

        // Fill layer: NinePatchRect of the transparent-center texture. Tinted by
        // PanelFillTint by default (navy); callers can pass fillTint to override
        // — used by C7 turn-order cards for side-coded blue / burgundy.
        var fill = MakeNinePatch(UiPanelFillPath);
        fill.Modulate = fillTint ?? PanelFillTint;

        var border = MakeNinePatch(UiPanelBorderPath);

        // Add order IS draw order — fill underneath, border on top.
        panel.AddChild(fill);
        panel.AddChild(border);

        // contentPad defaults to PanelContentPad (18) for all chrome panels.
        // Callers needing a smaller card (C7 turn-order strip) can override
        // to shrink vertical content height, since PanelContainer treats
        // CustomMinimumSize as a floor — it auto-sizes to fit MarginContainer
        // + content. Smaller contentPad → smaller min content height → the
        // panel can actually honor a smaller minHeight.
        int pad = contentPad ?? PanelContentPad;
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left",   pad);
        margin.AddThemeConstantOverride("margin_right",  pad);
        margin.AddThemeConstantOverride("margin_top",    pad);
        margin.AddThemeConstantOverride("margin_bottom", pad);
        panel.AddChild(margin);

        content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 4);
        margin.AddChild(content);

        return panel;
    }

    /// <summary>
    /// Creates a NinePatchRect with the standard panel patch margins and mouse-ignore filter.
    /// Used as the fill and border layers of <see cref="MakeLayeredPanel"/>.
    ///
    /// Full-rect anchors (0,0,1,1) are set so that when this NinePatchRect is a child of a
    /// Container (e.g. PanelContainer), `fit_child_in_rect` stretches it to fill the content
    /// rect. Without anchors, fit_child_in_rect would size the NinePatchRect to its own
    /// `size` property (default 0,0) — it would render invisibly.
    ///
    /// Callers outside a Container (e.g. BattleMessage with manual anchor/offset positioning)
    /// will overwrite these anchors explicitly.
    /// </summary>
    internal static NinePatchRect MakeNinePatch(string texturePath)
    {
        var np = new NinePatchRect();
        np.Texture            = GD.Load<Texture2D>(texturePath);
        np.PatchMarginLeft    = PanelPatchMargin;
        np.PatchMarginRight   = PanelPatchMargin;
        np.PatchMarginTop     = PanelPatchMargin;
        np.PatchMarginBottom  = PanelPatchMargin;
        np.MouseFilter        = Control.MouseFilterEnum.Ignore;
        np.AnchorLeft         = 0f;
        np.AnchorRight        = 1f;
        np.AnchorTop          = 0f;
        np.AnchorBottom       = 1f;
        return np;
    }

    /// <summary>
    /// Applies the standard battle-UI text styling: white body, subtle dark drop shadow,
    /// consistent font size. All status/menu labels should route through this.
    /// </summary>
    internal static void StyleLabel(Label label, int fontSize = UiFontSize)
    {
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 1f));
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, UiFontShadowAlpha));
        label.AddThemeConstantOverride("shadow_offset_x", UiFontShadowOffset);
        label.AddThemeConstantOverride("shadow_offset_y", UiFontShadowOffset);
    }

    // Modulate tint applied to HP-fill Controls. Kenney's `barRed` asset reads warm/orange —
    // multiplying green and blue by 0.30 shifts it to a saturated deep red while preserving the
    // texture's highlights. Blue/back bars use Color(1,1,1,1) = no tint.
    private static readonly Color HpFillTint = new Color(1.00f, 0.30f, 0.30f, 1.00f);

    /// <summary>
    /// Builds an HP/MP bar with a 3-part Kenney background and a 3-part Kenney fill overlay.
    /// The returned fill Control is resized by <see cref="UpdateHPBars"/> / <see cref="UpdateMpBar"/>
    /// via `.Size = new Vector2(BarWidth * pct, ...)`. ClipContents hides the portion of the fill's
    /// 3-part texture that extends past the fill's current width — producing a drain-from-right look
    /// that keeps the left cap visible at any HP%.
    ///
    /// <paramref name="fillTint"/> is applied as a Modulate on the fill Control (propagates to
    /// all 3 children). Used to shift Kenney's warm `barRed` toward a truer red.
    /// </summary>
    private void BuildStyledBar(Control parent,
                                string backLeft, string backMid, string backRight,
                                string fillLeft, string fillMid, string fillRight,
                                out Control fillControl, out Label overlayLabel,
                                Color? fillTint = null,
                                float  barWidth = BarWidth)
    {
        // Background (always full width, never resized).
        AddThreePartBarChildren(parent, backLeft, backMid, backRight, barWidth);

        // Fill — resized by HP/MP percentage. ClipContents crops its children when it shrinks.
        fillControl = new Control();
        fillControl.Position     = Vector2.Zero;
        fillControl.Size         = new Vector2(barWidth, BarHeight);
        fillControl.ClipContents = true;
        if (fillTint.HasValue) fillControl.Modulate = fillTint.Value;
        parent.AddChild(fillControl);
        AddThreePartBarChildren(fillControl, fillLeft, fillMid, fillRight, barWidth);

        // Numeric overlay label centered over the full bar.
        overlayLabel                     = new Label();
        overlayLabel.Size                = new Vector2(barWidth, BarHeight);
        overlayLabel.HorizontalAlignment = HorizontalAlignment.Center;
        overlayLabel.VerticalAlignment   = VerticalAlignment.Center;
        StyleLabel(overlayLabel);
        parent.AddChild(overlayLabel);
    }

    /// <summary>
    /// Adds three TextureRects (left cap, stretched mid, right cap) at absolute positions
    /// sized to span the full BarWidth × BarHeight. Children use Position/Size — not anchors —
    /// so they stay at fixed pixel locations even when the parent resizes (ClipContents handles crop).
    /// </summary>
    private void AddThreePartBarChildren(Control parent,
                                         string leftPath, string midPath, string rightPath,
                                         float  barWidth = BarWidth)
    {
        var left = new TextureRect();
        left.Texture     = GD.Load<Texture2D>(leftPath);
        left.Position    = Vector2.Zero;
        left.Size        = new Vector2(BarCapDisplayWidth, BarHeight);
        left.StretchMode = TextureRect.StretchModeEnum.Scale;
        parent.AddChild(left);

        var mid = new TextureRect();
        mid.Texture     = GD.Load<Texture2D>(midPath);
        mid.Position    = new Vector2(BarCapDisplayWidth, 0f);
        mid.Size        = new Vector2(barWidth - 2f * BarCapDisplayWidth, BarHeight);
        mid.StretchMode = TextureRect.StretchModeEnum.Scale;
        parent.AddChild(mid);

        var right = new TextureRect();
        right.Texture     = GD.Load<Texture2D>(rightPath);
        right.Position    = new Vector2(barWidth - BarCapDisplayWidth, 0f);
        right.Size        = new Vector2(BarCapDisplayWidth, BarHeight);
        right.StretchMode = TextureRect.StretchModeEnum.Scale;
        parent.AddChild(right);
    }

    /// <summary>
    /// Constructs the combined enemy panel (one shared PanelContainer with one row per
    /// combatant) and a count-aware centered row of player panels at the bottom of the
    /// screen. At 1v1 the player strip centers a single panel mid-bottom — visual diff
    /// from the pre-C6 bottom-left anchor (acknowledged design change).
    ///
    /// Must run AFTER <see cref="BuildInitialParties"/> populates _playerParty /
    /// _enemyParty (panels bind to existing combatants), and BEFORE the first
    /// <see cref="UpdateHPBars"/> call (which iterates the panel lists).
    /// </summary>
    private void BuildStatusPanels()
    {
        var layer = new CanvasLayer();
        layer.Name = "StatusPanels";
        AddChild(layer);

        BuildEnemyCombinedPanel(layer);

        // Centered player strip: total width = N panels + (N-1) gaps; left edge =
        // (viewport - total) / 2 places the group symmetrically.
        int   n          = _playerParty.Count;
        float stripWidth = n * PlayerPanelWidth + (n - 1) * PanelStackGap;
        float stripLeft  = (ViewportWidth - stripWidth) * 0.5f;
        for (int i = 0; i < n; i++)
            _playerPanels.Add(BuildPlayerPanelForSlot(layer, _playerParty[i], i, stripLeft));
    }

    // Horizontal spacing between successive combatant slots at C3. Placeholder formation;
    // C6 replaces this with a proper staggered 2-2 / 3-2 layout. Players cluster left of
    // slot 0 (X decreasing); enemies cluster right of slot 0 (X increasing). At
    // TestFullParty (4v5), slots 3–4 on the enemy side clip off the 1920-wide viewport —
    // acceptable scaffolding until C6 lands.
    private const float PlayerSlotSpacing = 140f;
    private const float EnemySlotSpacing  = 160f;

    /// <summary>
    /// Constructs party lists that back the Combatant abstraction. Loops
    /// <see cref="PlayerPartySize"/> / <see cref="EnemyPartySize"/> so defaults of 1 / 1
    /// preserve the pre-Phase-6 1v1 scene exactly; larger sizes (e.g. TestFullParty's 4/5)
    /// spawn additional <c>ColorRect</c> + <c>AnimatedSprite2D</c> pairs for slots > 0.
    /// Called from <see cref="_Ready"/> after sprites / BattleSystem / Phase2EnemyData are
    /// all settled, but before the EnemyData HP init and test-flag overrides — those blocks
    /// write directly into the slot-0 Combatant fields produced here.
    ///
    /// Origin note: each Combatant carries two origin snapshots — <c>Origin</c> (the
    /// ColorRect position, read by <c>ComputeClosePosition</c> / <c>ComputeSlamPosition</c>
    /// / <c>ComputeCameraMidpoint</c>) and <c>AnimSpriteOrigin</c> (the AnimatedSprite2D
    /// position after floor-anchor + per-slot offset, read by PlayHopIn / PlayTeardown
    /// for the AnimSprite tween destination). Distinct because the two formulas differ
    /// per side; per-slot so multi-unit retreats land each slot back at its own origin
    /// instead of slot 0's.
    /// </summary>
    private void BuildInitialParties(Shader overlayShader)
    {
        int enemyInitialMaxHp = (EnemyData != null && EnemyData.MaxHp > 0) ? EnemyData.MaxHp : 200;

        for (int i = 0; i < PlayerPartySize; i++)
        {
            ColorRect        rect;
            AnimatedSprite2D sprite;
            if (i == 0)
            {
                // Slot 0 reuses the tscn-placed pair — SpriteFrames / Scale / Material are
                // already populated by _Ready before BuildInitialParties is invoked.
                rect   = _playerSprite;
                sprite = _playerAnimSprite;
            }
            else
            {
                (rect, sprite) = SpawnPlayerSlot(i, overlayShader);
            }
            _playerParty.Add(BuildPlayerCombatantForSlot(i, rect, sprite));
        }

        for (int i = 0; i < EnemyPartySize; i++)
        {
            ColorRect        rect;
            AnimatedSprite2D sprite;
            if (i == 0)
            {
                rect   = _enemySprite;
                sprite = _enemyAnimSprite;
            }
            else
            {
                (rect, sprite) = SpawnEnemySlot(i, overlayShader);
            }
            _enemyParty.Add(BuildEnemyCombatantForSlot(i, rect, sprite, enemyInitialMaxHp));
        }

        GD.Print($"[BattleTest] Parties built — " +
                 $"player: {_playerParty.Count} combatant(s) (slot 0 HP={_playerParty[0].CurrentHp}/{_playerParty[0].MaxHp}, " +
                 $"MP={_playerParty[0].CurrentMp}/{_playerParty[0].MaxMp}); " +
                 $"enemy: {_enemyParty.Count} combatant(s) (slot 0 HP={_enemyParty[0].CurrentHp}/{_enemyParty[0].MaxHp}).");
    }

    /// <summary>
    /// Spawns the <c>ColorRect</c> + <c>AnimatedSprite2D</c> pair for player slot
    /// <paramref name="slotIndex"/> (must be > 0 — slot 0 is the tscn-placed pair).
    /// SpriteFrames is shared with slot 0 (Godot Resource, safe to reference from
    /// multiple AnimatedSprite2D nodes). A fresh ShaderMaterial instance is created so
    /// each combatant's flash/tint uniforms are independent.
    /// Returns the (rect, sprite) pair ready for <see cref="BuildPlayerCombatantForSlot"/>.
    /// </summary>
    private (ColorRect rect, AnimatedSprite2D sprite) SpawnPlayerSlot(int slotIndex,
                                                                      Shader overlayShader)
    {
        // ColorRect — same size/color/visibility as slot 0; X shifted left by slot index.
        var rect = new ColorRect();
        rect.Size     = _playerSprite.Size;
        rect.Color    = _playerSprite.Color;
        rect.Position = new Vector2(_playerSprite.Position.X - slotIndex * PlayerSlotSpacing,
                                     _playerSprite.Position.Y);
        rect.Visible  = false;  // debug-only anchor, same as slot 0
        AddChild(rect);

        // AnimatedSprite2D — shares slot 0's SpriteFrames, own overlay material.
        var sprite = new AnimatedSprite2D();
        sprite.SpriteFrames = _playerAnimSprite.SpriteFrames;
        sprite.Scale        = _playerAnimSprite.Scale;
        sprite.Centered     = _playerAnimSprite.Centered;
        sprite.Position     = new Vector2(_playerAnimSprite.Position.X - slotIndex * PlayerSlotSpacing,
                                           _playerAnimSprite.Position.Y);
        sprite.Material     = CreateCombatantOverlayMaterial(overlayShader);
        AddChild(sprite);
        sprite.Play("idle");

        return (rect, sprite);
    }

    /// <summary>
    /// Spawns the <c>ColorRect</c> + <c>AnimatedSprite2D</c> pair for enemy slot
    /// <paramref name="slotIndex"/> (must be > 0). Mirrors <see cref="SpawnPlayerSlot"/>
    /// with enemy-side X shift (rightward), FlipH copied from slot 0, and the shared
    /// enemy SpriteFrames.
    /// </summary>
    private (ColorRect rect, AnimatedSprite2D sprite) SpawnEnemySlot(int slotIndex,
                                                                     Shader overlayShader)
    {
        var rect = new ColorRect();
        rect.Size     = _enemySprite.Size;
        rect.Color    = _enemySprite.Color;
        rect.Position = new Vector2(_enemySprite.Position.X + slotIndex * EnemySlotSpacing,
                                     _enemySprite.Position.Y);
        rect.Visible  = false;
        AddChild(rect);

        var sprite = new AnimatedSprite2D();
        sprite.SpriteFrames = _enemyAnimSprite.SpriteFrames;
        sprite.Scale        = _enemyAnimSprite.Scale;
        sprite.Centered     = _enemyAnimSprite.Centered;
        sprite.FlipH        = _enemyAnimSprite.FlipH;
        sprite.Position     = new Vector2(_enemyAnimSprite.Position.X + slotIndex * EnemySlotSpacing,
                                           _enemyAnimSprite.Position.Y);
        sprite.Material     = CreateCombatantOverlayMaterial(overlayShader);
        AddChild(sprite);
        sprite.Play("idle");

        return (rect, sprite);
    }

    /// <summary>
    /// Creates a fresh <see cref="ShaderMaterial"/> bound to the combatant overlay shader
    /// with both <c>flash_amount</c> and <c>tint_amount</c> uniforms zeroed. Each combatant
    /// gets its own instance so white-flash (learnable signal) and red-tint (threat reveal)
    /// tweens do not interfere across combatants.
    /// </summary>
    private static ShaderMaterial CreateCombatantOverlayMaterial(Shader overlayShader)
    {
        var mat = new ShaderMaterial();
        mat.Shader = overlayShader;
        mat.SetShaderParameter("flash_amount", 0.0f);
        mat.SetShaderParameter("tint_amount",  0.0f);
        return mat;
    }

    /// <summary>
    /// Builds a player <see cref="Combatant"/> around an existing (rect, sprite) pair.
    /// Slot 0 is the Absorber; additional slots are non-Absorbers. Names:
    /// slot 0 → "Knight"; slots 1+ → "Knight N+1".
    /// </summary>
    private Combatant BuildPlayerCombatantForSlot(int slotIndex,
                                                   ColorRect rect,
                                                   AnimatedSprite2D sprite)
    {
        // C7-prereq test agility values: divergent so the tick-based scheduler
        // produces visibly varied turn order at TestFullParty. Slot 0 (Knight,
        // Absorber) is fastest at 12 to exercise Absorber mechanics frequently
        // during scaffolding. Final balance values TBD post-Phase-6.
        int agility = slotIndex switch
        {
            0 => 12,  // Knight   (Absorber)        — fastest player
            1 =>  8,  // Knight 2 (Damage Dealer)   — slowest player
            2 => 10,  // Knight 3 (Buffer/Debuffer)
            3 =>  9,  // Knight 4 (Healer)
            _ => 10,
        };
        return new Combatant
        {
            Name             = slotIndex == 0 ? "Knight" : $"Knight {slotIndex + 1}",
            Side             = CombatantSide.Player,
            CurrentHp        = PlayerMaxHP,
            MaxHp            = PlayerMaxHP,
            Agility          = agility,
            IsDead           = false,
            Origin           = rect.Position,
            AnimSpriteOrigin = sprite.Position,  // post-floor-anchor + per-slot offset
            PositionRect     = rect,
            AnimSprite       = sprite,
            CurrentMp        = PlayerMaxMp,
            MaxMp            = PlayerMaxMp,
            IsDefending      = false,
            IsAbsorber       = slotIndex == 0,  // slot 0 is the sole Absorber in Phase 6
            FlashMaterial    = sprite.Material as ShaderMaterial,
        };
    }

    /// <summary>
    /// Builds an enemy <see cref="Combatant"/> around an existing (rect, sprite) pair.
    /// All slots share the same <see cref="EnemyData"/> (identical copies in Phase 6).
    /// Names: slot 0 → <c>EnemyData.EnemyName</c>; slots 1+ → "<name> N+1".
    /// </summary>
    private Combatant BuildEnemyCombatantForSlot(int slotIndex,
                                                  ColorRect rect,
                                                  AnimatedSprite2D sprite,
                                                  int enemyInitialMaxHp)
    {
        // C7-prereq test agility values: slot 0 (boss) at 10, mooks varied
        // 7-11 for visible asymmetry against the player party. Final balance
        // TBD post-Phase-6.
        int agility = slotIndex switch
        {
            0 => 10,  // Warrior   (boss)
            1 =>  7,  // Warrior 2 (slowest mook)
            2 => 11,  // Warrior 3 (fastest mook)
            3 =>  8,  // Warrior 4
            4 =>  9,  // Warrior 5
            _ => 10,
        };
        string baseName = EnemyData?.EnemyName ?? "Enemy";
        return new Combatant
        {
            Name             = slotIndex == 0 ? baseName : $"{baseName} {slotIndex + 1}",
            Side             = CombatantSide.Enemy,
            CurrentHp        = enemyInitialMaxHp,
            MaxHp            = enemyInitialMaxHp,
            Agility          = agility,
            IsDead           = false,
            Origin           = rect.Position,
            AnimSpriteOrigin = sprite.Position,  // post-floor-anchor + per-slot offset
            PositionRect     = rect,
            AnimSprite       = sprite,
            Data             = EnemyData,
            FlashMaterial    = sprite.Material as ShaderMaterial,
        };
    }

    /// <summary>
    /// Builds the combined enemy panel — Octopath-style single PanelContainer at the
    /// top-right with one compact row per enemy inside its VBoxContainer. Stored on
    /// <see cref="_enemyCombinedPanel"/> for any future direct reference. Each row
    /// is backed by a <see cref="PartyPanel"/> with <c>Panel = null</c> and
    /// <c>ModulateTarget = row HBox</c> so per-row dead/alive Modulate styling
    /// applies independently.
    /// </summary>
    private void BuildEnemyCombinedPanel(CanvasLayer layer)
    {
        var panel = MakeLayeredPanel(EnemyCombinedPanelMinWidth, out var content);
        panel.AnchorLeft     = 1f;
        panel.AnchorRight    = 1f;
        panel.AnchorTop      = 0f;
        panel.AnchorBottom   = 0f;
        panel.GrowHorizontal = Control.GrowDirection.Begin;
        panel.OffsetRight    = -UiEdgeMargin;
        panel.OffsetTop      = UiEdgeMargin;
        layer.AddChild(panel);
        _enemyCombinedPanel = panel;

        for (int i = 0; i < _enemyParty.Count; i++)
            _enemyPanels.Add(BuildEnemyRow(content, _enemyParty[i]));
    }

    /// <summary>
    /// Builds one row inside the combined enemy panel — an HBoxContainer of
    /// [Name Label][HP fill bar][HP overlay text]. The row itself is the
    /// ModulateTarget so per-enemy dead-state grayout doesn't bleed into siblings.
    /// </summary>
    private PartyPanel BuildEnemyRow(VBoxContainer parent, Combatant combatant)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        parent.AddChild(row);

        var nameLabel = new Label();
        nameLabel.Text                = combatant.Name;
        nameLabel.CustomMinimumSize   = new Vector2(EnemyRowNameWidth, 0);
        nameLabel.HorizontalAlignment = HorizontalAlignment.Left;
        nameLabel.VerticalAlignment   = VerticalAlignment.Center;
        StyleLabel(nameLabel);
        row.AddChild(nameLabel);

        var barContainer = new Control();
        barContainer.CustomMinimumSize = new Vector2(EnemyRowBarWidth, BarHeight);
        row.AddChild(barContainer);

        BuildStyledBar(barContainer,
                       UiBarBackLeftPath, UiBarBackMidPath, UiBarBackRightPath,
                       UiBarRedLeftPath,  UiBarRedMidPath,  UiBarRedRightPath,
                       out var hpFill, out var hpLabel,
                       fillTint: HpFillTint,
                       barWidth: EnemyRowBarWidth);

        return new PartyPanel
        {
            Panel          = null,         // combined panel held in _enemyCombinedPanel
            ModulateTarget = row,          // per-row Modulate so dead-styling is local to one row
            NameLabel      = nameLabel,
            HpFill         = hpFill,
            HpLabel        = hpLabel,
            MpFill         = null,
            MpLabel        = null,
            BoundCombatant = combatant,
        };
    }

    /// <summary>
    /// Builds a player HP/MP panel for one slot in the count-aware centered strip at the
    /// bottom of the screen. <paramref name="stripLeft"/> is the X coordinate of the
    /// strip's left edge (computed once in <see cref="BuildStatusPanels"/> so the group
    /// stays centered for any party size). Each slot offsets <c>stripLeft + slotIndex *
    /// (PlayerPanelWidth + PanelStackGap)</c> rightward.
    /// </summary>
    private PartyPanel BuildPlayerPanelForSlot(CanvasLayer layer, Combatant combatant, int slotIndex, float stripLeft)
    {
        var panel = MakeLayeredPanel(PanelMinWidthStatus, out var content);
        panel.AnchorLeft     = 0f;
        panel.AnchorRight    = 0f;
        panel.AnchorTop      = 1f;
        panel.AnchorBottom   = 1f;
        panel.GrowHorizontal = Control.GrowDirection.End;
        panel.GrowVertical   = Control.GrowDirection.Begin;
        panel.OffsetLeft     = stripLeft + slotIndex * (PlayerPanelWidth + PanelStackGap);
        panel.OffsetBottom   = -UiEdgeMargin;
        layer.AddChild(panel);

        var pp = new PartyPanel
        {
            Panel          = panel,
            ModulateTarget = panel,  // outer panel is the Modulate target — cascades to all children
            BoundCombatant = combatant,
        };
        AddPlayerRow(content, combatant.Name, pp);
        return pp;
    }

    private void AddPlayerRow(VBoxContainer parent, string name, PartyPanel pp)
    {
        // Name, then HP bar, then MP bar stacked vertically with 4px gap (VBox separation).
        var nameLabel = new Label();
        nameLabel.Text = name;
        StyleLabel(nameLabel, fontSize: 15);
        parent.AddChild(nameLabel);
        pp.NameLabel = nameLabel;

        var hpContainer = new Control();
        hpContainer.CustomMinimumSize = new Vector2(BarWidth, BarHeight);
        parent.AddChild(hpContainer);
        BuildStyledBar(hpContainer,
                       UiBarBackLeftPath, UiBarBackMidPath, UiBarBackRightPath,
                       UiBarRedLeftPath,  UiBarRedMidPath,  UiBarRedRightPath,
                       out var hpFill, out var hpLabel,
                       fillTint: HpFillTint);
        pp.HpFill  = hpFill;
        pp.HpLabel = hpLabel;

        var mpContainer = new Control();
        mpContainer.CustomMinimumSize = new Vector2(BarWidth, BarHeight);
        parent.AddChild(mpContainer);
        BuildStyledBar(mpContainer,
                       UiBarBackLeftPath, UiBarBackMidPath, UiBarBackRightPath,
                       UiBarBlueLeftPath, UiBarBlueMidPath, UiBarBlueRightPath,
                       out var mpFill, out var mpLabel);
        pp.MpFill  = mpFill;
        pp.MpLabel = mpLabel;
    }

    // =========================================================================
    // Turn-order strip (C7) — vertical column at top-left, top = next-to-act
    // =========================================================================

    /// <summary>
    /// First-time setup of the C7 turn-order strip. Creates the dedicated
    /// CanvasLayer and instantiates the initial <see cref="LookaheadCount"/>
    /// cards from the queue's first Lookahead. Called once from
    /// <see cref="_Ready"/> after <c>_queue.Reset(...)</c>.
    /// </summary>
    private void BuildTurnOrderStrip()
    {
        _turnOrderLayer = new CanvasLayer { Name = "TurnOrderStrip" };
        AddChild(_turnOrderLayer);

        var preview = _queue.Lookahead(LookaheadCount);
        for (int i = 0; i < preview.Count; i++)
            _turnOrderCards.Add(BuildTurnOrderCard(_turnOrderLayer, preview[i], i));
        RefreshTopCardHighlight();
    }

    /// <summary>
    /// Builds one mini-card at the given vertical slot index. Side-coded fill
    /// tint (player blue / enemy burgundy) routes through <see cref="MakeLayeredPanel"/>'s
    /// fillTint parameter so the existing chrome family is preserved with only
    /// the fill layer recolored. Card height is fixed via <c>CustomMinimumSize.Y</c>
    /// so slot offsets are predictable.
    /// </summary>
    private TurnOrderCard BuildTurnOrderCard(CanvasLayer layer, Combatant combatant, int slotIndex)
    {
        Color fillTint = combatant.Side == CombatantSide.Player
            ? PlayerCardFillTint
            : EnemyCardFillTint;
        // contentPad: 9 (vs default 18) shrinks the card vertically so the
        // rendered height matches TurnOrderCardHeight = 36. The PanelContainer
        // auto-sizes to content; without the override, default padding (18) +
        // label height (~18) + padding (18) = 54 px, which would overlap
        // neighboring slots at the 42-px stride (TurnOrderCardHeight + Gap).
        // 9 + 18 + 9 = 36 matches the constant exactly.
        var panel = MakeLayeredPanel(TurnOrderCardWidth, out var content,
                                      minHeight: TurnOrderCardHeight,
                                      fillTint: fillTint,
                                      contentPad: 9);
        panel.AnchorLeft     = 0f;
        panel.AnchorRight    = 0f;
        panel.AnchorTop      = 0f;
        panel.AnchorBottom   = 0f;
        panel.GrowHorizontal = Control.GrowDirection.End;
        panel.OffsetLeft     = UiEdgeMargin;
        panel.OffsetTop      = UiEdgeMargin + slotIndex * (TurnOrderCardHeight + TurnOrderCardGap);
        layer.AddChild(panel);

        var nameLabel = new Label();
        nameLabel.Text                = combatant.Name;
        nameLabel.HorizontalAlignment = HorizontalAlignment.Center;
        StyleLabel(nameLabel, fontSize: 13);
        content.AddChild(nameLabel);

        return new TurnOrderCard
        {
            Panel          = panel,
            NameLabel      = nameLabel,
            BoundCombatant = combatant,
        };
    }

    /// <summary>
    /// Wipes existing cards and rebuilds from scratch via the latest
    /// <c>_queue.Lookahead</c>. No animation. Used by Reset paths
    /// (<see cref="_Ready"/>'s initial render via <see cref="BuildTurnOrderStrip"/>,
    /// Phase 2 transition's <c>SwapToPhase2</c>) and as the fallback when an
    /// in-flight slide gets interrupted by a successive Advance.
    /// </summary>
    private void HardRebindStrip()
    {
        foreach (var card in _turnOrderCards) card.Panel.QueueFree();
        _turnOrderCards.Clear();

        var preview = _queue.Lookahead(LookaheadCount);
        for (int i = 0; i < preview.Count; i++)
            _turnOrderCards.Add(BuildTurnOrderCard(_turnOrderLayer, preview[i], i));
        RefreshTopCardHighlight();
    }

    /// <summary>
    /// Applies <see cref="PanelActiveModulate"/> to the top card and
    /// <see cref="PanelAliveModulate"/> to all others. Called from
    /// <see cref="BuildTurnOrderStrip"/>, <see cref="HardRebindStrip"/>, and
    /// the post-callback inside <see cref="AnimateSlide"/> after the list
    /// rotation. The active boost makes the next-to-act card visibly
    /// distinct beyond the positional convention. During slide-out the
    /// ex-top card's alpha tweens to 0 so the boost is washed out anyway —
    /// no special handling needed for the in-flight slide.
    /// </summary>
    private void RefreshTopCardHighlight()
    {
        for (int i = 0; i < _turnOrderCards.Count; i++)
            _turnOrderCards[i].Panel.Modulate =
                (i == 0) ? PanelActiveModulate : PanelAliveModulate;
    }

    /// <summary>
    /// Refreshes the turn-order strip after a queue mutation. <paramref name="animate"/>
    /// = true triggers the slide animation (top card slides off right + fades, cards
    /// 1..N-1 shift up one slot, new card slides in from below). animate = false is a
    /// hard rebuild — used by Reset paths since "fresh start" should not visually
    /// suggest "turn resolved." If a slide is already in flight, kill it and fall
    /// back to hard rebuild for this Advance (no animation overlap).
    /// </summary>
    private void RefreshTurnOrderStrip(bool animate)
    {
        if (_turnOrderLayer == null) return;  // pre-_Ready safety

        if (_turnOrderTween != null && _turnOrderTween.IsValid() && _turnOrderTween.IsRunning())
        {
            _turnOrderTween.Kill();
            animate = false;  // slide interrupted; rebuild atomically
        }

        if (!animate)
        {
            HardRebindStrip();
            return;
        }

        AnimateSlide();
    }

    /// <summary>
    /// Slide animation triggered by an Advance: top card (the turn that just
    /// resolved) slides off to the right + fades; cards 1..N-1 shift up by one
    /// slot via parallel OffsetTop tweens; a new card spawns at slot N's would-be
    /// position with opacity 0 and slides up + fades into slot N-1. After
    /// 0.18 s, a callback rotates <see cref="_turnOrderCards"/> (RemoveAt(0) +
    /// Add(newCard)) so card identities for slots 1..N-1 persist across the
    /// animation — that's what produces the "slide" feel rather than a rebuild
    /// flash. All tweens run in parallel via <see cref="Tween.SetParallel"/>.
    /// </summary>
    private void AnimateSlide()
    {
        var preview = _queue.Lookahead(LookaheadCount);

        _turnOrderTween = CreateTween();
        _turnOrderTween.SetParallel(true);

        // 1. Slide-out top card: tween rightward + fade to transparent.
        TurnOrderCard slideOut = _turnOrderCards.Count > 0 ? _turnOrderCards[0] : null;
        if (slideOut != null)
        {
            float targetX = slideOut.Panel.OffsetLeft + TurnOrderSlideOffset;
            _turnOrderTween.TweenProperty(slideOut.Panel, "offset_left", targetX, TurnOrderSlideDur)
                           .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
            _turnOrderTween.TweenProperty(slideOut.Panel, "modulate:a", 0f, TurnOrderSlideDur)
                           .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        }

        // 2. Shift cards 1..N-1 up by one slot (each by CardHeight + Gap).
        // Cards 1..N-1 of _turnOrderCards correspond to combatants
        // preview[0..N-2] — Lookahead is deterministic from the post-Advance
        // AP state, and no other mutator has fired since. Names match
        // positions implicitly through this correspondence; no per-card name
        // update needed.
        for (int i = 1; i < _turnOrderCards.Count; i++)
        {
            var card = _turnOrderCards[i];
            float newTop = card.Panel.OffsetTop - (TurnOrderCardHeight + TurnOrderCardGap);
            _turnOrderTween.TweenProperty(card.Panel, "offset_top", newTop, TurnOrderSlideDur)
                           .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        }

        // 3. Spawn new bottom card at slot N's would-be position; tween up to
        // slot N-1's position (its post-shift home) + fade in.
        TurnOrderCard newCard = null;
        if (preview.Count >= LookaheadCount)
        {
            var newCombatant = preview[LookaheadCount - 1];
            newCard = BuildTurnOrderCard(_turnOrderLayer, newCombatant, LookaheadCount);
            newCard.Panel.Modulate = new Color(1f, 1f, 1f, 0f);

            float homeTop = UiEdgeMargin + (LookaheadCount - 1) * (TurnOrderCardHeight + TurnOrderCardGap);
            _turnOrderTween.TweenProperty(newCard.Panel, "offset_top", homeTop, TurnOrderSlideDur)
                           .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
            _turnOrderTween.TweenProperty(newCard.Panel, "modulate:a", 1f, TurnOrderSlideDur);
        }
        // If preview.Count < LookaheadCount, all combatants are dead
        // (CheckGameOver should have caught this). Defensive — no new card
        // spawns, strip shrinks visibly until the end-screen fires.

        // 4. After parallel tweens complete: free the slid-out card and
        // rotate the list (RemoveAt(0) + Add(newCard)). Captures locals so
        // the callback works even if more state changes between schedule and fire.
        var slideOutCapture = slideOut;
        var newCardCapture  = newCard;
        _turnOrderTween.TweenCallback(Callable.From(() =>
        {
            if (slideOutCapture != null && IsInstanceValid(slideOutCapture.Panel))
                slideOutCapture.Panel.QueueFree();
            if (_turnOrderCards.Count > 0) _turnOrderCards.RemoveAt(0);
            if (newCardCapture != null) _turnOrderCards.Add(newCardCapture);
            // Apply the active-card boost to the new top after the rotation —
            // before the rotation, slot 0 is the ex-top (already alpha-faded).
            RefreshTopCardHighlight();
        })).SetDelay(TurnOrderSlideDur);
    }

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
    /// Sets <see cref="_sequenceAttacker"/>, <see cref="_sequenceDefender"/>, and
    /// <see cref="_sequenceAttackerClosePos"/> before starting the tween, so
    /// <see cref="ComputeCameraMidpoint"/> and <see cref="ComputeSlamPosition"/> are
    /// usable immediately after this call returns. <paramref name="onComplete"/> fires
    /// when the tween finishes; safe to pass null.
    /// </summary>
    /// <param name="attackerOffset">
    /// Optional offset for hop-in melee attacks. Only the X component is added to
    /// <see cref="_sequenceAttackerClosePos"/> — this keeps the camera midpoint and slam
    /// positions unaffected by vertical adjustment. The Y component is applied solely
    /// to the enemy-side attacker's AnimatedSprite2D tween destination so the sprite
    /// moves vertically without shifting the camera or target zone. No Y-offset applies
    /// when the attacker is player-side.
    /// </param>
    private void PlayHopIn(Combatant attacker, Combatant defender, Action onComplete,
                           Vector2 attackerOffset = default)
    {
        _sequenceAttacker         = attacker;
        _sequenceDefender         = defender;
        _sequenceAttackerClosePos = ComputeClosePosition(attacker, defender) + new Vector2(attackerOffset.X, 0f);

        // Raise the attacker's sprite ZIndex so it renders in front of the defender
        // during the hop-in overlap. Restored to 0 in PlayTeardown.
        attacker.AnimSprite.ZIndex = 1;
        defender.AnimSprite.ZIndex = 0;

        // Hop-in footstep sound.
        if (!attacker.IsDead)
            PlaySound("short_quick_steps.wav", volumeDb: 6f);

        // Player-side attacker: play run at double speed on the player sprite via the guard
        // helper. Guard helper operates on the single-player sprite in the current UI; at
        // multi-character density the run-animation call routes through a per-unit helper.
        if (attacker.Side == CombatantSide.Player)
        {
            // OWNER: PlayHopIn (player turn, charge begins).
            int runFrames = attacker.AnimSprite.SpriteFrames?.GetFrameCount("run") ?? 0;
            if (runFrames > 0)
            {
                attacker.AnimSprite.SpeedScale = 2f;
                PlayAnim(_sequenceAttacker, "run");   // OWNER: player turn, hop-in charge
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
        tween.TweenProperty(attacker.PositionRect, "position", _sequenceAttackerClosePos, SetupDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);

        // Move the attacker's AnimatedSprite2D by the same X delta as the ColorRect hop.
        // Reads the per-combatant AnimSpriteOrigin snapshot (set in
        // BuildPlayerCombatantForSlot / BuildEnemyCombatantForSlot) so multi-unit
        // hop-ins from any slot retreat to their own origin instead of slot 0's.
        float   hopDeltaX        = _sequenceAttackerClosePos.X - attacker.Origin.X;
        Vector2 animSpriteOrigin = attacker.AnimSpriteOrigin;
        // Y-offset applies only to enemy-side attackers (see parameter doc). Preserves
        // the pre-refactor asymmetric behavior.
        float   animTargetY      = attacker.Side == CombatantSide.Player
            ? animSpriteOrigin.Y
            : animSpriteOrigin.Y + attackerOffset.Y;
        Vector2 animTarget       = new Vector2(animSpriteOrigin.X + hopDeltaX, animTargetY);
        tween.TweenProperty(attacker.AnimSprite, "position", animTarget, SetupDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);

        // Enemy-side attacker: play run at double speed if alive (mirrors player pattern
        // above, but routed through the PlayAnim guard helper on the enemy sprite).
        if (attacker.Side == CombatantSide.Enemy && !attacker.IsDead)
        {
            attacker.AnimSprite.SpeedScale = 2f;
            PlayAnim(_enemyParty[0], "run");
        }

        // Camera zooms in centered between the two combatants.
        tween.TweenProperty(_camera, "position", ComputeCameraMidpoint(attacker, defender), SetupDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        tween.TweenProperty(_camera, "zoom", CameraZoomIn, SetupDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        tween.Finished += () =>
        {
            // Reset enemy-side attacker SpeedScale after hop-in and hold idle until
            // melee_attack starts. Player-side doesn't need this reset because the
            // player's wind-up pose is set explicitly in BeginAttack's hop-in callback.
            if (attacker.Side == CombatantSide.Enemy && !attacker.IsDead)
            {
                attacker.AnimSprite.SpeedScale = 1f;
                PlayAnim(_enemyParty[0], "idle");
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
        Combatant attacker,
        Combatant defender,
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
        Vector2 playerHopInOffset = attacker.Side == CombatantSide.Player
            ? new Vector2(200f, 0f)
            : Vector2.Zero;
        PlayHopIn(attacker, defender, () =>
        {
            // Hop-in finished — freeze on frame 0 (wind-up pose) without playing.
            // The slash fires from frame 1 only after the timing circle resolves,
            // so the pose reads as intent-to-strike while the player waits for input.
            if (attacker.Side == CombatantSide.Player)
            {
                // OWNER: BeginAttack hop-in callback (player turn, awaiting input).
                // "combo" frame 0 = first wind-up pose for both single and combo attacks.
                attacker.AnimSprite.SpeedScale = 1f;
                attacker.AnimSprite.Animation  = "combo";
                SetAnimFrame(attacker, 0);  // OWNER: player turn, wind-up pose (sheet frame 0)
                StopAnim(attacker);
            }
            // Position is set here so ComputeCameraMidpoint reflects the final close stance.
            prompt.Position      = ComputeCameraMidpoint(attacker, defender);
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
        var attacker = _sequenceAttacker;
        Vector2 slamPos = ComputeSlamPosition(attacker, _sequenceDefender);
        var tween = CreateTween();
        tween.TweenProperty(attacker.PositionRect, "position", slamPos, SlamInDuration)
             .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        tween.TweenProperty(attacker.PositionRect, "position", _sequenceAttackerClosePos, SlamOutDuration)
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
                SafeDisconnectAnim(attacker, OnComboPass0SlashFinished);
                PlayAnim(attacker, "combo_slash1");  // OWNER: combo pass 0, first strike (frames 1–3)
                ConnectAnim(attacker, OnComboPass0SlashFinished);
                break;
            case 1:
                PlaySound("player_attack_swing.wav");
                SafeDisconnectAnim(attacker, OnComboPass1SlashFinished);
                PlayAnim(attacker, "combo_slash2");  // OWNER: combo pass 1, second strike (frames 6–9)
                ConnectAnim(attacker, OnComboPass1SlashFinished);
                break;
            case 2:
                // Final strike — OnFinalSlashFinished handles the 0.3s hold and retreat.
                // _pendingGameOver is set in OnPlayerPromptCompleted (PromptCompleted fires in
                // the same frame as the last PassEvaluated) for the all-hits case, or here when
                // the miss branch runs.
                PlaySound("player_attack_swing.wav");
                SafeDisconnectAnim(attacker, OnFinalSlashFinished);
                PlayAnim(attacker, "combo_slash1");  // OWNER: combo pass 2, final strike (frames 1–3)
                ConnectAnim(attacker, OnFinalSlashFinished);
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
        var comboTarget = _enemyParty[0];  // single target in the current UI
        comboTarget.TakeDamage(comboDamage);
        GD.Print($"[BattleTest] Combo pass {passIndex + 1} {comboDmgResult}: {comboDamage} damage. " +
                 $"Enemy HP: {comboTarget.CurrentHp}/{comboTarget.MaxHp}");
        PlaySound("enemy_hit.wav");
        SpawnDamageNumber(ComputeDamageOrigin(comboTarget), comboDamage, comboDmgColor);
        ShakeCamera(intensity: 8f, duration: 0.25f);
        PlayCombatantHurtFlash(comboTarget);
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
        // Read the sequence-scoped attacker set in PlayHopIn (or by the non-hop-in /
        // magic-attack paths that write it directly in BeginEnemyAttack /
        // BeginPlayerMagicAttack). PlayTeardown is called from animation-callback
        // continuations that can't naturally receive the attacker as a parameter, so
        // the field is the right plumbing here.
        var attacker = _sequenceAttacker;
        bool attackerMoved = _sequenceAttackerClosePos != attacker.Origin;

        var tween = CreateTween();
        tween.SetParallel(true);
        // Hop out — ease-in (slow start, accelerates = snapping away).
        tween.TweenProperty(attacker.PositionRect, "position", attacker.Origin, TeardownDuration)
             .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);

        // Return the attacker's AnimatedSprite2D to its scene origin alongside the ColorRect.
        // Reads the per-combatant AnimSpriteOrigin snapshot so multi-unit retreats land
        // each slot back at its own origin instead of slot 0's.
        Vector2 animSpriteOrigin = attacker.AnimSpriteOrigin;
        // Player-side footstep + position tween play unconditionally on teardown —
        // attackerMoved below gates only the enemy-side run-backwards animation (and its
        // paired footstep), not the position tween. Preserved asymmetry from pre-refactor:
        // player retreat animation (PlayAnimBackwards) is driven by animation handlers
        // elsewhere (OnFinalSlashFinished, etc.), not by PlayTeardown.
        if (attacker.Side == CombatantSide.Player && !attacker.IsDead)
            PlaySound("short_quick_steps.wav", volumeDb: 0f);
        tween.TweenProperty(attacker.AnimSprite, "position", animSpriteOrigin, TeardownDuration)
             .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);

        // Enemy-side attacker: play run backwards at double speed during hop-back, only
        // if the attacker actually moved from origin (hop-in melee). Cast attacks stay at
        // origin so _sequenceAttackerClosePos == attacker.Origin; skip the run animation then.
        if (attacker.Side == CombatantSide.Enemy && !attacker.IsDead && attackerMoved)
        {
            PlaySound("short_quick_steps.wav", volumeDb: 0f);
            attacker.AnimSprite.SpeedScale = 2f;
            attacker.AnimSprite.PlayBackwards("run");
        }

        // Camera zooms back out to default.
        tween.TweenProperty(_camera, "position", CameraDefaultPos, TeardownDuration)
             .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
        tween.TweenProperty(_camera, "zoom", CameraDefaultZoom, TeardownDuration)
             .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
        tween.Finished += () =>
        {
            // Enemy-side attacker that moved: reset SpeedScale and return to idle after hop-back.
            if (attacker.Side == CombatantSide.Enemy && attackerMoved && !attacker.IsDead)
            {
                attacker.AnimSprite.SpeedScale = 1f;
                PlayAnim(_enemyParty[0], "idle");
            }
            // Restore default ZIndex on both sequence participants now that the attack
            // is over. The attacker hopped in and had its ZIndex implicitly involved;
            // the defender stays put but is reset for symmetry. Skip the dead enemy —
            // SpawnBossReveal bumped slot 0 enemy's ZIndex up so the reveal stays
            // strictly behind, and SwapToPhase2 restores the original snapshot value.
            // (Player death doesn't bump ZIndex, so the IsDead check only matters for
            // the enemy-side participant.)
            foreach (var c in new[] { _sequenceAttacker, _sequenceDefender })
            {
                if (c.Side == CombatantSide.Enemy && c.IsDead) continue;
                c.AnimSprite.ZIndex = 0;
            }
            onComplete?.Invoke();
        };
    }

    // =========================================================================
    // Animation position helpers
    // =========================================================================

    /// <summary>
    /// Returns the position where the attacker stands in the close stance —
    /// <see cref="AttackGap"/> pixels from the defender's near edge, same Y as origin.
    /// Calculated from the combatants' stored origins so it is independent of any
    /// animation in progress.
    /// </summary>
    private Vector2 ComputeClosePosition(Combatant attacker, Combatant defender)
    {
        Vector2 attackerOrigin = attacker.Origin;
        Vector2 defenderOrigin = defender.Origin;
        bool    onLeft         = attackerOrigin.X < defenderOrigin.X;

        float closeX = onLeft
            ? defenderOrigin.X - attacker.PositionRect.Size.X - AttackGap   // attacker right edge = defender left - gap
            : defenderOrigin.X + defender.PositionRect.Size.X + AttackGap;  // attacker left edge  = defender right + gap

        return new Vector2(closeX, attackerOrigin.Y);
    }

    /// <summary>
    /// Returns the slam position — attacker overlaps the defender by <see cref="SlamOverlap"/> pixels.
    /// Reads <see cref="_sequenceAttackerClosePos"/> for the slam's Y (so slam stays on the
    /// close-stance horizontal line regardless of any Y-offset applied during hop-in).
    /// </summary>
    private Vector2 ComputeSlamPosition(Combatant attacker, Combatant defender)
    {
        Vector2 attackerOrigin = attacker.Origin;
        Vector2 defenderOrigin = defender.Origin;
        bool    onLeft         = attackerOrigin.X < defenderOrigin.X;

        float slamX = onLeft
            ? defenderOrigin.X - attacker.PositionRect.Size.X + SlamOverlap   // right edge overlaps defender by SlamOverlap
            : defenderOrigin.X + defender.PositionRect.Size.X - SlamOverlap;  // left edge overlaps defender by SlamOverlap

        return new Vector2(slamX, _sequenceAttackerClosePos.Y);
    }

    /// <summary>
    /// Returns the world-space midpoint between the attacker's close stance center
    /// and the defender's center — the point the camera zooms in on.
    /// Reads <see cref="_sequenceAttackerClosePos"/> for the attacker's current
    /// close-stance position (which differs from its rest origin when the attacker
    /// has hopped in).
    /// </summary>
    private Vector2 ComputeCameraMidpoint(Combatant attacker, Combatant defender)
    {
        Vector2 attackerCenter = _sequenceAttackerClosePos + attacker.PositionRect.Size / 2f;
        Vector2 defenderCenter = defender.Origin           + defender.PositionRect.Size / 2f;
        return (attackerCenter + defenderCenter) / 2f;
    }
}
