# Target Selection Audit — 1v1 Assumptions in Combat Code

## Purpose
Catalog of every location in the existing combat code that hardcodes a 1v1
assumption — a single player, a single enemy, or a single attacker-defender
pair. Produced as a reference for the upcoming multi-character refactor (see
`docs/target-selection-and-scaffolding-plan.md`).

Scope: combat semantics only. Animation-internal references to `_playerAnimSprite`
or `_enemyAnimSprite` that operate on their own sprite (play, stop, set frame,
hit flash lifecycle) are not findings — that's local animation state, not a
combat targeting assumption. Findings are limited to places where "the player"
or "the enemy" or "the defender" is treated as a unique referent for combat
logic (damage routing, target positioning, effect spawning, state tracking).

Findings are grouped by category because the refactor will proceed
category-by-category, not file-by-file, and many categories span multiple
files.

---

## A. HP / MP / per-unit combat state

These are scalar fields that will need to become per-unit state (tracked
against individual combatants) once multiple of each side exist.

### A1. Single-scalar HP fields
**Location:** `BattleTest.cs:49-51`
```csharp
private int _playerHP    = PlayerMaxHP;
private int _enemyHP     = EnemyMaxHPDefault;
private int _enemyMaxHP  = EnemyMaxHPDefault;
```
**Assumption type:** state
**Assumes:** one player has one HP pool; one enemy has one HP pool + one max.
**Resolution sketch:** per-unit `CurrentHp` / `MaxHp` on a `Combatant` abstraction. Every read/write of `_playerHP` / `_enemyHP` becomes `unit.CurrentHp` where `unit` is resolved from attacker / defender / target context.
**Risk/complexity:** Medium per individual call site, **but High as the structural linchpin** — almost every Medium-risk finding below depends on the Combatant abstraction that A1 motivates. Delivering A1 is the single largest structural lever in the refactor; most other findings resolve mechanically once A1 is in place. See "Design questions surfaced by the audit" at the end.

