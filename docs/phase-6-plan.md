# Phase 6 Plan — Multi-Character Scaffold (Revision 2)

## 1. Context

The battle system has been multi-character-ready at the data layer since the
Phase 3–5 arc: `Combatant` is a per-unit plain-C# class, `SequenceContext`
marshals attacker/target through signals, the overlay shader supports per-
combatant independent tween handles, and the unified `PlayAnim(Combatant,
string)` family already routes through `Combatant.AnimSprite`. What stayed 1v1
is the _surface_: a singleton turn alternation (`ShowMenu` ↔ `BeginEnemyAttack`),
hardcoded `_playerParty[0]` / `_enemyParty[0]` sites across 95 call locations,
auto-confirmed target selection, a single pair of hardcoded sprite positions
in `BattleTest.tscn` (440, 630 / 1480, 670), and HP panels that assume one
status card per side.

Phase 6 converts the entire runtime to multi-character machinery driven by an
agility-based turn queue. 1v1 is preserved as an _emergent outcome_ of
`PlayerPartySize = 1, EnemyPartySize = 1` running through the same machinery
— not a separate code path. `TestFullParty = true` flips the roster to 4v8
for density validation. The output is a close-to-shippable 4v8 Phase 1 boss
fight when the flag is on, and a fully-regression-tested 1v1 when the flag
is off. Layout redesign, Beckon target-redirect (per `docs/design-notes.md`
Option A), and the deferred pointer / damage-number / Cure-circle positioning
backlog all land in this phase.

---

## 2. Survey findings

### 2.1 Turn loop and state machine

- `BattleState` enum at [BattleTest.cs:35](../Scripts/Battle/BattleTest.cs):
  `EnemyAttack, PlayerMenu, SelectingTarget, PlayerAttack, GameOver, Victory`.
- Alternation is wired through timer callbacks, not a queue:
  - [BattleTest.cs:1628](../Scripts/Battle/BattleTest.cs)
    `PlayTeardown(() => GetTree().CreateTimer(0.5f).Timeout += ShowMenu)`.
  - [BattleTest.cs:2169](../Scripts/Battle/BattleTest.cs) combo-miss path
    defers to `ShowMenu` directly.
  - `ConfirmMenuSelection` at [BattleMenu.cs:382](../Scripts/Battle/BattleMenu.cs)
    hard-routes Defend → `BeginEnemyAttack()`.
- Sequence-scoped fields on `BattleTest` (declared ~lines 256–259):
  `_sequenceAttacker`, `_sequenceDefender`, `_sequenceAttackerClosePos`,
  `_pendingGameOver`.
- `BuildInitialParties` at
  [BattleTest.cs:2706](../Scripts/Battle/BattleTest.cs) builds exactly one
  player and one enemy Combatant by direct reference to the scene-tree
  singleton nodes `_playerSprite`, `_enemyAnimSprite`, etc.

### 2.2 `_playerParty[0]` / `_enemyParty[0]` hardcoding

- ~95 occurrences across `BattleTest.cs` and `BattleAnimator.cs`.
- Partial semantic-local convention (`var player = _playerParty[0];`) exists
  at some callsites but most still index inline.
- No `foreach` / `for` loops over party lists anywhere in the codebase.

### 2.3 Defend and Beckon

- `Combatant.IsDefending` (bool) at
  [Combatant.cs:48](../Scripts/Battle/Combatant.cs). Set in
  `ConfirmMenuSelection` case 2, cleared in every `ShowMenu` call
  ([BattleMenu.cs:152](../Scripts/Battle/BattleMenu.cs)) and in
  `ApplyPhase2Sprite`. **The current "cleared on every ShowMenu" behaviour
  is only correct at 1v1 by coincidence** — `ShowMenu` fires exactly after
  every enemy turn, so the implicit rule "lasts one enemy turn" holds. At
  multi-unit density this rule silently breaks: a player who Defends on
  their turn would lose IsDefending the moment any other player's ShowMenu
  fires, well before the defender's own next turn. Phase 6 replaces this
  with per-combatant "cleared on that combatant's own next ShowMenu" — see
  C5 notes.
- `Combatant.IsBeckoning` (bool) at
  [Combatant.cs:49](../Scripts/Battle/Combatant.cs). Set in `PerformBeckon`,
  consumed in `SelectEnemyAttack` at
  [BattleTest.cs:2247](../Scripts/Battle/BattleTest.cs) where it forces the
  learnable attack return. Replaced by `BeckoningTarget: Combatant?` in C1.

### 2.4 Target selection (Phase 4)

- [BattleTest.cs:1693–1795](../Scripts/Battle/BattleTest.cs):
  `EnterSelectingTarget`, `ConfirmTargetSelection`, `CancelTargetSelection`,
  `HandleSelectingTargetInput`. `ui_left/ui_right` cycling is stubbed as a
  no-op ([BattleTest.cs:1790–1794](../Scripts/Battle/BattleTest.cs)).
- `IsTargetPoolSingleton(Combatant) => true` at
  [BattleTest.cs:1723](../Scripts/Battle/BattleTest.cs). Auto-confirms every
  selection today.
- `MenuContext` enum tracks return-to routing on cancel (Main / AbsorbedMoves
  / Items).
- `TargetPointer` at [TargetPointer.cs](../Scripts/Battle/TargetPointer.cs) draws
  a pure-code triangle, `SnapTo` uses `target.AnimSprite.GlobalPosition.X`
  with a Y offset derived from `target.PositionRect.Size.Y` — this is the
  ColorRect-vs-visible-sprite mismatch the user flagged. Damage-number origin
  in `ComputeDamageOrigin` ([BattleTest.cs:117–120]) uses hardcoded
  (440, 570) / (1480, 530) coordinates — same root cause.

### 2.5 Phase 5 threat reveal

- `_threatenedCombatants` (List<Combatant>) populated in `BeginEnemyAttack`
  at [BattleTest.cs:1261](../Scripts/Battle/BattleTest.cs). Single entry
  today. `FlashCombatantThreatened` tweens `tint_amount` on that combatant's
  `FlashMaterial`; 0.6s pulse; a `CreateTimer(0.6f)` defers the attack launch
  so tint fades as the attack begins.
- Each Combatant gets its own `ShaderMaterial` instance (per-sprite,
  independent `flash_amount` / `tint_amount` uniforms and independent
  `FlashTween` / `ThreatTween` handles).

### 2.6 Menu and UI

- Main menu: `{ "Attack", "Absorbed Moves", "Defend", "Items" }` at
  [BattleMenu.cs:14](../Scripts/Battle/BattleMenu.cs). No "Beckon" at top
  level — Beckon lives inside the Absorbed Moves submenu
  ([BattleMenu.cs:242](../Scripts/Battle/BattleMenu.cs)).
- `MakeMenuPanel` anchors bottom-left; `PositionMenuPanelsAbovePlayerPanel`
  reads `_playerPanel.Size.Y` to sit just above the player HP panel.
- Player HP panel at bottom-left (260f min width), enemy HP panel at top-
  right (220f min width). `UpdateHPBars` at
  [BattleTest.cs:2421](../Scripts/Battle/BattleTest.cs) writes fill widths
  for exactly one bar per side.
- `BattleTest.tscn` contains hardcoded `PlayerAnimatedSprite` at (440, 630)
  and `EnemyAnimatedSprite` at (1480, 670). Two ColorRect reference nodes.
  No CanvasLayers in the tscn — all panels built at runtime.
- `FloorY = 750f` at [BattleTest.cs:353](../Scripts/Battle/BattleTest.cs).
  Read at player Y ([BattleTest.cs:430]), enemy Y ([BattleTest.cs:444]), and
  `SpawnEffectSprite` positioning.
- `BattleDialogue` and `BattleMessage` are two separate classes with
  duplicated bottom-anchored panel construction. `BattleDialogue` at
  ~(0.2, 0.8) x anchor, 96px above bottom; `BattleMessage` at 0.5 center,
  100px above bottom. CLAUDE.md flags a deferred `BottomCenteredOverlayPanel`
  helper — not started.

### 2.7 Test flags

Priority resolution at
[BattleTest.cs:383–406](../Scripts/Battle/BattleTest.cs): Victory > GameOver
> PhaseTransition. Each conflict logs a `[TEST] X overrides Y` error. Each
active flag prints `[TEST] X active — Y.` to stdout. Intro dialogue skipped
only on Victory/GameOver paths (PhaseTransition keeps intro).

### 2.8 Handler-binding inventory

Handlers bound via `AnimationFinished +=`:

| Site | Sprite | Handler | SafeDisconnect before |
|---|---|---|---|
| BattleTest.cs cast path (~1331) | `_enemyAnimSprite` | `OnCastIntroFinished` | yes |
| BattleAnimator.cs death sites (~785, ~838) | `_enemyAnimSprite` | `OnEnemyDeathFinished` | yes |
| BattleAnimator.cs retreat (~796, ~807) | `_playerAnimSprite` | `OnRetreatFinished` | yes |
| (plus parry, combo-slash, hit, hop-in, cast_end, cast_transition, magic cast) | mixed | mixed | yes |
| BattleSystem:564 | spawned effect sprite | self-disconnecting inline closure | yes |

**Two layers of 1v1-hardcoding in this subsystem:**

1. **Subscription sites** still reach through the singleton sprite fields —
   e.g. `_enemyAnimSprite.AnimationFinished += OnCastIntroFinished` and
   `_playerAnimSprite.AnimationFinished += OnParryFinished`. At multi-unit
   density these must become
   `target.AnimSprite.AnimationFinished += handler` (or routed through a
   helper) so the right sprite's completion fires the right callback.
2. **Handler bodies** call the unified helpers with `_playerParty[0]` /
   `_enemyParty[0]` arguments. No handler body writes
   `_playerAnimSprite.Play(...)` directly for animation changes; direct
   singleton access is limited to the capture-before-Stop frame reads and a
   handful of ZIndex/Material sites documented in CLAUDE.md.

C2 rewrites both layers. See C2 grep checklist.

### 2.9 What is already multi-unit-ready

- `Combatant` class structure (per-unit fields, independent shader material,
  independent tween handles).
- `SequenceContext` (general Attacker/Target refs).
- `PlayAnim` / `StopAnim` / `PlayAnimBackwards` / `SetAnimFrame` /
  `SafeDisconnectAnim` helpers in `BattleAnimator.cs` (route through
  `Combatant.AnimSprite`).
- `TakeDamage` / `Heal` (receiver-only, attacker-agnostic).
- `SpawnEffectSprite` geometry (`attackerOnRight` derived from attacker/
  target X comparison, not hardcoded side).
- `_threatenedCombatants` list scaffold.

### 2.10 What still assumes 1v1 (Phase 6 targets)

- Turn alternation (ShowMenu ↔ BeginEnemyAttack cycle).
- Subscription sites hardcoded to singleton sprite fields.
- Handler bodies' hardcoded `_playerParty[0]` / `_enemyParty[0]` references.
- `BuildInitialParties` constructs singletons only.
- `BattleTest.tscn` has one pre-placed AnimatedSprite2D per side.
- HP panels (one per side).
- `IsTargetPoolSingleton => true`; no cycling; no pointer on confirmed
  single-target attacks.
- `ComputeDamageOrigin` hardcoded to two coordinates.
- Defend cleared on every ShowMenu (correct at 1v1 only by coincidence).
- Victory/GameOver triggers fire on single-unit death. Multi-unit needs
  "all enemies dead" / "all players dead" checks.
- No `Agility` / `IsAbsorber` fields on Combatant.
- Beckon has no target-selection UI and its force-learnable clears
  unconditionally.

---

## 3. Scope

### 3.1 In scope

- **Default config is 1v1; 4v8 is gated on `TestFullParty`**, but both run
  through the same multi-character machinery. There is no separate 1v1
  code path. `PlayerPartySize` and `EnemyPartySize` are
  `[Export] int` properties on BattleTest with defaults `1, 1`. Setting
  `TestFullParty = true` overrides them to `4, 8`. `BuildInitialParties`
  loops those counts to construct the rosters — Knight copies for players,
  Warrior Phase 1 copies for enemies. (4v8 fills the staggered enemy grid
  to all 4 columns × both rows; the prior 4v5 left cols 1 and 3 empty,
  which masked the row/col-Z bug landed in the C7-extra-followup.)
- Absorber identified by explicit `bool IsAbsorber` on Combatant;
  `_playerParty[0].IsAbsorber = true`, others false. Only the Absorber's
  Skills submenu contains absorbed moves. **Only the Absorber's Skills
  submenu contains a Beckon entry** — non-Absorbers do not render the
  Beckon entry at all (not greyed out). Using Beckon requires IsAbsorber
  by construction; no runtime gate is needed because the menu never
  exposes the option to non-Absorbers.
- Skills submenu renamed from "Absorbed Moves". All four players share
  Combo Strike, Magic Comet, Cure. Absorber additionally has Beckon and
  any absorbed moves.
- Agility field on Combatant; all combatants equal agility for Phase 6.
- `TurnOrderQueue` — agility sort with tie-break (players > enemies, then
  party-list order). At `PlayerPartySize=1, EnemyPartySize=1` the queue
  emits P1 E1 P1 E1… matching the current ShowMenu/BeginEnemyAttack
  alternation — the old behaviour is an emergent output of the queue, not
  a retained fallback.
- Queue replaces ShowMenu/BeginEnemyAttack alternation everywhere.
- Per-player Defend persistence: cleared on that combatant's own next
  ShowMenu (or on death). Multiple players may be Defending concurrently.
- Beckon target-redirect via `Combatant.BeckoningTarget: Combatant?`
  (Option A from `docs/design-notes.md`). Replaces `IsBeckoning: bool`.
- Target selection: `IsTargetPoolSingleton` reads live valid-target count;
  `ui_left` / `ui_right` cycles through the pool; pointer visible when
  pool > 1.
- Enemy target selection (when the target is not redirected by Beckon):
  uniform random over alive players. Deterministic rules
  (lowest-HP, frontmost) deferred as real design work.
- Layout redesign: FloorY raise so a middle-bottom slot fits
  dialogue/message, 2-2 staggered player formation, 3-2 staggered enemy
  formation, 4-across bottom player HP strip, 5-across top enemy HP strip,
  battle menu stays fixed bottom-left, active-player identified via
  turn-order strip + HP highlight + subtle sprite tint, active-enemy gets
  a subtle sprite tint.
- Turn-order UI strip (top of screen, stylised name cards, no portraits).
- `BottomCenteredOverlayPanel` helper — structural extraction only.
  Consolidates CanvasLayer setup, anchor config, and panel inset constants
  shared by BattleDialogue and BattleMessage. **No UX rule changes:**
  BattleDialogue remains skippable-on-input, BattleMessage remains
  non-skippable with duration-based dismissal.
- `TestFullParty` test flag (lowest priority). Default `false` → 1v1
  (`PlayerPartySize = 1, EnemyPartySize = 1`). When `true` → 4v8
  (overrides those exports to 4 and 8 in the test-flag resolution block).
- **Phase 2 transition suppression under TestFullParty:** when the flag
  is active, the test-flag resolution block sets `Phase2EnemyData = null`
  and logs `[TEST] TestFullParty suppresses Phase 1 → Phase 2
  transition.` The default 1v1 path retains the fallback load and
  Phase 2 works exactly as today.
- Deferred positioning fixes folded in: pointer (ColorRect → sprite), damage
  numbers (same root cause), Cure target circle (same root cause).

### 3.2 Out of scope (hold the line)

- Phase 1 → Phase 2 transition expanded to multi-unit. At 1v1 default it
  works as today; at 4v8 TestFullParty it is explicitly suppressed.
- Parry counter refactor to route through `BattleSystem.StartSequence`.
- `AttackStep.Offset` / `PlayerOffset` schema consolidation (D5).
- Character-specific move sets (all players share Knight moves).
- Stats beyond HP/MP/Agility (strength, defence, etc.).
- Turn-order UI portraits / art polish.
- Balance tuning passes.
- Friendly-fire exposure (`CanFriendlyFire` opt-in). Architecture permits it;
  no menu option exposes it in Phase 6.
- Promotion to typed-array roster config (`EnemyData[]` per slot). Deferred
  past Phase 6; current config is uniform copies only.

---

## 4. Work breakdown

Commit-sized units, implementation order. Each is a standalone commit under
the pre-commit diff-review workflow (`../claude_review/<name>-review.txt`).

### C1 — Combatant field additions (data-only)

- Add `int Agility = 10` and `bool IsAbsorber` to `Combatant.cs`.
- Add `Combatant BeckoningTarget` (nullable); remove `IsBeckoning` bool.
  Update the one write site (`PerformBeckon`) and the one read site
  (`SelectEnemyAttack`) to operate on `BeckoningTarget != null`. Target
  defaulted to `_enemyParty[0]` for this commit so behaviour is preserved.
  Phase 2 transition cleanup site (`ApplyPhase2Sprite`) updated in the same
  commit (clear `BeckoningTarget` instead of `IsBeckoning`).
- No other behaviour change. `BuildInitialParties` sets
  `_playerParty[0].IsAbsorber = true`, `Agility = 10` for both entries.
- Verification: `dotnet build`, Godot `--headless --quit`, one full fight.

### C2 — Handler refactor: subscription sites + body references

**Subscription sites** — every `_<side>AnimSprite.AnimationFinished +=
handler` in `BattleTest.cs` and `BattleAnimator.cs` becomes
`<combatant>.AnimSprite.AnimationFinished += handler` (or routed through
a helper `ConnectAnim(Combatant, Action)` that mirrors `SafeDisconnectAnim`
semantically). The combatant is sourced from `_sequenceAttacker` /
`_sequenceDefender` at the subscription moment — same rule as the body
refactor.

**Handler bodies** — within every `AnimationFinished` handler body, replace
hardcoded `_playerParty[0]` / `_enemyParty[0]` with reads against
`_sequenceAttacker` / `_sequenceDefender` (via semantic locals like
`var attacker = _sequenceAttacker;`). See §6 for the justification.

**Parry counter edge case:** at `PlayParryCounter` entry, the counter's
attacker is the prior sequence's defender and vice versa. Swap the scope
fields (`_sequenceAttacker = _sequenceDefender; _sequenceDefender =
_sequenceAttacker;` via a local) so subscription sites and handler bodies
alike resolve to the right combatant after the swap.

**Party-scoped handlers** (death-animation ends, game-over / victory
checks that iterate both parties) use explicit party-iteration, not the
sequence-scoped fields.

Still 1v1; this is a pure refactor that makes C5 safe.

**C2 grep checklist — run at start and end of the commit:**

- `_(player|enemy)AnimSprite\.AnimationFinished` — expected zero hits
  post-refactor.