### A2. Single-scalar MP
**Location:** `BattleTest.cs:57, 61`
```csharp
[Export] public int PlayerMaxMp = 50;
private int _playerMp;
```
**Assumption type:** state
**Assumes:** one player has one MP pool.
**Resolution sketch:** per-unit `MaxMp` / `CurrentMp` on the Combatant abstraction (enemies don't spend MP in the current design, but the pool belongs per-unit).
**Risk/complexity:** Low — touches `UseEtherItem`, `PerformBeckon`, `RestoreMp`, `UpdateMpBar`.

### A3. Single-enemy HP bar references
**Location:** `BattleTest.cs:67-73`
```csharp
private Control _playerHPFill;
private Control _enemyHPFill;
private Control _playerMPFill;
private Label   _playerHPLabel;
private Label   _enemyHPLabel;
private Label   _enemyNameLabel;
private Label   _playerMPLabel;
```
**Assumption type:** UI state (one-per-side)
**Assumes:** exactly one of each UI element per side. `UpdateHPBars` ([BattleTest.cs:2042](Scripts/Battle/BattleTest.cs:2042)) writes to exactly these fields.
**Resolution sketch:** per-unit HP/MP bar, built from a per-combatant panel. The status-panel layout will need to scale to 4 players and 5 enemies — outside strict refactor scope; see scaffolding exercise.
**Risk/complexity:** Medium — the code update is mechanical (replace singletons with a per-unit lookup) but the panel *layout* decisions are visually-tuned and tangled with the broader UI.

### A4. Per-sequence parry state tracks "the enemy's attack"
**Location:** `BattleTest.cs:81`, used at `:1074, :1076, :1176, :1417, :1488`
```csharp
private bool _parryClean;  // set true at start of each enemy attack
```
**Assumption type:** state (implicit singleton defender)
**Assumes:** one ongoing enemy attack at a time, against one player defender.
**Resolution sketch:** keep as per-sequence context but bind to the specific sequence/target pair. If simultaneous attacks are ever wanted, this becomes per-sequence scoped state rather than a top-level field.
**Risk/complexity:** Low — naming and scope adjustment.

### A5. Player-side single bool state
**Locations:** `BattleTest.cs:85, 89, 93`
```csharp
private bool _playerDefending;        // Defend halves miss damage
private bool _hasAbsorbedLearnableMove; // prevents re-absorption
private bool _beckoning;              // baits THE enemy
```
**Assumption type:** state
**Assumes:** one player, one enemy. Defend applies to the single player. Absorption is tracked once for the single enemy's learnable. Beckon baits the single enemy.
**Resolution sketch:** `_playerDefending` → per-unit defend state. `_hasAbsorbedLearnableMove` → per-enemy (or per-learnable-move) absorbed flag. `_beckoning` → needs a target: which enemy to beckon.
**Risk/complexity:** Medium — `_hasAbsorbedLearnableMove` tangles with absorb-loop semantics; beckon needs a target-selection UX, not just a flag.

### A6. CheckGameOver uses direct scalar comparison
**Location:** `BattleTest.cs:2025-2040`
```csharp
if (_playerHP <= 0) { ... return true; }
if (_enemyHP <= 0) { ... return true; }
```
**Assumption type:** formula / state
**Assumes:** one player and one enemy. Game over condition is "either scalar dropped to zero."
**Resolution sketch:** party-wipe / enemy-wipe semantics — iterate `playerParty.Any(u => u.IsAlive)` and `enemyParty.Any(u => u.IsAlive)`. Pre-scaffolding decision: Phase 1 boss is 1-enemy anyway, so the refactor can land without changing game-over behavior.
**Risk/complexity:** Low for the structural change; Medium when we decide what wins/loses mean under mixed partial-death states.

### A7. Single death flags
**Location:** `BattleTest.cs:212-213`
```csharp
private bool _playerDead;
private bool _enemyDead;
```
**Assumption type:** state
**Assumes:** one player, one enemy; each either dead or alive.
**Resolution sketch:** per-unit `IsDead` on the Combatant abstraction. Dead-flag guards (`PlayPlayer`, `PlayEnemy` helpers in `BattleAnimator.cs`) become per-unit.
**Risk/complexity:** Medium — touches many sprite-interaction call sites that go through `PlayPlayer`/`PlayEnemy`/`StopPlayer`/etc. Dead-flag guard helper pattern needs to generalize.

---

## B. Sprite references

### B1. Single AnimatedSprite2D per side
**Location:** `BattleTest.cs:191-192`
```csharp
private AnimatedSprite2D _enemyAnimSprite;
private AnimatedSprite2D _playerAnimSprite;
```
**Assumption type:** field reference
**Assumes:** exactly one animated sprite per side. Every `_playerAnimSprite.*` call in combat-semantics contexts (damage routing, hurt flash, sprite positioning in combat) presumes this single reference.
**Resolution sketch:** per-unit `AnimatedSprite2D` on the Combatant abstraction. Combat-semantics references become `attacker.AnimSprite` / `defender.AnimSprite`. Animation-lifecycle references (a sprite managing its own animation state) can stay local to that sprite.
**Risk/complexity:** Medium — the combat-semantics vs animation-lifecycle distinction needs to be enforced on every callsite. Many calls look identical but serve different roles.

### B2. Single ColorRect sprite wrappers
**Location:** `BattleTest.cs:200-201`
```csharp
private ColorRect _playerSprite;
private ColorRect _enemySprite;
```
**Assumption type:** field reference
**Assumes:** one ColorRect per side, used as the positioning anchor for attacker/defender math.
**Resolution sketch:** per-unit positioning anchor. The ColorRect-vs-AnimatedSprite2D split is a legacy concern (positioning math uses ColorRect; visual rendering uses AnimatedSprite2D) — may be worth collapsing during this refactor, but that's a Phase 1 code review decision, not a targeting concern directly.
**Risk/complexity:** Medium — tightly coupled with positioning math.

### B3. Single origin snapshots
**Location:** `BattleTest.cs:202-205`
```csharp
private Vector2 _playerOrigin;
private Vector2 _enemyOrigin;
private Vector2 _playerAnimSpriteOrigin;
private Vector2 _enemyAnimSpriteOrigin;
```
**Assumption type:** state
**Assumes:** one origin per side.
**Resolution sketch:** per-unit `Origin` / `AnimSpriteOrigin` on the Combatant abstraction.
**Risk/complexity:** Low — used by `GetOrigin` (C3), `PlayTeardown`, `PlayHopIn`.

### B4. Single enemy flash material
**Location:** `BattleTest.cs:174-175`
```csharp
private ShaderMaterial _enemyFlashMaterial;
private Tween          _enemyFlashTween;
```
**Assumption type:** field reference
**Assumes:** one enemy with one flash material.
**Resolution sketch:** per-enemy flash material attached to each enemy's sprite. `FlashEnemyWhite` takes an enemy reference.
**Risk/complexity:** Low.

### B5. Hardcoded damage-number spawn points
**Location:** `BattleTest.cs:108-109`
```csharp
private static readonly Vector2 PlayerDamageOrigin = new Vector2(440f,  570f);
private static readonly Vector2 EnemyDamageOrigin  = new Vector2(1480f, 530f);
```
**Assumption type:** formula (hardcoded constants)
**Assumes:** exactly one known player position (x=440, y=570) and one enemy position (x=1480, y=530).
**Resolution sketch:** derive per-unit damage origin at spawn time from the unit's sprite position + frame-size offset, instead of hardcoding.
**Risk/complexity:** Low.

---

## C. Positioning and layout — pair-based

### C1. Single attacker / defender state
**Location:** `BattleTest.cs:208-210`
```csharp
private ColorRect _attacker;
private ColorRect _defender;
private Vector2   _attackerClosePos;
```
**Assumption type:** state
**Assumes:** exactly one attacker and one defender at any time. Positioning helpers read these fields directly.
**Resolution sketch:** pass attacker/defender as explicit parameters through the call chain (BeginEnemyAttack → PlayHopIn → ComputeCameraMidpoint etc.), eliminating the top-level field. The fields exist to avoid threading — threading them through makes multi-target viable.
**Risk/complexity:** Medium — touches all the pair-based positioning helpers (C3-C6).

### C2. `GetOrigin(ColorRect sprite)` — binary player-or-enemy lookup
**Location:** `BattleTest.cs:2750-2751`
```csharp
private Vector2 GetOrigin(ColorRect sprite) =>
    sprite == _playerSprite ? _playerOrigin : _enemyOrigin;
```
**Assumption type:** formula (binary branch)
**Assumes:** any sprite is either THE player or THE enemy; no other units exist.
**Resolution sketch:** take a Combatant reference and return its own `Origin`. Alternatively, a dictionary lookup keyed on sprite reference. Once Combatant exists, each unit owns its origin and this function collapses into a field access.
**Risk/complexity:** Low.

### C3. `ComputeClosePosition` / `ComputeSlamPosition` / `ComputeCameraMidpoint`
**Location:** `BattleTest.cs:2758, 2776, 2793`
**Assumption type:** formula (reads `_attacker` + `_defender` fields)
**Assumes:** single current attacker-defender pair; `GetOrigin` resolves correctly for both.
**Resolution sketch:** take explicit attacker + defender parameters rather than reading state. Internally use per-unit Origin via Combatant.
**Risk/complexity:** Low (C1 is the prerequisite).

### C4. `PlayHopIn(ColorRect attacker, ColorRect defender, ...)` — binary side branching
**Location:** `BattleAnimator.cs`, function definition at `BattleTest.cs:2453`
```csharp
if (attacker == _playerSprite) { _playerAnimSprite.ZIndex = 1; _enemyAnimSprite.ZIndex = 0; }
else if (attacker == _enemySprite) { _enemyAnimSprite.ZIndex = 1; _playerAnimSprite.ZIndex = 0; }
```
**Assumption type:** formula (binary side branch)
**Assumes:** attacker is either the single player sprite or the single enemy sprite; no third option exists.
**Resolution sketch:** take an `AttackerIsPlayerSide` bool (or better, a `Side` enum / faction enum), generalize the ZIndex / animation / sound calls to work from the unit reference. `attacker == _playerSprite` comparisons become `attacker.Side == Side.Player`.
**Risk/complexity:** Medium — this function has five binary-side branches (ZIndex, footstep sound, run animation player, X-delta computation for AnimatedSprite2D, teardown cleanup). Each needs generalization.

### C5. `PlayTeardown` — same binary-side pattern
**Location:** `BattleAnimator.cs`, function at `BattleTest.cs:2689`
**Assumption type:** formula
**Assumes:** `_attacker` is one of two known ColorRects.
**Resolution sketch:** same as C4.
**Risk/complexity:** Medium.

---

## D. Attack routing and BattleSystem signatures

### D1. `BattleSystem.StartSequence` signature
**Location:** `BattleSystem.cs:199-200`
```csharp
public void StartSequence(Node2D parent, Vector2 defenderCenter, Vector2 promptPosition,
                          bool isPlayerAttack = false)
```
**Assumption type:** signature
**Assumes:** one defender (identified by its center), binary attacker side.
**Resolution sketch:** replace `Vector2 defenderCenter` + `bool isPlayerAttack` with explicit `Combatant attacker` and `Combatant target` (or a collection if multi-target). BattleSystem internally derives `defenderCenter` from `target`, and the flip direction from attacker + target positions.
**Risk/complexity:** Medium — signature change affects callers in `BeginEnemyAttack`, `BeginPlayerMagicAttack`, `OnPlayerCastFinished`.

### D2. `_isPlayerAttack` binary side flag
**Location:** `BattleSystem.cs:66`
```csharp
private bool _isPlayerAttack;  // true when player is the attacker — uses step.PlayerOffset
```
**Assumption type:** field reference / state
**Assumes:** attacker is player OR enemy, never something else.
**Resolution sketch:** replace with `Combatant _attacker` reference. `_attacker.Side` is available for any branching still needed.
**Risk/complexity:** Low (cascades from D1).

### D3. Single `_defenderCenter` / `_promptPosition` fields
**Location:** `BattleSystem.cs:63-64`
```csharp
private Vector2 _defenderCenter;
private Vector2 _promptPosition;
```
**Assumption type:** state
**Assumes:** one current defender, one prompt position for the whole sequence.
**Resolution sketch:** per-target center (if sequence has multiple defenders) or a single `_target` Combatant reference from which center is derived at spawn time.
**Risk/complexity:** Low (cascades from D1).

### D4. FlipH auto-invert
**Location:** `BattleSystem.cs:520`
```csharp
sprite.FlipH = _isPlayerAttack ? !step.FlipH : step.FlipH;
```
**Assumption type:** formula (binary direction)
**Assumes:** direction of effect sprite is always player-side → enemy-side or vice versa. No same-side attacks (e.g. heal where target is an ally).
**Resolution sketch:** compute direction from attacker-to-target vector sign. Effect sprites facing left-to-right when `target.X > attacker.X`, right-to-left when `target.X < attacker.X`.
**Risk/complexity:** Medium — needs a per-target FlipH determination; existing .tres files author FlipH with the binary assumption in mind, so they may need revisiting.

### D5. `AttackStep.Offset` vs `AttackStep.PlayerOffset`
**Location:** `AttackStep.cs:97-108`
```csharp
[Export] public Vector2 Offset       = Vector2.Zero;  // when enemy uses this attack
[Export] public Vector2 PlayerOffset = Vector2.Zero;  // when player uses this attack
```
**Assumption type:** field reference (schema)
**Assumes:** binary attacker side. Authors tune the effect position once per side.

**Resolution — this is a design decision, not a mechanical refactor.** Three options worth comparing explicitly in the schema-migration pass:

1. **Collapse to single target-relative offset** — one `TargetOffset` field, interpreted relative to the target with attacker-side direction normalized out. Cleanest data model; loses perspective-specific tuning flexibility. All existing .tres files need re-authoring.
2. **Keep both but clarify semantics** — rename to something like `AttackerSideOffset` / `DefenderSideOffset` or `LeftSideOffset` / `RightSideOffset` to make the perspective-relative intent explicit. Effectively a rename + docs pass; no migration cost. Leaves the binary-side assumption baked in at the data level.
3. **Author once from a canonical direction; engine mirrors appropriately** — single `CanonicalOffset` field authored assuming attacker-on-left. Engine computes mirrored offset when the attacker is on the right. Preserves per-attack visual tuning while collapsing the schema. Migration cost is moderate — existing values need per-attack inspection to decide which "side" they were authored for.

The current proposal in this audit is option 1, but that preference is **not load-bearing**. The Offset/PlayerOffset split may actually be the right design if per-side perspective really matters for polish. Decide before migrating any .tres files.

**Risk/complexity:** High — every existing `.tres` attack file has both fields visually tuned, and the right resolution depends on design choices not made yet.

### D6. BattleSystem cancel-on-miss rule
**Location:** `BattleSystem.cs:438-445`
```csharp
if (!_sequenceCancelled &&
    _isPlayerAttack &&
    _currentAttack?.Category == AttackCategory.Physical &&
    (TimingPrompt.InputResult)result == TimingPrompt.InputResult.Miss)
```
**Assumption type:** formula (binary side)
**Assumes:** player-side is the only side that cancels on miss; enemy attacks always complete.
**Resolution sketch:** the design rule is actually "attacker's party cancels on miss" — when the player's party contains multiple members, the rule stays the same (player-party attack cancels on miss; enemy-party attack plays through). Replace `_isPlayerAttack` with `_attacker.Side == Side.Player`.
**Risk/complexity:** Low (cascades from D2).

---

## E. Damage application — single-target implicit

### E1. Player-attack damage routing
**Locations:** `BattleTest.cs:1584, 1640, 2665`
```csharp
_enemyHP = Mathf.Max(0, _enemyHP - damage);        // OnPlayerPromptCompleted
_enemyHP = Mathf.Max(0, _enemyHP - amount);        // OnPlayerMagicPassEvaluated
_enemyHP = Mathf.Max(0, _enemyHP - comboDamage);   // OnAttackPassEvaluated
```
**Assumption type:** damage
**Assumes:** one enemy; any player attack lands on THE enemy.
**Resolution sketch:** `target.TakeDamage(amount)` where `target` is resolved from the attack's selected target. `SpawnDamageNumber` likewise takes per-target position.
**Risk/complexity:** Low (cascades from A1 + D1).

### E2. Enemy-attack damage routing
**Location:** `BattleTest.cs:1179`
```csharp
_playerHP = Mathf.Max(0, _playerHP - damage);
```
**Assumption type:** damage
**Assumes:** enemy attack always damages THE player.
**Resolution sketch:** same as E1 with target resolved from the enemy's selected target.
**Risk/complexity:** Low.

### E3. Heal target routing
**Location:** `BattleTest.cs:1530, 1533-1535, 1621`
```csharp
_defender = _isPlayerHealAttack ? _playerSprite : _enemySprite;
Vector2 defenderCenter = _isPlayerHealAttack
    ? GetOrigin(_playerSprite) + _playerSprite.Size / 2f
    : GetOrigin(_enemySprite)  + _enemySprite.Size / 2f;
_playerHP = Mathf.Min(PlayerMaxHP, _playerHP + amount);  // heal lands on THE player
```
**Assumption type:** formula (binary target)
**Assumes:** Cure heals THE player; any other magic targets THE enemy.
**Resolution sketch:** target is selected by the player (future target-selection UX) and passed through explicitly. `_isPlayerHealAttack` collapses into "the chosen target is an ally."
**Risk/complexity:** Medium — intertwined with the magic-attack flow; also the point where `target = self vs. target = ally` matters.

### E4. Perfect-parry counter damage
**Location:** `BattleAnimator.cs:993`
```csharp
_enemyHP = Mathf.Max(0, _enemyHP - CounterDamage);
```
**Assumption type:** damage (no target parameter)
**Assumes:** there's one enemy whose just-completed attack is being countered.
**Resolution sketch:** counter-attack target is the unit whose attack was parried. Thread attacker reference through `PlayParryCounter` and `SpawnCounterSlashEffect`. Also flagged in Known Next Steps as a candidate for `BattleSystem.StartSequence` refactor — resolving that refactor resolves this finding naturally.
**Risk/complexity:** Medium — sits alongside the hand-rolled counter-attack timer cascade that already needs refactoring.

### E5. `SpawnDamageNumber` callsites use hardcoded origins
**Locations:** `BattleTest.cs:1182, 1587, 1623, 1643, 2669`
```csharp
SpawnDamageNumber(PlayerDamageOrigin, damage, DmgColorPlayer);
SpawnDamageNumber(EnemyDamageOrigin, damage, dmgColor);
```
**Assumption type:** formula
**Assumes:** known single-position damage origins per side (B5).
**Resolution sketch:** derive per-target damage origin from the target unit's current sprite position. Helper: `ComputeDamageOrigin(Combatant unit)` returning above-head world position.
**Risk/complexity:** Low.

---

## F. Effect / visual spawning

### F1. `SpawnCounterSlashEffect` hardcoded target position
**Location:** `BattleAnimator.cs:1075`
```csharp
sprite.Position = _enemyAnimSprite.Position;
```
**Assumption type:** formula (implicit target)
**Assumes:** counter slash always spawns on THE enemy (the one whose attack was parried).
**Resolution sketch:** take a target parameter. Parent handler already knows which enemy was countering — thread that reference through.
**Risk/complexity:** Low.

### F2. `SpawnEtherEffect` hardcoded on player
**Location:** `BattleTest.cs:1778`
```csharp
Vector2 playerCenter = GetOrigin(_playerSprite) + _playerSprite.Size / 2f;
```
**Assumption type:** formula
**Assumes:** Ether is always used by/on THE player.
**Resolution sketch:** Ether will eventually be usable on any ally — take a target parameter. For the prototype scope, target is "self" (the current player), so the parameter just becomes the user's own reference.
**Risk/complexity:** Low.

### F3. `PlayEnemyHurtFlash`, `FlashEnemyWhite`, `ShakeEnemySprite`
**Locations:** `BattleAnimator.cs:1116, 2008, 1093`
**Assumption type:** field reference (no target parameter)
**Assumes:** operates on THE enemy sprite.
**Resolution sketch:** take target parameter. Each call site knows which enemy was hit / is signaling / is being shaken.
**Risk/complexity:** Low.

### F4. `BattleSystem.SpawnEffectSprite` uses `_defenderCenter` + binary offset
**Location:** `BattleSystem.cs:511, 520, 522`
```csharp
Vector2 activeOffset = _isPlayerAttack ? step.PlayerOffset : step.Offset;
sprite.FlipH = _isPlayerAttack ? !step.FlipH : step.FlipH;
sprite.Position = new Vector2(_defenderCenter.X, FloorY) + activeOffset;
```
**Assumption type:** formula (binary side + single defender)
**Assumes:** one target per sequence, binary attacker side.
**Resolution sketch:** target reference → per-call defenderCenter derivation; AttackStep offset collapses to single target-relative value (D5).
**Risk/complexity:** Medium (driven by D1 + D4 + D5).

---

## G. Attack / target selection

### G1. No target-selection concept exists
**Assumption type:** system omission
**Assumes:** attacker's target is always obvious (the one on the other side). `BeginEnemyAttack` assigns `_defender = _playerSprite` directly; player attacks target `_enemySprite` without selection.
**Resolution sketch:** new `SelectingTarget` battle state + cycle input + target-pointer visualization — scope of Part 1 of the target-selection plan.
**Risk/complexity:** High — the whole feature being built; this audit is the prerequisite.

### G2. `AttackSelector.SelectAttack` picks attack, not target
**Location:** `AttackSelector.cs` entire file
**Assumption type:** system omission
**Assumes:** an enemy attack's target is implicit (THE player).
**Resolution sketch:** enemy AI eventually also needs target selection — pair `AttackSelector` with a `TargetSelector` (or extend it). Not strictly a 1v1-refactor concern today because prototype is 1v1, but worth flagging as the scaffolding exercise will exercise this.
**Risk/complexity:** Medium (Phase 2 enemy-AI work, not immediate refactor).

### G3. Beckon targets THE enemy implicitly
**Location:** `BattleTest.cs:1972-1980` (`PerformBeckon`)
**Assumption type:** state (`_beckoning` single bool)
**Assumes:** one enemy to beckon.
**Resolution sketch:** beckon needs an enemy target when the enemy party is >1. Flag per-unit or carry a `_beckonTarget` reference.
**Risk/complexity:** Low.

---

## H. Scene structure

### H1. `BattleTest.tscn` defines single player + single enemy nodes
**Location:** `Scenes/Battle/BattleTest.tscn`
**Assumption type:** scene structure
**Assumes:** one `PlayerSprite`, one `EnemySprite`, one `PlayerAnimatedSprite`, one `EnemyAnimatedSprite`.
**Resolution sketch:** either instantiate per-unit nodes at runtime (BattleTest constructs them from party / enemy-pool data) or keep a fixed-max-size node tree with Visible toggles per active slot. The scaffolding exercise should decide.
**Risk/complexity:** High — the scene itself is the fixed shape that everything else hangs off.

### H2. `EnemyData` is a single `[Export]` field
**Location:** `BattleTest.cs:171`
```csharp
[Export] public EnemyData EnemyData;
```
**Assumption type:** field reference
**Assumes:** one enemy per battle.
**Resolution sketch:** `EnemyData[] EnemyParty` (or a new `BattleData` resource describing both parties).
**Risk/complexity:** Low structurally, Medium in cascading impact (everywhere `EnemyData` is accessed).

---

## I. Per-sequence caches with single-defender context

### I1. `_playerMagicDefenderCenter` / `_playerMagicPromptPos`
**Location:** `BattleTest.cs:262-263`
```csharp
private Vector2 _playerMagicDefenderCenter;
private Vector2 _playerMagicPromptPos;
```
**Assumption type:** state
**Assumes:** one target for a magic attack, captured at the moment the cast begins.
**Resolution sketch:** these caches exist to bridge the cast-animation wait before sequence starts. They can stay as context for the current magic cast but need to be keyed to the selected target rather than the single defender.
**Risk/complexity:** Low.

### I2. `_bouncingHopInStep` / `_bouncingHopInAnimDelay`
**Location:** `BattleTest.cs:274-276`
**Assumption type:** state
**Assumes:** one active hop-in attacker at a time. `OnBouncingHopInPassEvaluated` plays on `_enemyAnimSprite`.
**Resolution sketch:** attach the bouncing-replay subscription to the specific attacker sprite via closure, not top-level fields.
**Risk/complexity:** Low.

---

## J. Signal payloads — implicit single-context subscribers

Signals fired during attack sequences carry enough info to identify the step / pass / result, but nothing about **which attacker** or **which target** the sequence belongs to. Subscribers resolve that context from the singleton `_attacker` / `_defender` fields. Once multiple sequences can be live (or multiple attacker-target pairs exist even in serial combat), subscribers can no longer assume the currently-firing signal relates to the combatant they care about.

### J1. `BattleSystem` signals lack combatant context
**Location:** `BattleSystem.cs:44-50`
```csharp
[Signal] public delegate void StepPassEvaluatedEventHandler(int result, int passIndex, int stepIndex);
[Signal] public delegate void StepStartedEventHandler(int stepIndex);
[Signal] public delegate void SequenceCompletedEventHandler();
```
**Assumption type:** signature
**Assumes:** the currently-running sequence is the singleton sequence the subscriber cares about. Context is implicit (read from caller-side state when the handler fires).
**Resolution sketch:** add attacker / target references to the payload — e.g. `StepPassEvaluated(int result, int passIndex, int stepIndex, Combatant attacker, Combatant target)`. Alternatively, `SequenceCompleted(Combatant attacker, Combatant target)` so subscribers route cleanly. Either way, the payload carries enough to disambiguate concurrent sequences if ever needed.
**Risk/complexity:** Low — signal signature expansion, mechanical callsite updates.

### J2. `TimingPrompt` signals lack combatant context
**Location:** `TimingPrompt.cs` (signal definitions)
```csharp
[Signal] public delegate void PassEvaluatedEventHandler(int result, int passIndex);
[Signal] public delegate void PromptCompletedEventHandler(int result);
```
**Assumption type:** signature
**Assumes:** the subscriber knows which prompt (and therefore which attacker-target context) the signal belongs to, typically because there's one active prompt or the subscriber subscribed to a specific prompt instance.
**Resolution sketch:** TimingPrompt is the right place for target-agnosticism — it's a UI widget, not a combat entity. Keep signals target-free. The attacker/target routing happens one level up, where BattleSystem subscribes to prompts on behalf of a specific attacker-target pair. Document this as the intended separation.
**Risk/complexity:** Low — may be zero-change; just needs the separation documented so future work doesn't muddy it.

---

## K. Audio routing — callsite identity (low priority)

The `PlaySound` helpers themselves are target-agnostic and fine (noted in Non-findings). The **callsites** use generic identity-free sounds that don't carry per-unit semantics. Not a blocker for multi-target combat — the sounds still denote the right event — but worth documenting so the scaffolding exercise can verify audio readability at density.

### K1. Generic per-side hit/parry sounds
**Locations:** `BattleTest.cs:1181, 1335-1337, 1586, 1642, 1995, 2668`
```csharp
PlaySound("player_hit.wav");    // plays whenever any enemy attack lands
PlaySound("enemy_hit.wav");     // plays whenever any player attack lands
PlaySound("parry_clash.wav");   // plays on every successful block
```
**Assumption type:** implicit singleton per side
**Assumes:** "player was hit" and "enemy was hit" are meaningful semantic units on their own. True at 1v1; at density, sounds pile up without indicating which unit — Knight #2 taking damage sounds identical to Knight #3 taking damage.
**Resolution sketch:** decide during the scaffolding exercise whether per-unit audio variation is worth it. Options: positional AudioStreamPlayer2D (stereo panning based on unit X-position), per-unit hit-sound variants, or leaving as-is if the prototype still reads at density.
**Risk/complexity:** Low as a code change, **Design decision** on whether to do it at all.

### K2. Counter-attack and magic audio similarly generic
**Locations:** `BattleAnimator.cs:986, 995`, `BattleTest.cs:1542, 1730`
```csharp
PlaySound("counter_slash_multi.wav");  // plays on parry counter
PlaySound("magic_impact.wav");         // plays at magic circle close
```
**Assumption type:** implicit singleton
**Assumes:** one counter per turn, one magic impact per turn.
**Resolution sketch:** same as K1 — positional or identity-carrying audio if density warrants.
**Risk/complexity:** Low.

---

## Summary

### Counts by risk/complexity

| Risk | Count | Examples |
|---|---|---|
| Low | 20 | MP scalar, origin snapshots, `GetOrigin`, `ComputeCameraMidpoint`, damage routing call sites, per-sequence caches, signal payloads (J1, J2), audio (K1, K2) |
| Medium | 11 | Status panels, Sprite refs, Dead flags, PlayHopIn binary branches, StartSequence signature, FlipH direction, Heal target, EnemyData cascade, Counter-attack, AttackSelector pairing, `_hasAbsorbedLearnableMove` |
| High | 4 | **A1 (HP scalars — the linchpin)**, D5 (AttackStep Offset/PlayerOffset schema), G1 (target-selection feature itself), H1 (BattleTest.tscn scene structure) |

### Cross-cutting patterns observed

1. **Binary "which side" comparisons are everywhere.** `attacker == _playerSprite`, `_isPlayerAttack`, `_isPlayerHealAttack` — these appear across BattleTest.cs, BattleSystem.cs, and BattleAnimator.cs. A `Side` / faction enum on a Combatant abstraction collapses most of them into a single comparison pattern. Worth establishing early in the refactor.

2. **Implicit-target functions dominate.** Most damage / effect / hurt-reaction functions take no target parameter because the target is always obvious. Threading target references through is mechanical but widespread — mostly-Low-risk work that adds up to a large total line count.

3. **Positioning math depends on stored pair state.** `_attacker` + `_defender` + `_attackerClosePos` drive every positioning helper. Threading attacker/defender as explicit parameters (rather than reading from fields) is the refactor's central lever — it eliminates the pair singleton and lets each sequence carry its own context.

4. **The `AttackStep.Offset` vs `PlayerOffset` schema is the only High-risk per-unit finding where .tres content would need re-tuning.** Everything else is code-only. If the refactor lands before the effect-offset schema change, visual tuning can stay as-is; the schema migration can be deferred to a separate pass.

5. **Per-side singleton flags — `_parryClean`, `_playerDefending`, `_hasAbsorbedLearnableMove`, `_beckoning` — cluster around player-initiated state that will want per-unit tracking.** Not urgent, but worth grouping when the Combatant abstraction lands so the same design can host all of them.

6. **Signal payloads are uniformly context-free.** Every combat-relevant signal (`BattleSystem.*`, `TimingPrompt.*`) omits attacker/target info from the payload — subscribers rely on singleton state to know who the event relates to. Threading Combatant references through the payloads is a Low-risk mechanical change but worth doing at the same time as the A1 Combatant abstraction lands; otherwise the payload types become another thing to re-do later.

7. **Audio callsite identity is the one category the refactor doesn't mechanically resolve.** Every other finding collapses into per-unit routing once Combatant lands. Audio callsites continue working identically at multi-unit density (they'd still play the right sound for the right event), but lose per-unit specificity. Whether to add positional / per-unit audio is a polish decision the scaffolding exercise should evaluate, not something the refactor forces.