- `_(player|enemy)AnimSprite\.(Play|PlayBackwards|SpeedScale|SpriteFrames)`
  inside handler bodies — expected zero hits post-refactor.
- Direct `_(player|enemy)AnimSprite\.(Frame|Stop|Material|ZIndex)` reads
  inside handler bodies — allowed only for the deliberate exceptions
  documented in CLAUDE.md's "Direct sprite access that bypasses the guards"
  paragraph (capture-before-Stop frame reads, ZIndex writes during reveal
  sequence). If anything else surfaces that isn't already documented, add
  it to the C2 work list and address it.

Verification: `dotnet build`, full fight including parry counter, ether
item use, cure, combo strike. Behaviour must be bit-identical to pre-C2.

### C3 — Party expansion infrastructure (still 1v1 default)

- `[Export] int PlayerPartySize = 1;` and
  `[Export] int EnemyPartySize = 1;` on `BattleTest`.
- Rewrite `BuildInitialParties` to loop each size, spawning additional
  `AnimatedSprite2D` + `ColorRect` nodes at runtime, building frames for
  each, applying per-combatant `ShaderMaterial` instances, and appending
  to `_playerParty` / `_enemyParty`. The existing tscn-placed pair serves
  as slot 0 on each side; additional slots are instantiated in code.
- Each slot gets a positional offset (C6 computes the real staggered
  formation; C3 lays slots out on a simple horizontal line — correct math
  lands in C6). Positions are data-derived, not hardcoded.
- `UpdateHPBars`, `CheckVictory`, `CheckGameOver` iterate both party lists.
  Victory fires when every enemy `IsDead`; GameOver fires when every
  player `IsDead`.
- **Intermediate-state note (deliberate):** C3 makes rosters multi-unit but
  the queue doesn't land until C5. During the C3-through-C5 interval, the
  existing alternation still targets `_playerParty[0]` as the active
  player and `_enemyParty[0]` as the active enemy — other party members
  sit idle on their slots. Enemy attacks only hit player 0, and only
  player 0's menu appears. This is intentional scaffolding, not a bug —
  call it out explicitly in the C3 review note so chat-Claude doesn't
  flag the mismatch. C5 closes the mismatch.
- Sequence-scoped fields still drive the active attacker/defender.

Verification: headless load at `PlayerPartySize = 1, EnemyPartySize = 1`
(default) — 1v1 fight behaves identically. Headless load at
`PlayerPartySize = 4, EnemyPartySize = 8` (via test-flag or manual export
change) — screenshot confirms 4 players and 8 enemies render at sensible
positions; fight continues to target slot-0-only (intermediate behaviour).

### C4 — `TestFullParty` flag

- `[Export] bool TestFullParty = false;` on `BattleTest`.
- Priority resolution extended in the existing test-flag block: Victory >
  GameOver > PhaseTransition > FullParty. Conflicts log
  `[TEST] Victory/GameOver/PhaseTransition overrides TestFullParty`.
- When active: set `PlayerPartySize = 4, EnemyPartySize = 8`, set
  `Phase2EnemyData = null`, and log both
  `[TEST] TestFullParty active — 4 players vs 8 enemies.` and
  `[TEST] TestFullParty suppresses Phase 1 → Phase 2 transition.`
- The Phase 2 suppression is essential — without it, the first Warrior
  death would trigger the transition logic, which assumes exactly one
  enemy and would corrupt state at 7 remaining live enemies.
- Verification: toggle flag, confirm 1v1 and 4v8 both load. Verify Phase 2
  transition works at 1v1 default and is suppressed at 4v8.

### C4.5 — Menu restructure for multi-character Skills

- Main menu at [BattleMenu.cs:14] becomes
  `{ "Attack", "Skills", "Defend", "Items" }`. Rename only — same
  dispatch slot (index 1).
- Skills submenu (renamed from "Absorbed Moves", was built at
  [BattleMenu.cs:242]) base entries become
  `{ "Combo Strike", "Magic Comet", "Cure", "Back" }` for all players.
  Note: "Comet" in the current code becomes "Magic Comet" for clarity.
  Keep current entry if you prefer — cosmetic, but call out in review.
- **Absorber-only conditional entries:** when the active player
  `.IsAbsorber` is true, the submenu additionally includes a "Beckon"
  entry and any absorbed moves (from `_absorbedMoves`). For
  non-Absorbers, Beckon does not render at all — not greyed-out, not
  present. Use the existing `RebuildSubMenu` pattern; the rebuild is now
  parameterised by the active player.
- This commit still runs on the pre-queue alternation (C5 hasn't landed
  yet), so "active player" is still `_playerParty[0]`. The menu
  conditional has no visible effect at 1v1 default (slot 0 is always the
  Absorber) but becomes observable at 4v8 TestFullParty once the queue
  rotates the active player.
- `InitSubMenuData` / `PopulateSubMenuPanel` restructured to rebuild per-
  active-player rather than at BuildMenu time. The existing rebuild-on-
  absorption pattern generalises to rebuild-on-active-player-change.

Verification: Structural-only verification for this commit: `dotnet build`
and Godot `--headless --quit` scene load. The Beckon-only-for-Absorber
rendering is not observable until C5 provides active-player rotation;
observable verification is folded into C5's acceptance.

### C5 — Turn-order queue

- New `TurnOrderQueue` class. Computes a round's ordering from the two
  party lists by stable sort on `-Agility`, tie-break `Side == Player ? 0
  : 1` then party-list index. `Advance()` pops next alive combatant;
  `Rebuild()` recomputes each round. Dead combatants are skipped during
  advance.
- `BattleTest._queue` field, populated at `_Ready` after
  `BuildInitialParties`.
- Replace legacy alternation:
  - `ShowMenu()` / `BeginEnemyAttack()` become the two branches of a new
    `AdvanceTurn()` method that reads `_queue.Current()`. Player →
    `ShowMenu` with `_activePlayer` set; enemy → `BeginEnemyAttack` with
    the active enemy passed in.
  - At every sequence-completion site (OnPlayerPromptCompleted,
    OnEnemySequenceCompleted, parry counter teardown, combo-miss,
    Beckon, Defend, ItemUse, Victory/GameOver branches), replace the
    current `ShowMenu` or `BeginEnemyAttack` invocation with
    `_queue.Advance(); AdvanceTurn();`.
- `BeginEnemyAttack` / `ExecuteEnemyAttack` accept the active enemy
  Combatant as parameter — derived from the queue, not `_enemyParty[0]`.
- `ShowMenu` sources `_activePlayer` from the queue. All menu paths
  (ConfirmMenuSelection, ConfirmSubMenuSelection, ConfirmItemMenuSelection)
  use `_activePlayer` instead of `_playerParty[0]`. C4.5's rebuild is
  triggered by the `_activePlayer` change.
- **Defend semantics:** each player's `Combatant.IsDefending` persists
  from the turn they pressed Defend until their own next `ShowMenu`.
  Multiple players can be Defending simultaneously. Enemy attack miss
  path checks the specific target's `IsDefending`. `IsDefending` is
  cleared in `ShowMenu` only when `_activePlayer` matches the defender,
  and is cleared on death by the death-handler branch.
- **Enemy target selection (uniform random):** new helper
  `SelectEnemyTarget(Combatant attacker)` — if any player's
  `BeckoningTarget == attacker`, return that player (C10 lands the full
  BeckoningTarget plumbing; C5 ships the helper as `null`-tolerant so
  C10 is a drop-in). Otherwise pick a uniform-random alive player. The
  resolved target populates `_threatenedCombatants` and becomes the
  sequence's defender.
- **1v1 correctness test:** at `PlayerPartySize = 1, EnemyPartySize = 1`
  the queue emits P1 E1 P1 E1… — the old alternation is an emergent
  output of the queue, not a retained fallback path. This is the primary
  regression gate for the commit.

Verification: headless load + forced 4v8 (C4 flag on), log queue state
per turn, run through ~2 full rounds observing P1 P2 P3 P4 E1 E2 E3 E4
E5 E6 E7 E8 order. Separately, headless load at default (1v1) — full fight runs
bit-identical to pre-queue behaviour.

### C6 — Layout redesign

- Raise combatants: new `FloorY = 650f` (was 750f — frees ~100px below
  for dialogue/message slot). Per-side Y offset tunables kept for visual
  fine-tuning.
- Staggered formations (4v8 case; 1v1 case uses slot 0 of each side):
  - Players: 2 front (Y = FloorY) at X = 380, 560; 2 back (Y = FloorY -
    60) at X = 290, 470. Depth-order via ZIndex.
  - Enemies: 3 front (Y = FloorY) at X = 1160, 1320, 1480; 2 back
    (Y = FloorY - 70) at X = 1240, 1400. ZIndex layering.
  - At 1v1 default, slot 0 on each side sits at a central position that
    reads well solo (approximately the current position relative to the
    new FloorY). Tune during implementation.
- Bottom HP strip: 4 player HP/MP mini-panels horizontal across bottom
  (only `PlayerPartySize` of them visible — at 1v1 only slot 0).
- Top HP strip: 5 enemy HP mini-panels horizontal across top (only
  `EnemyPartySize` visible — at 1v1 only slot 0).
- Battle menu stays fixed bottom-left.
  `PositionMenuPanelsAbovePlayerPanel` is no longer valid (no single
  `_playerPanel`); replace with a fixed-Y offset against the bottom HP
  strip's top edge.