### Total findings

**40 catalogued across 11 categories.**

---

## Non-findings (verified clean)

These were examined and found NOT to contain 1v1 assumptions, documented so the refactor phase knows what's been checked:

- **`TimingPrompt.cs`** — entirely target-agnostic. The prompt knows about input windows, bounce counts, and flash feedback; it does not know about attackers or defenders. Positioning is set by the caller. `ConfirmAll()` operates on a global active-prompts list without any side filtering.
- **`TargetZone.cs`** — draws rings only. Positioning is entirely the caller's concern (BattleTest sets `_targetZone.Position = ComputeCameraMidpoint()`). The node itself has no 1v1 bias.
- **`BattleMessage.cs`**, **`BattleDialogue.cs`** — UI components, no combat semantics.
- **`AttackData.cs`** — data-only resource; has no target concept (correctly — attacks describe *what happens*, target resolution is the caller's job).
- **`EnemyData.cs`** — defines one enemy but the file itself isn't encoding a 1v1 battle shape; it just describes one combatant. Plural `EnemyData[]` is the fix at the field-declaration level (H2).
- **Sound playback helpers** (`PlaySound` in both `BattleTest.cs` and `BattleSystem.cs`) — side-agnostic, just play a stream. No target routing needed at the helper level; callsite identity is K1/K2.
- **Camera shake** (`ShakeCamera`, `_shakeIntensity` etc.) — operates on the single game camera, which stays single even with multi-combat.
- **Background gradient, `MakeLayeredPanel`, `StyleLabel`, `MakeNinePatch`** — UI utilities; no target concept.
- **Dead-flag guard helpers** (`PlayPlayer`, `PlayEnemy`, `StopPlayer`, `SetPlayerFrame`, `PlayPlayerBackwards`) — guarded by the `_playerDead`/`_enemyDead` flags (A7), which are the actual findings. The helpers themselves become per-unit naturally once the flags do.
- **`BattleAnimator.cs` animation-lifecycle code** (sprite setup, frame construction, sprite-local animation callbacks) — animating a specific sprite from its own callback is not a combat targeting concern.

---

## Design questions surfaced by the audit

Several findings reference a "Combatant abstraction" in their resolution sketches without defining what that abstraction actually is. These questions need to be answered **before** the Phase 2 refactor begins — decisions made here shape every other finding's resolution.

Not answered in this audit. Flagged for design discussion.

### 1. What is `Combatant`?
Class? Interface? Struct? Abstract base with Player / Enemy subclasses? The choice affects:
- Whether player-specific fields (MP, absorb library) and enemy-specific fields (AttackPool, LearnableAttack) live on one type or diverge.
- Whether `foreach` over a party is `foreach (Combatant c in party)` or something stricter.
- Whether sharing combat primitives (HP, IsDead, Origin) between sides is ergonomic or coerced.

### 2. Ownership of sprite nodes
Does a `Combatant` own references to its `AnimatedSprite2D` and `ColorRect`, or does something else own them while `Combatant` is pure data? Either works; the choice determines whether `combatant.Play("idle")` is a thing or whether a separate `combatantAnimator[combatant].Play("idle")` lookup is required.

### 3. Architectural home
Where does the party / enemy-list live?
- `List<Combatant> PlayerParty` / `List<Combatant> EnemyParty` as owned fields on `BattleTest`?
- A new `CombatantManager` / `BattleState` class that owns both and hands them to subsystems?
- Distributed — each scene-tree node exposes its Combatant, and a scanner collects them?

This affects how signals carry context (payload passes a Combatant reference vs. an index into a list).

### 4. Party representation
Assuming the answer to #3 is lists: fixed-size arrays sized to max party (4/5) with null slots for empty, or dynamic lists? Fixed-size matches the intended max and avoids nullability in loops; dynamic is more flexible if party size ever varies mid-combat.

### 5. Incremental refactor vs. full abstraction up-front
For the 1v1 prototype, can the refactor land in stages — parameterize-where-it-matters first, introduce the full `Combatant` abstraction later — or does the abstraction need to exist from the start so subsequent work doesn't re-bake new 1v1 assumptions?

Staged risk: new code written before the abstraction lands will re-introduce `_attacker` / `_defender`-style implicit-singleton patterns that need removing later. Up-front risk: the abstraction is designed without real data on how it'll be used at density, so it may need re-designing once the scaffolding exercise surfaces what's actually needed.

The scaffolding exercise (`docs/target-selection-and-scaffolding-plan.md`, Part 2) produces exactly the data needed to answer this — a provisional `Combatant` abstraction can be designed after the exercise runs, rather than before.

### 6. Where does target selection live?
Player-side target selection needs input handling, a pointer visual, and a valid-target-pool resolution. Does this live on `BattleMenu` (as a new sub-state within menu flow), on `BattleTest` (as a new `BattleState.SelectingTarget`), on its own class, or some combination? The audit doesn't prescribe; the implementation will.

### 7. Enemy-side target selection
Enemies currently have attack selection (`AttackSelector`) but not target selection. Does target selection live on `AttackSelector` (extended), on a new `TargetSelector` class, on `EnemyData` as a strategy field, or on the enemy `Combatant` itself?

### 8. Signal payload shape
If we add Combatant references to signal payloads (J1, J2), does every signal carry both attacker and target, or just the subscriber-relevant one? Do we pass `Combatant` directly or an index? This is a minor call, but it's pervasive — making it twice is worse than making it once.

These questions are not independent — answers to #1 and #3 constrain answers to #5 through #8. Worth discussing together, ideally with the scaffolding exercise's output in hand so density concerns inform the data model.