- `BottomCenteredOverlayPanel` helper: extracted from the duplicated
  `BattleDialogue` / `BattleMessage` construction — shared CanvasLayer
  setup, shared anchor config, shared panel inset constants. Each
  component retains its own skip/auto-dismiss UX rule. No behaviour
  changes to either component.
- Dialogue and message overlay fit the new middle-bottom slot between
  combatants and the bottom HP strip.

Verification: visual inspection via Godot editor + headless load with
screenshots confirming the formation at both 1v1 and 4v8. Intro
dialogue runs at both configs; menu appears below the dialogue slot.

### C7 — Turn-order strip UI

- New `TurnOrderStrip` CanvasLayer built in `BattleTest._Ready` after
  the queue lands.
- Horizontal strip at top of screen, each slot a stylised
  `MakeLayeredPanel` mini-card with a name label and a side-coded border
  colour. No portraits.
- Current-turn slot visually distinct (brighter modulate + slight scale
  up, or different panel asset — pick one simple approach during
  implementation).
- Strip repopulates at `_queue.Rebuild()` (each round) and refreshes
  current-turn highlight at each `_queue.Advance()`.
- At 1v1 the strip shows two slots alternating — still renders, not
  hidden.

Verification: visual inspection at both 1v1 and 4v8. Log shows current-
turn index aligns with queue state.

### C7-extra — Combat sprite layout cleanup

This chunk was identified during C7 interactive verification — the
strip lookahead surfaced the underlying multi-character sprite layout
as untenable at 4v5 density (sprites trailing off-screen with the
`PlayerSlotSpacing=140` / `EnemySlotSpacing=160` single-row math from
the C3 scaffolding). Inserted as a C8 prerequisite — sprites must be
visible to be highlighted.

- FF-style mirrored diagonal columns. Player diagonal slopes ↘ (slot 0
  at top-right, "leader confronting"; slots 1-3 step down-left at
  partySize=4). Player anchors: legacy slot-0 tscn position is the
  front anchor (preserves 1v1 bit-identical visual); linear
  interpolation to a back anchor for slots 1..N-1. Player party caps
  at 4 in Phase 6 scope — no two-row extension needed on the player
  side.
- Constant scale `(3, 3)` across all slots — no depth scaling. Hop-in
  tweens stay pure-translate (critical for animation simplicity at
  multi-character density).
- Enemy formation (post-redesign — staggered two-row diagonal grid,
  follow-up commit): two parallel down-and-right diagonals. Front row
  is anchored at slot 0's runtime position (per-EnemyData: Warrior
  `(1480, 606)`, Phase-2 boss `(1480, 592)`); each subsequent column
  in either row steps `+80 X, +24 Y`. Back row sits BELOW (`+96 Y`)
  and to the LEFT (`-40 X`) of the corresponding front-row column —
  reads as a "second wave below" depth-staggered formation, not
  "depth-receding behind." Slot index → `(row, col)` via
  `EnemySlotToGridPosition` lookup; pattern alternates front/back and
  fills outer columns (col 0, col 2) before inner (col 1) before
  far-outer (col 3). At 4 enemies, slots fill `FC0/BC0/FC2/BC2`
  leaving cols 1 and 3 empty — reads as a "pinch" formation around
  the inner column. Slot 0 at `(row=0, col=0)` returns the slot 0
  runtime position unchanged → 1v1 bit-identical to ship state by
  construction. No depth-sort / Z-index changes in this pass —
  Godot's `CANVAS_ITEM_Z_MAX ≈ 4096` rules out a naive Y-tied
  scheme; if interactive review surfaces back-row occlusion issues
  they're handled in a separate follow-up commit. Per-encounter
  overrides remain out of scope.
- (Original C7-extra shipped enemies as a single diagonal column with
  linear-Lerp + back-column extension at 6-8 enemies. The 4v5 default
  cramped sprites at 80-px slot spacing vs 390-px Warrior sprite
  width; the staggered two-row grid above replaces it.)
- HP/MP panels at bottom-center reorder so slot 0 sits at the
  RIGHTMOST panel position via `(partySize - 1 - slotIndex)`
  inversion. Spatial correlation: damage on sprite → eye drops →
  matching panel directly below.
- Position-consuming code (`PlayHopIn`, `PlayTeardown`,
  `ComputeClosePosition`, `ComputeSlamPosition`,
  `ComputeCameraMidpoint`, `ComputeDamageOrigin`,
  `BattleSystem.SpawnEffectSprite`) all read per-combatant
  `Origin` / `AnimSpriteOrigin`. New diagonal positions flow through
  automatically; no migration needed at consumer sites.
- Per-encounter override system — out of scope. JRPG genre convention
  is hand-placed positions per encounter for set-piece fights;
  C7-extra ships only a universal default formula. A future override
  mechanism (e.g. via `EnemyData` per-slot offsets) is post-Phase-6
  work.
- Magic timing-circle off-axis (already C11 scope): diagonal layout
  amplifies the `ComputeCameraMidpoint`-vs-sprite-positions mismatch
  for cross-formation magic attacks. Flagged for C11 priority.

Verification: visual at 1v1 (single Knight + Warrior at exact legacy
positions, panel at legacy left-anchored slot-0 position) and 4v8
(TestFullParty=true; staggered grid visible with both rows × all four
columns populated, all 12 sprites in-frame, 4 player panels reordered
so leader = rightmost; slot 7 right-edge clip acceptable). 4v8 (rather
than the prior 4v5) is the verification target because it fills cols 1
and 3 — needed to exercise the row/col-derived enemy Z (C7-extra-
followup).

### C7-extra follow-up — Per-slot Z-index for combatant depth-sort

Identified after the staggered two-row diagonal grid landed: at
multi-character density, back-row sprites overlapping front-row
sprites in the same column inherited only the scene-tree spawn order
for render priority, producing the wrong "back wave on top of front
wave" occlusion. Bounded slot-derived Z values fix the depth-sort
without risking the `CANVAS_ITEM_Z_MAX (~4096)` overflow the prior
Y-tied attempt hit. As a consequence, `TestFullParty` is bumped from
4v5 to 4v8 — the prior 4v5 left enemy cols 1 and 3 empty, masking the
class of bug this commit fixes.

- **Formation Z values are spaced 2 apart** so the hop-in attacker's
  `defender.Z + 1` bump always lands at an odd Z that's unique by
  construction — strictly between formation members on either side.
  Without the spacing, an enemy attacker bumped to Z=1 would tie with
  player slot 1 at Z=1, and scene-tree order (enemies added after
  players in `BuildInitialParties`) would let the enemy win
  regardless of attacker side. Captured in interactive verification:
  enemy Warrior 3 attacking Knight slot 0 was bumped to the wrong
  side of a Z-tie under the prior unit-spaced scheme.
- **Player Z = `slotIndex * 2`.** Player formation is a single ↘
  diagonal column where Y is monotonically increasing in slot index,
  so slot index already matches Y rank.
- **Enemy Z = `(row * 4 + col) * 2`** read from
  `EnemySlotToGridPosition`. The slot fill pattern
  (`FC0/BC0/FC2/BC2/FC1/BC1/FC3/BC3` — outer-cols-first, alternating
  front/back) is deliberately non-monotonic in Y, so slot index ≠ Y
  rank. `(row*4+col)` maps to Y rank: front row 0..3 (FC0..FC3
  ascending Y) then back row 4..7 (BC0..BC3 ascending Y, all behind
  the front row). Slot 0 still gets Z=0 (FC0 = (0,0)*2 = 0),
  preserving 1v1 bit-identity. Both sides' assignments live in
  `BuildInitialParties` after the rect/sprite pair is resolved
  (covers tscn-placed slot 0 and dynamically spawned slots through
  one site each).
- **Hop-in attacker takes `defender.Z + 1`.** "Joins the defender's
  row" depth band, lands at an odd Z slot uniquely free under the
  2-apart spacing — guarantees the attacker renders strictly in front
  of the defender AND of any same-side or opposing combatant whose
  formation Z would otherwise tie. Pre-bump Z snapshotted into a new
  `_attackerZIndexBeforeHopIn` sentinel field (-1 = no active
  snapshot) at `PlayHopIn` start; restored at `PlayTeardown`'s
  `tween.Finished`. `IsDead` check on restore preserves the Phase 1 →
  Phase 2 reveal contract (dead Phase 1 warrior keeps its
  `SpawnBossReveal`-bumped Z until `SwapToPhase2`'s own snapshot
  pattern restores it). Sentinel cleared unconditionally so a leak
  cannot persist into the next sequence even when restore is skipped.
- The previous unconditional `defender.AnimSprite.ZIndex = 0` in
  `PlayHopIn` is dropped — at 1v1 it was already a no-op; at
  multi-character density it was a latent clobber of the defender's
  slot Z. Defender Z is left untouched throughout the sequence.
- `SpawnEffectSprite` (BattleSystem) now reads
  `target.AnimSprite.ZIndex` instead of the hardcoded `Z = 3`.
  Effects "join the row" of their target visually. Tree order keeps
  the effect rendering on top of the target sprite at equal Z (effect
  added later). Phase 2 reveal sequence is unaffected — no
  `SpawnEffectSprite` calls fire during the reveal, so the legacy
  reveal-layer Z values (reveal = 1, warrior bumped = 2) keep their
  hardcoded constants in `BattleAnimator.SpawnBossReveal`.
- Damage numbers (Z = 100 design lock) are not implemented in the
  current codebase; this constraint is reserved for a future commit
  rather than shipped here.
- Hit flashes are shader-on-sprite — they follow the sprite's Z
  naturally, no separate handling.
- **TestFullParty roster bump (4v5 → 4v8) folded into this commit.**
  Verification is meaningless at 4v5 because cols 1 and 3 stay empty
  — slot 4 (FC1) and slot 6 (FC3) don't exist, so the Y-rank-vs-slot-
  index divergence that produces the bug never manifests. Aligning
  the in-source flag with the verification density keeps future
  regression checks from missing the same class of bug.

Verification: visual 1v1 (no observable change — slot 0 Z=0, hop-in
attacker at defender.Z + 1 = 1 matches pre-refactor explicit `+1`
bump, effects at Z=0 with tree-order fallback) and 4v8 with
`TestFullParty=true` (front-row sprites depth-sort by ascending Y
within the row, back-row sprites depth-sort by ascending Y within the
back row, all back-row sprites render in front of all front-row
sprites in overlap; hop-in attacker visibly joins the target's row
and renders strictly in front of every formation member that would
overlap during the close-stance regardless of which side it belongs
to; effects align with target's depth band).

`CANVAS_ITEM_Z_MAX (~4096)` headroom: max formation Z at 4v8 is
2*7 = 14 (back-row enemy slot 7 = BC3 = (1*4+3)*2). Hop-in attacker
peaks at 15. Three orders of magnitude of headroom remain — the
2-apart spacing scales freely if the formation grows in either party
size or grid depth.

### C8 — Active-combatant indicators (3 sub-features bundled)

After C7 / C7-extra-followup landed, the multi-character formation
read correctly geometrically but lacked clear "whose turn is it"
signalling. C8 bundles three UX cues that share `AdvanceTurn`'s
outgoing→incoming lifecycle hub.

A fourth sub-feature was attempted and reverted during interactive
verification: a chrome-only panel modulate scheme that excluded the
HP/MP bars from the active highlight. Motivation was to prevent the
bars from being dulled when the active modulate cascaded through the
panel subtree. The implementation worked technically (split the
modulate target between the panel root for dead state and the fill +
border NinePatchRects for active state), but the visual tradeoff was
worse than the original problem: the brighter strip-style differentiation
that the chrome split implicitly enabled (1.8 boost vs 0.7 dimmed
non-active) made non-active panels read as gray, more distracting
than the bar-dulling it solved. The slide-up cue (sub-feature 2) and
the active sprite tint (sub-feature 3) carry the active-panel
identification clearly enough on their own. Reverted to the pre-C8
single-target scheme: panel root takes both active boost (1.5) and
dead grayout, with non-active panels at identity white. Strip
constants kept separate (`StripActiveModulate` / `StripAliveModulate`)
so sub-feature 1's stronger differentiation applies only to cards.

**Sub-feature 1 — Turn-strip differentiation.** Strip cards use
their own modulate constants (`StripActiveModulate = (1.8, 1.8, 1.4, 1.0)`,
`StripAliveModulate = (0.7, 0.7, 0.7, 1.0)`) — separate from the
panel constants so the dimmed-vs-boosted scheme applies only to the
strip. Active brightens AND non-active dims gives a ~2.6× ratio
between current and next-up cards, readable at a glance without
side-by-side comparison.

**Sub-feature 2 — Active player panel slide-up.** Active player's
panel slides up by 15% of its height (`PanelSlideRatio = 0.15f`,
~12px on a typical 80px panel) over 0.10s ease-out at turn-start;
slides back to its captured `RestingOffsetBottom` at turn-end. Per-
panel `RestingOffsetBottom` snapshotted at build time and read on
every slide-back call (rather than tracking deltas) — guarantees
no Y-drift across many turn cycles even with mid-flight tween
interruption. Player-only (enemy panels don't slide).

**Sub-feature 3 — Active sprite tint (player-only).** New
`active_amount` uniform on `CombatantOverlay.gdshader` (alongside
the existing `flash_amount` and `tint_amount`). Subtle white wash
applied to the active player's sprite during their turn so the
acting character reads at a glance. No pointer above the sprite;
pointers are reserved for target selection (yellow). Self-targeting
reuses the existing yellow target pointer on the active player's
sprite — orthogonal to the active tint, both can co-exist. Per-
combatant `Combatant.ActiveTween` mirrors `FlashTween` /
`ThreatTween` so simultaneous fades don't stomp. `ActiveTintAmount
= 0.15f` is the effective shader value — softness lives in the
constant, not in an in-shader multiplier, so visual tuning is
one-place. `ActiveTintFadeSec = 0.10f` matches `PanelSlideDur` so
all turn-transition cues land together.

The original C8 design applied the tint to enemies too on the
rationale that 4v8 needed a way to identify the active warrior.
Reverted during interactive verification: the white wash was
visually noisy alongside the threat-reveal red on the targeted
player + the warrior windup pose + the camera shake. Strip-card
differentiation (sub-feature 1) and threat reveal (Phase 5) carry
the active-enemy signal sufficiently. Both
`ApplyActiveSpriteTint` and `ClearActiveSpriteTint` gate
internally on `Side == Player`; enemy combatants pass through the
helpers harmlessly so the lifecycle hub remains symmetric.

**Lifecycle hubs.** Two complementary entry points:

- **Action-commit (player primary)** — `ConfirmTargetSelection`
  fires `ClearActiveSpriteTint(_activePlayer)` and
  `SlidePlayerPanelDown(_activePlayer)` before invoking the
  pending launcher. Synchronises with the panel modulate clear
  that `UpdateHPBars` (called immediately after the launcher
  returns) picks up once `_state` has transitioned out of
  `{PlayerMenu, SelectingTarget}`. Covers Attack / Combo Strike /
  Magic / Cure / Items (Ether) / Beckon — every action routed
  through SelectingTarget.
- **AdvanceTurn (turn-end backup + Defend)** — captures
  `outgoing = _queue.Current` BEFORE `_queue.Advance()`, fires
  the same turn-end hooks. Primary hook for Defend (which
  bypasses SelectingTarget and goes Menu → HideMenu() +
  AdvanceTurn directly). Defensive backup for SelectingTarget
  paths so any future code path that skips
  `ConfirmTargetSelection` still gets the clear. Helpers are
  idempotent (already-cleared = 0→0 fade; already-at-rest =
  no-op tween) so the double-fire costs nothing on
  SelectingTarget paths. Turn-start hooks
  (`ApplyActiveSpriteTint`, `SlidePlayerPanelUp`) fire here on
  the incoming actor — the only entry point for those.

This dual-path arrangement keeps player turn-end visual cues
synchronised with the panel-modulate clear at action-commit
(where the player perceives the action beginning), rather than
delayed until the action-completion AdvanceTurn fires ~0.5s
later.

**Shader stacking.** Composition order in
`CombatantOverlay.gdshader`: tint (red threat) → active (white
wash) → flash (white pulse). Flash overrides everything (matches
prior behaviour); active composes on top of tint and may slightly
lighten the threat red when both fire on the same sprite (rare —
threat fires on the enemy's target, active fires on the active
actor; same-sprite stack happens only if the active actor is also
the threat target, which the queue invariant prevents in steady
state). Documented fallback: gate active on `(1.0 - tint_amount)`
if interactive verification at 4v8 surfaces a washout — keeps the
red dominant during a learnable wind-up at the cost of one extra
shader op. Apply only if needed.

**Phase 2 transition cleanup.** Extended `SwapToPhase2` to clear
shader uniforms (`active_amount`, `flash_amount`, `tint_amount`)
and per-combatant tween handles (`ActiveTween`, `FlashTween`,
`ThreatTween`) on the slot-0 enemy. The `FlashMaterial` reference
persists across the sprite swap (same AnimatedSprite2D node, just
new `SpriteFrames` per `ApplyPhase2Sprite`), so without explicit
clearing the Phase 2 boss inherits stale uniform values. Closes a
pre-existing latent issue made observable by C8's longer-lived
active tint.

**Verification:**

- Headless 1v1 (default) and 4v8 (`TestFullParty=true`) load clean.
- Interactive 1v1: panel slides up smoothly at turn start, sprite
  tint visible on the lone player and lone enemy on their respective
  turns. Strip's two-card alternation reads with the new
  differentiation. Panel returns to exact resting Y across many
  turns (no drift).
- Interactive 4v8: strip top card obviously distinct from the
  second card at a glance. Active-player panel slide-up identifies
  which of 4 panels is acting. Active
  sprite tint readable on each combatant in formation rotation —
  previous fades out as next fades in (synchronised 0.10s).
  Threat-red on player target during enemy turn coexists with
  active-tint on the active enemy (different sprites; no shader
  conflict). Learnable enemy turn: enemy gets active-tint
  (sustained subtle white) + learnable-flash (pulse); pulse pops
  over the sustained tint visibly.
- Phase 1 → Phase 2 transition: warrior dies with active state set;
  `SwapToPhase2` clears it; new boss starts clean.
- Edge cases observed during interactive verification:
  - Game over flow (overlay vs scene replacement) — confirm stale
    state isn't visible underneath the overlay; if it is, add
    `ClearActiveSpriteTint` in `CheckGameOver` before its early
    return.
  - Player-dies-on-own-turn — dead player's panel sits at slid-up
    position with dead-modulate during death animation before
    `AdvanceTurn` brings it back; confirm transient reads
    acceptably or fire `SlidePlayerPanelDown` on death detection
    rather than waiting for AdvanceTurn.

### C9 — Target-pool expansion and cycling

- `EnterSelectingTarget` signature takes an explicit `CombatantSide
  targetSide` hint instead of a `Combatant defaultTarget`. The five
  menu callsites in `BattleMenu.cs` (Attack, Combo Strike, Magic /
  Cure, Beckon, Ether) pass the side directly: offensive attacks pass
  `CombatantSide.Enemy`; Cure and Ether pass `CombatantSide.Player`.
  Reuses the existing `CombatantSide` enum from `Combatant.cs` rather
  than introducing a parallel `TargetSide` enum.
- New helper `GetTargetPool(CombatantSide side)` returns alive
  combatants on the named side, sorted ascending by
  `AnimSpriteOrigin.Y` then `AnimSpriteOrigin.X` — top-to-bottom in
  formation, tie-breaking left-to-right within a row. AnimSpriteOrigin
  is the snapshotted post-floor-anchor sprite center; during
  SelectingTarget no sprite tween is active (hop-in fires after
  confirm), so this matches live screen position.
- `EnterSelectingTarget` builds the pool once at entry and caches it
  into `_targetPool`/`_targetPoolIndex`. Default starting target is
  pool index 0 (topmost, leftmost on tie) — not the menu-supplied
  default, not slot 0, not last-selected. Per-actor / cross-battle
  target memory is deferred.
- `IsTargetPoolSingleton` is removed; the named method is no longer
  needed. Auto-confirm fires on `_targetPool.Count == 1` directly in
  `EnterSelectingTarget`. Empty-pool case (structurally unreachable
  today — Victory / GameOver fire before all-dead-side menu entry) is
  defended with a `PrintErr` + `CancelTargetSelection`.
- `HandleSelectingTargetInput` wires `ui_left` / `ui_right` to cycle
  the cursor through `_targetPool` with wraparound. Cycling only
  mutates pool cursor + `_selectedTarget` + pointer position;
  attack-identity state (`_isComboAttack`, `_activeMagicAttack`) is
  left untouched so the launcher closure captured at menu-pick time
  remains valid.
- Cure-on-allies is in scope: heal attacks pass
  `CombatantSide.Player`, so the pool includes all alive players
  (including the active player). At 1v1 default the pool is
  singleton and Cure auto-confirms onto the active player as today.
- Friendly-fire damage exposure (`AttackData.CanFriendlyFire`) and
  Beckon target-redirect on the enemy turn remain out of scope (the
  latter lands in C10). C9's Beckon picker just selects which enemy
  populates `_activePlayer.BeckoningTarget`.
- Single-resolved-target launcher contract preserved:
  `ConfirmTargetSelection` still reads `_selectedTarget` and invokes
  the captured launcher; no launcher-side changes.

Verification: at 1v1 default every menu action auto-confirms (pool
size 1 for both sides) and the pointer never renders — bit-identical
to pre-C9 surface. At 4v8 (`TestFullParty=true`) Attack /
Combo Strike / Beckon over 8 enemies, Cure / Ether over 4 players —
pointer renders on the topmost combatant (leftmost on tie),
ui_left/ui_right cycles by Y rank with wraparound, dead combatants
filtered from the pool, cancel routes
back to the originating menu. Edge cases: pool of 1 at end of fight
auto-confirms; cycling never lands on a dead sprite.

**C9 follow-up bundle** — three bugs surfaced during interactive
verification ship in the same commit:

- **Offensive damage routing**: `OnPlayerPromptCompleted`,
  `OnPlayerMagicPassEvaluated` offensive branch, and
  `OnAttackPassEvaluated` combo per-pass damage all read
  `_sequenceDefender` instead of hardcoded `_enemyParty[0]`.
  Three single-line edits; the heal branch already used
  `_sequenceDefender` per Phase 3.6.
- **Per-target death handling**: new `KillCombatant(Combatant)`
  helper bundles `IsDead` flag, death sound, death animation,
  strip fade (player) / Phase 2 reveal scheduling (enemy,
  internally gated), and Beckon-target cleanup. Wired at all 5
  TakeDamage sites: basic, magic, combo per-pass, enemy attack on
  player, parry counter. Game-over branches in
  `BeginComboMissRetreat`, `OnFinalSlashFinished`,
  `OnEnemySequenceCompleted`, `OnPlayerMagicSequenceCompleted`,
  and `OnEtherSequenceCompleted` simplified — kill work moves
  into KillCombatant; branches retain only retreat / idle /
  ShowEndLabel work. Mid-sequence cancellation: combo introduces
  `_comboTargetDied` flag mirroring `_comboMissed` semantics;
  multi-circle offensive magic guards on dead-defender at the top
  of `OnPlayerMagicPassEvaluated`. `TurnOrderQueue.Advance`
  already skips IsDead internally — no queue-side change needed.
- **TargetPointer Y offset**: `SnapTo` reads
  `FrameHeight * AnimSpriteScale.Y * HeadOffsetMultiplier` instead
  of the ColorRect-derived `PositionRect.Size.Y` basis. Multiplier
  retuned from 0.55 to 0.5 against the larger basis.

**Post-C9 cycling-direction fix** (separate commit): the C9 bundle
shipped a single Y-asc / X-asc sort comparator for both sides.
Player picker (Cure / Ether) cycling at 4v8 felt reversed because
the player single-↘-diagonal traverses Y-asc from top-right to
bottom-left — `ui_right` moved the pointer leftward in screen
space. Fix: `GetTargetPool` branches on `CombatantSide`. Players
sort X-primary / Y-tie-break (matches the diagonal's dominant
horizontal span); enemies keep Y-primary / X-tie-break (matches
the two-row grid). Conceptual framing: each side's primary sort
axis matches its dominant visual axis. Single-file fix, ~20 LOC
in `GetTargetPool`'s body.

### C10 — Beckon target-redirect (Option A, Absorber-only)

The mechanical wiring shipped silently in commit `1da4e3b` (the C9
bundle) alongside the multi-target picker — both touched the same
enemy target-selection / threat-reveal pipeline, so they landed
together rather than as a separate C10 commit. The C10 commit covers
only the cleanup work surfaced by post-bundle survey.

**Wiring shipped in `1da4e3b` (the C9 bundle):**

- `PerformBeckon` routes through `SelectingTarget` with
  `MenuContext.Skills`. Beckon menu entry is Absorber-only via the
  C4.5 submenu rebuild + a defense-in-depth `IsAbsorber` reject in
  `PerformBeckon` itself.
- On target confirm: `_activePlayer.BeckoningTarget = chosenEnemy`,
  MP deducted (`BeckonMpCost = 10`), turn advances via
  `AdvanceTurn()`.
- `SelectEnemyAttack(enemy)` scans `_playerParty` for any `p` with
  `p.BeckoningTarget == enemy`. On match: clears `p.BeckoningTarget`
  and returns `EnemyData.LearnableAttack` if non-null; otherwise
  breaks out and falls through to natural attack-pool selection
  (graceful no-op for enemies without a defined learnable).
- `SelectEnemyTarget(enemy)` does the same scan read-only — returns
  the matching beckoner if found, otherwise uniform-random over
  alive players.
- Single-clear invariant: `SelectEnemyTarget` is read-only;
  `SelectEnemyAttack` is the sole consumption site. The
  `BeginEnemyAttack` flow at [BattleTest.cs:1540-1550] resolves
  defender first then attack so the redirect scan and force-
  learnable scan see the same `BeckoningTarget` value.
- `_threatenedCombatants` populated post-redirect, so the red-tint
  flash fires on the Beckoner.
- `KillCombatant` defensively nulls any `p.BeckoningTarget == dying`
  so a dead Beckoned enemy doesn't leave a stale reference.

**This commit (C10):**

- `ShowLearnableSignal` ("If I watch carefully…" text + sound) gates
  on `playerDefender.IsAbsorber`. The text is the Absorber's
  introspective perception cue; non-Absorbers have no learning
  channel. `FlashCombatantWhite(enemyAttacker)` stays unconditional —
  it's the enemy's signature visual identity for the move,
  independent of who's targeted.
- `Combatant.BeckoningTarget` doc-comment refreshed (was stale,
  referenced "defaults to `_enemyParty[0]` until C10 wires proper
  target selection").
- First interactive 4v8 verification of the Beckon redirect path
  end-to-end. The wiring existed in HEAD post-`1da4e3b` but hadn't
  been exercised under interactive multi-character density.

Verification: 4v8 TestFullParty with P1 as Absorber. P1 beckons E3.
E3's turn: uniform-random enemy target selection would normally pick
a random player; Beckon redirect overrides to P1. Threat-reveal
fires on P1, white-flash fires on E3, attack is the learnable.
"If I watch carefully…" fires (target IS Absorber). Absorb on
perfect parry → learnable added to P1's Skills submenu. Negative
case: enemy uses learnable while targeting non-Absorber player —
white flash fires, but text does not (the new gate's observable
effect). Non-Absorbers (P2–P4) never see a Beckon entry in their
submenus.

**Post-C10 enhancement — multi-Beckon stacking** (separate commit):
the C10 verification surfaced an edge case where stacking Beckons
silently overwrote the prior pending Beckon — `BeckoningTarget`
was a single `Combatant` reference. Refactor: field renamed to
`BeckoningTargets` and typed `HashSet<Combatant>`. Multiple
concurrent Beckons supported; each beckoned enemy's force-learnable
fires when its turn arrives. The Beckon picker excludes enemies
already in the active player's set (via an optional `include`
predicate threaded through `GetTargetPool` and `EnterSelectingTarget`).
All read/write/clear sites updated: `PerformBeckon` (Add),
`SelectEnemyAttack` (Contains + Remove on match — single-clear
preserved per-enemy), `SelectEnemyTarget` (Contains, read-only),
`KillCombatant` (Remove — no-op on absent), `SwapToPhase2` (Clear).

### C11 — Positioning fixes (pointer / damage numbers / Cure circle)

**Split into sub-chunks** (post-Phase-6-arc shippability decision):
- **C11.1** — Target highlight + pointer gated off. Yellow per-sprite
  highlight (`target_amount` on `CombatantOverlay.gdshader`) becomes
  the sole "selected target" indicator. The pointer was originally
  intended as a secondary cue but the frame-top-vs-visible-content-top
  problem (transparent space above sprites means even a small margin
  floats the tip well above the visible head) made precise placement
  unreliable without per-sprite content-top authoring. Rather than
  ship slightly-wrong placement, the pointer is gated off behind
  `ShowTargetPointer = false` in `BattleTest.cs` — the lifecycle code
  stays wired, re-enablement is a one-line flag flip. The
  `TargetPointer.SnapTo` formula was retuned from frame-top (0.5) to
  a 10%-of-rendered-height anchor above sprite center (0.1) — sprite
  frames have transparent space above the character so frame-top
  floats the tip above the visible head; the smaller multiplier pulls
  the tip back toward the visible character body. Empirically tuned
  against the current sprite roster. Each sub-chunk is its own commit.
- **C11.2** — Magic / Cure circle static formation-center anchor.
  Two non-hop-in callsites (player magic post-cast in
  `BattleAnimator.cs`, enemy non-hop-in cast in `BattleTest.cs`)
  switch from `ComputeCameraMidpoint(attacker, defender)` to
  `_magicCircleAnchor` — a static Vector2 computed once at the end
  of `BuildInitialParties`. Anchor formula: midpoint of player
  formation center (`(PlayerFrontAnchor + PlayerBackAnchor) / 2`)
  and enemy formation center
  (`_enemyParty[0].AnimSpriteOrigin + (0, EnemyBackRowYOffset/2)`),
  lifted by `MagicCircleYOffset = -50f` (interactively tuned —
  sits in the upper portion of the formation Y range so the circle
  reads as "amidst the action" while still being predictable across
  caster-target pairs; slight overlap with taller front-row sprites
  is accepted as the cost of input proximity). Open to revisiting
  during broader playtesting. Hop-in callsites keep
  `ComputeCameraMidpoint` because the caster physically moves to
  that geographic point. Cure-on-self ships center-anchor too —
  timing input at static center, heal effect on the recipient's
  sprite; the input-vs-feedback split is the cost of consistency.
  Damage-number positioning is NOT bundled (deferred to C11.3 or
  its own commit).
- **C11.3** — Layout polish. Four bundled changes:
  - **Formation Y lift** (−50px): `FloorY` 750 → 700, with lockstep
    Y updates on `PlayerFrontAnchor` (630 → 580), `PlayerBackAnchor`
    (720 → 670), `PlayerFrontAnchorRect` (590 → 540), and
    `PlayerBackAnchorRect` (680 → 630). Enemy positions auto-shift
    via the FloorY-derived runtime formula. Pulls the formation
    toward vertical center; tightens the previously-empty top gap
    above the formation while giving more breathing room above the
    player panel strip.
  - **Magic-circle X hardcode** to viewport center
    (`MagicCircleX = 960f`). Locks the timing-input position to
    the most predictable horizontal anchor regardless of formation
    X variance. Y stays formation-derived so the relative-above-
    formation property auto-preserves under formation lifts. Pre-
    survey audit confirmed formation centroids are already
    symmetric within 2.5px of viewport center (player 335 + enemy
    1580 / 2 = 957.5 vs viewport 960), so the hardcode coincides
    with the natural center.
  - **Damage number sprite-derived formula**: `ComputeDamageOrigin`
    switches from ColorRect-derived (with fixed `-20f` Y) to
    `AnimSpriteOrigin + (0, -renderedHeight * 0.5 -
    DamageNumberMarginPixels)`. Mirrors C9's TargetPointer Y-offset
    fix. Single helper rewrite; five callsites unchanged
    (basic offensive, magic per-circle, combo per-pass,
    enemy-on-player, parry counter). `DamageNumberMarginPixels`
    starts at `25f`, tunable. Uses `AnimSpriteOrigin` (rest
    position) not `GlobalPosition` (live tween position) per the
    pre-existing rest-position invariant.
  - **Battle message overlay clearance**: `OverlayBottomInset`
    144 → 160. 16px additional clearance above the C8 active-
    player panel slide-up (~15.6px lift). Shared by `BattleMessage`
    and `BattleDialogue` — both lift together.
  - **`SpawnEffectSprite` and `ComputeCameraMidpoint` migrated
    off ColorRect-derived geometry.** Pre-existing ~40-50px
    ColorRect-vs-sprite Y mismatch (the long-standing "Cure
    target-circle positioning quirk" / damage-number quirk from
    Phase 3.3) was widened to ~90-100px by the formation lift
    above; effects would have spawned at sprite knee/foot height
    without this migration. `SpawnEffectSprite.targetCenter`
    switches from `target.Origin + target.PositionRect.Size / 2f`
    to `target.AnimSpriteOrigin`. `ComputeCameraMidpoint` Y
    derives from `ComputeAnimSpriteCloseY` (attacker close-stance,
    same helper `PlayHopIn`'s AnimSprite tween uses) and
    `defender.AnimSpriteOrigin.Y`; X stays ColorRect-derived
    (the X dimension of the gap was negligible — bug was
    purely Y). Mirrors C11.1 pointer fix shape. Completes the
    ColorRect → sprite-positioning migration started by C11.1
    (pointer) and the damage-number bullet above. After
    this, only HP bar / PartyPanel binding and the X dimension
    of close-stance / midpoint computations use ColorRect-
    derived geometry; positioning math is sprite-derived
    everywhere.
  - **Calibration restore via `SpriteContentYOffset = 52f`.**
    The migration above eliminated the ColorRect-vs-sprite
    structural gap correctly but ALSO removed the implicit
    ~40-45px offset that `.tres`-authored `activeOffset` values,
    `ComputeCameraMidpoint`'s Y expectation, and the original
    damage-number formula were all calibrated against (the
    pre-C11.3 ColorRect-center sat 40px below sprite frame
    center on the player side, 44px on the enemy side —
    convergent below noise floor). Without the calibration
    restore, post-migration consumers read ~30-50px "too high"
    relative to the visible body. New `internal const float
    SpriteContentYOffset = 52f` captures the offset (tuned
    slightly below the pre-C11.3 empirical 40-44 to pull
    consumers down ~12px from the bare calibration baseline
    into upper-body center); applied at `SpawnEffectSprite`
    (`targetCenter += SpriteContentYOffset`) and
    `ComputeCameraMidpoint` (both Y values
    `+= SpriteContentYOffset`). `ComputeDamageOrigin` rewritten
    to use the same constant as a transparent-space allowance,
    with an interactively-tuned multiplier change from `0.5f`
    (frame top) to `0.25f` (upper-body anchor) — the 0.5
    multiplier put taller sprites' damage numbers well above
    visible content (warrior 390 / boss 480 have proportionally
    smaller above-head transparent space than Knight 240). 0.25
    pulls warrior / boss numbers down ~80-100px into the
    upper-body / sword-art region (slight overlap with sword
    art accepted per design call); Knight numbers land in
    shoulder/upper-chest area. Final formula:
    `AnimSpriteOrigin.Y - renderedHeight * 0.25
    + SpriteContentYOffset - DamageNumberMargin`. Counter-
    attack damage stays on its own hand-tuned in-body formula
    (BattleAnimator.cs PlayParryCounter, ~`0.3 × renderedHeight
    + 50f`) — intentional divergence for "big impact" feel.

  Formation X audit confirmed no X edits needed: formations
  symmetric within 2.5px of viewport center. Future refactor
  opportunity: derive all four player anchors from `FloorY` at
  scene init so the lockstep self-maintains.

- `TargetPointer.SnapTo`: replace `target.PositionRect.Size.Y` with the
  visible sprite bounds derived from
  `target.AnimSprite.Scale * frameHeight` (pass frame height in on the
  Combatant, or compute from the sprite's `SpriteFrames` + current
  animation). Pointer sits at a consistent fraction above the visible
  sprite top.
- `ComputeDamageOrigin(Combatant)`: derive from
  `target.AnimSprite.Position` plus a per-combatant vertical offset
  (sprite top). No more hardcoded (440, 570) / (1480, 530).
- Cure target circle (self-targeting visual): same fix; centers on the
  actual sprite, not the ColorRect.
- Magic-attack timing-circle target position: the closing circle for
  ranged/magic attacks spawns at `ComputeCameraMidpoint(attacker,
  defender)` — the midpoint between attacker and defender. At multi-
  unit this places the circle in arbitrary empty space depending on
  which slots are involved (e.g. front-row attacker + back-row
  defender lands the circle off-axis from any combatant). Fix
  alongside the broader multi-unit positioning work picked up in C6
  / C7 / C11; pin to the defender's visual center (or a tunable
  offset above it) rather than the geometric midpoint.

Verification: visual. Pointer above every combatant regardless of
formation position. Damage numbers float from the hit sprite's actual
location. Cure circle centers on the recipient.

---

## 5. Ordering justification

- **C1 before everything** — data additions are low risk and unblock
  every downstream commit that references `IsAbsorber`, `Agility`, or
  `BeckoningTarget`.
- **C2 before C3** — the handler refactor (both subscription sites and
  body references) is behaviour-preserving at 1v1 but load-bearing for
  multi-unit. Landing it on the still-1v1 codebase lets us verify
  correctness under the existing test surface (full fight, parry
  counter, cure, ether) before party count changes.
- **C3 before C4** — infrastructure first; the flag is a
  convenience override that sets the exports, so the exports must exist
  first.
- **C4 before C4.5** — the menu restructure needs a way to test under
  multi-player conditions. Even though C4.5 lands before the queue, the
  4v8 roster makes the Beckon-only rendering observable via slot-0 vs
  slot-N inspection.
- **C4.5 before C5** — the queue wires `_activePlayer` into menu
  dispatch; C4.5 makes the menu structure parameterised by active
  player. C5 then uses that plumbing. Doing them in the other order
  means C5 wires the queue to the old non-parameterised menu and we
  immediately rewrite it.
- **The queue drives 1v1 too.** Because default config is 1v1, _every
  dev run_ exercises the queue at `PlayerPartySize=1, EnemyPartySize=1`.
  The queue is not a 4v8-only code path. Correctness test for C5: at
  1v1 the emitted sequence must be P1 E1 P1 E1… matching the old
  alternation. Any divergence is a bug, not a "feature at high density."
- **C5 before C6** — layout changes at 1v1 are wasted effort; the
  formation only makes sense with full parties actually moving through
  the queue. Queue lands first so C6 has a real active-combatant
  notion to highlight against.
- **C6 before C7, C8** — UI layers depend on the final layout.
- **C7 parallel to C8** — both pure UI, no dependency. Either order
  fine.
- **C7-extra before C8** — sprites must be visible at multi-character
  density to be highlighted. C7-extra was identified during C7
  interactive verification, after the original chunk plan.
- **C9 before C10** — Beckon's target-redirect reuses target-selection
  UI.
- **C11 can land anywhere after C6** — positioning fixes benefit most
  when there are many combatants to test against, but are orthogonal
  to the queue.

---

## 6. Handler-signature tradeoff — recommendation: Option B

_(Confirmed by chat-Claude; not re-litigating.)_

Two approaches to letting animation callbacks know which combatant they
operate on at multi-character density:

### Option A — Generalise handler signatures to take `Combatant`

- `OnCastIntroFinished(Combatant attacker)` etc.
- `+=` sites wrap in closures: `_sprite.AnimationFinished += () =>
  OnCastIntroFinished(combatant);`.
- **Breaks `SafeDisconnectAnim`'s reference-equality contract.** A lambda
  wrapper produces a fresh Callable on every call; there is no stable
  handle to disconnect. To preserve disconnection, each subscribe site
  would need to store its Callable in a per-sprite dictionary and fetch
  it for disconnection. Significant ceremony at ~8 subscription sites.
- CLAUDE.md explicitly calls the reference-equality pattern load-bearing.

### Option B — Extend the sequence-scoped fields pattern (recommended)

- Handlers stay parameterless.
- Both subscription sites and handler bodies read against
  `_sequenceAttacker` / `_sequenceDefender` (or semantic locals derived
  from them). These fields already exist on BattleTest and are set at
  sequence start. Turn-based combat means at most one sequence is
  active at a time; the fields are unambiguous at every handler fire
  point.
- Parry counter uses **no scope-field swap**. The scope fields stay
  bound to the original sequence (attacker = enemy, defender = player)
  throughout the counter. Inside `PlayParryCounter`, two locals name
  the reversed roles explicitly:

  ```csharp
  var counterAttacker = _sequenceDefender;  // player delivers the counter
  var counterTarget   = _sequenceAttacker;  // enemy receives the counter
  ```

  This resolves an ambiguity that surfaced during C2 survey: handlers
  subscribed both pre-swap (regular sequence completion — e.g.
  `OnCastEndFinished` at NonHopInContinuation) and post-swap (during
  the counter — the cast_end line inside `PlayParryCounter`) would
  have to resolve which sequence-scoped field points at the enemy
  based on whether a swap was active. With no swap, handler bodies
  unambiguously read `_sequenceAttacker` for enemy-side ops regardless
  of entry path.
- Preserves the `SafeDisconnectAnim` reference-equality pattern intact.
- Migrates naturally to queue-based turn flow (C5): each
  `AdvanceTurn` / `ExecuteEnemyAttack` / `BeginPlayerAttack` call sets
  the scope fields from the queue's current combatant and its chosen
  target.

**Recommendation:** Option B, strictly smaller diff surface, preserves
load-bearing patterns, and the parry-counter role reversal is contained
to two locals inside one method (`PlayParryCounter`) rather than a
global swap that every handler body would have to reason about.

---

## 7. Verification

**Per-commit:**

- `dotnet build` on `rhythm-rpg.sln` — zero errors and warnings.
- Godot `--headless --quit` load — zero runtime errors on scene load.
- Every commit runs the 1v1 default path (full fight). Every commit
  that touches multi-unit logic (C3 onward) additionally runs the 4v8
  `TestFullParty` path.
- For UI commits (C6, C7, C8): visual inspection via editor run,
  screenshots committed to `../claude_review/` alongside the diff file.

**End-of-Phase-6 acceptance, 4v8 (`TestFullParty = true`):**

- Full fight runs end-to-end without crashes.
- Queue order at equal agility: P1 P2 P3 P4 E1 E2 E3 E4 E5 E6 E7 E8,
  round-robin.
- Defend persists across queue advances: defend with P2 → P2's
  IsDefending stays true through every intervening turn (E-turns, P3,
  P4, E-turns) until P2's next ShowMenu, at which point it clears.
  Multiple concurrent Defenders possible.
- Enemy target selection is uniform random over alive players (not
  Beckon-redirected).
- Absorber's Skills submenu contains absorbed moves and Beckon;
  non-Absorbers' Skills submenu does not render Beckon at all and does
  not list absorbed moves.
- Beckon target-redirect: P1 Absorber beckons E3. E3's turn attacks P1
  with its learnable, P1 absorbs on perfect parry, absorbed move
  appears in P1's Skills submenu.
- Positioning fixes: pointer, damage numbers, Cure circle all align to
  the visible sprite, not the ColorRect.
- Victory fires when all 8 enemies dead. GameOver fires when all 4
  players dead.
- Phase 2 transition does NOT fire (`TestFullParty` suppression log
  line is visible at scene start).

**End-of-Phase-6 acceptance, 1v1 (default config, `TestFullParty =
false`):**

- Queue emits P1 E1 P1 E1…, bit-identical turn flow to pre-refactor.
- Phase 2 transition works exactly as today (Phase2EnemyData fallback
  loaded; Phase 1 boss death triggers the reveal and transition).
- All other combat mechanics (parry counter, absorb, cure, ether item,
  defend, beckon-force-learnable) regression-pass.
- Intro dialogue plays, then menu appears.

---

## 8. Open questions

Most prior questions are resolved; one remains.

**Q7. Mid-turn deaths.** If an enemy dies during a multi-step attack
sequence, the queue should skip that enemy's remaining queue slots
without disrupting the active sequence. Proposed rule: a dying
combatant's current sequence runs to completion (SequenceCompleted fires
normally); all remaining queue entries for a dead combatant are
filtered at `_queue.Advance()`. Same for players dying mid-sequence —
if a player dies to a miss on the current enemy's pass, remaining
passes resolve as usual, then the queue advances, skipping the dead
player's turn slots. Death during a sequence never cancels the in-flight
sequence, even if the dying unit is the active attacker. Confirm this
rule before C5 lands.

Resolved and folded into scope:

- (Q1) 1v1 is the default config; 4v8 is TestFullParty (was 4v5; bumped
  to 4v8 in the C7-extra-followup commit so the staggered enemy grid
  fills both rows × all four columns and the row/col-derived Z fix is
  exercised — 4v5 left cols 1 and 3 empty). Both run through the same
  queue-driven machinery.
- (Q2) Roster config is `[Export] int PlayerPartySize / EnemyPartySize`,
  defaulting to 1/1. Typed-array promotion deferred past Phase 6.
- (Q3) Enemy target selection is uniform random over alive players when
  not Beckon-redirected.
- (Q4) Defend semantics per-player; cleared on own next ShowMenu or
  death; concurrent Defenders allowed.
- (Q5) BottomCenteredOverlayPanel is structural-only; no UX rule
  changes to BattleDialogue or BattleMessage.
- (Q6) Phase 2 transition suppressed when TestFullParty is active;
  Phase2EnemyData = null is set in the test-flag resolution block and
  logged.

---

## 9. Deliverable

On ExitPlanMode approval:

1. First execution commit copies this plan's content (sans the top note)
   to `docs/phase-6-plan.md` and removes the plan-mode scratch file.
2. Subsequent commits follow C1 → C2 → C3 → C4 → C4.5 → C5 → C6 → C7 →
   C7-extra → C8 → C9 → C10 → C11 in §4, with per-commit diff review
   per `docs/workflow.md`. (C7-extra was inserted post-C7 during
   interactive verification — see §4 C7-extra.)
