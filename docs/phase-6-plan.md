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
— not a separate code path. `TestFullParty = true` flips the roster to 4v5
for density validation. The output is a close-to-shippable 4v5 Phase 1 boss
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

- **Default config is 1v1; 4v5 is gated on `TestFullParty`**, but both run
  through the same multi-character machinery. There is no separate 1v1
  code path. `PlayerPartySize` and `EnemyPartySize` are
  `[Export] int` properties on BattleTest with defaults `1, 1`. Setting
  `TestFullParty = true` overrides them to `4, 5`. `BuildInitialParties`
  loops those counts to construct the rosters — Knight copies for players,
  Warrior Phase 1 copies for enemies.
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
  (`PlayerPartySize = 1, EnemyPartySize = 1`). When `true` → 4v5
  (overrides those exports to 4 and 5 in the test-flag resolution block).
- **Phase 2 transition suppression under TestFullParty:** when the flag
  is active, the test-flag resolution block sets `Phase2EnemyData = null`
  and logs `[TEST] TestFullParty suppresses Phase 1 → Phase 2
  transition.` The default 1v1 path retains the fallback load and
  Phase 2 works exactly as today.
- Deferred positioning fixes folded in: pointer (ColorRect → sprite), damage
  numbers (same root cause), Cure target circle (same root cause).

### 3.2 Out of scope (hold the line)

- Phase 1 → Phase 2 transition expanded to multi-unit. At 1v1 default it
  works as today; at 4v5 TestFullParty it is explicitly suppressed.
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
`PlayerPartySize = 4, EnemyPartySize = 5` (via test-flag or manual export
change) — screenshot confirms 4 players and 5 enemies render at sensible
positions; fight continues to target slot-0-only (intermediate behaviour).

### C4 — `TestFullParty` flag

- `[Export] bool TestFullParty = false;` on `BattleTest`.
- Priority resolution extended in the existing test-flag block: Victory >
  GameOver > PhaseTransition > FullParty. Conflicts log
  `[TEST] Victory/GameOver/PhaseTransition overrides TestFullParty`.
- When active: set `PlayerPartySize = 4, EnemyPartySize = 5`, set
  `Phase2EnemyData = null`, and log both
  `[TEST] TestFullParty active — 4 players vs 5 enemies.` and
  `[TEST] TestFullParty suppresses Phase 1 → Phase 2 transition.`
- The Phase 2 suppression is essential — without it, the first Warrior
  death would trigger the transition logic, which assumes exactly one
  enemy and would corrupt state at 4 remaining live enemies.
- Verification: toggle flag, confirm 1v1 and 4v5 both load. Verify Phase 2
  transition works at 1v1 default and is suppressed at 4v5.

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
  Absorber) but becomes observable at 4v5 TestFullParty once the queue
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

Verification: headless load + forced 4v5 (C4 flag on), log queue state
per turn, run through ~2 full rounds observing P1 P2 P3 P4 E1 E2 E3 E4
E5 order. Separately, headless load at default (1v1) — full fight runs
bit-identical to pre-queue behaviour.

### C6 — Layout redesign

- Raise combatants: new `FloorY = 650f` (was 750f — frees ~100px below
  for dialogue/message slot). Per-side Y offset tunables kept for visual
  fine-tuning.
- Staggered formations (4v5 case; 1v1 case uses slot 0 of each side):
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
screenshots confirming the formation at both 1v1 and 4v5. Intro
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

Verification: visual inspection at both 1v1 and 4v5. Log shows current-
turn index aligns with queue state.

### C8 — Active-combatant sprite highlight

- Reuse the existing `CombatantOverlay.gdshader` `tint_amount` uniform
  as the knob. A subtle pulsing tween on the active combatant's sprite
  during their turn window. New `ActiveTween` handle on Combatant (or
  reuse `ThreatTween` with care — they never co-fire because threat
  reveal fires on the _enemy's target_ while the enemy is active, which
  is a different combatant).
- HP panel for active player gets a distinct border colour or glow
  (modulate on the panel's border NinePatchRect).

Verification: visual at 1v1 and 4v5.

### C9 — Target-pool expansion and cycling

- Rewrite `IsTargetPoolSingleton`: for offensive non-friendly-fire
  attacks the pool is all alive combatants on the opposing side; for
  Cure (and future friendly-fire) the pool is all alive combatants on
  the attacker's side. Returns true only when pool count == 1.
- `HandleSelectingTargetInput` wires `ui_left` / `ui_right` to cycle
  through the pool (wrap-around). Each cycle calls
  `_targetPointer.SnapTo(next)`.
- Default target selection in `EnterSelectingTarget` picks the first
  alive opposing combatant for offensive attacks (or self for Cure).
- At 1v1 the pool is always singleton and the pointer still auto-
  confirms — behaviour preserved.

Verification: 4v5 TestFullParty → menu → Attack → cycle through
enemies with left/right. 1v1 default → menu → Attack → auto-confirms
as today.

### C10 — Beckon target-redirect (Option A, Absorber-only)

- Beckon menu entry exists only in the Absorber's Skills submenu (C4.5
  enforces rendering; C10 wires behaviour). Using Beckon requires
  `_activePlayer.IsAbsorber == true` by menu construction.
- `PerformBeckon` transitions to `SelectingTarget` with
  `MenuContext.Beckon` (return path on cancel is the Skills submenu).
  Target pool is alive enemies. Default target: first alive enemy.
- On target confirm: set `_activePlayer.BeckoningTarget = chosenEnemy`,
  deduct MP (`BeckonMpCost = 10`), advance turn via
  `_queue.Advance(); AdvanceTurn();`.
- `SelectEnemyAttack(enemy)`: before natural attack-pool selection, scan
  `_playerParty` for any `p` with `p.BeckoningTarget == enemy`. If
  found, return `enemy.Data.LearnableAttack` and clear
  `p.BeckoningTarget = null`. Otherwise natural selection runs.
- `SelectEnemyTarget(enemy)` (from C5): scan `_playerParty` for any
  `p` with `p.BeckoningTarget == enemy`. If found, redirect target to
  `p`. Otherwise uniform-random over alive players.
- `_threatenedCombatants` population reads the resolved target post-
  Beckon, so the red-tint flash fires on the Beckoner.
- Both `BeckoningTarget` clearing sites (SelectEnemyAttack +
  SelectEnemyTarget) must be coherent — clear in exactly one place (the
  moment the enemy attack begins execution), not both.

Verification: 4v5 TestFullParty with P1 as Absorber. P1 beckons E3.
E3's turn: uniform-random enemy target selection would normally pick a
random player; Beckon redirect overrides to P1. Threat-reveal fires on
P1, white-flash fires on E3, attack is the learnable. Absorb on
perfect parry → learnable added to P1's Skills submenu. Non-Absorbers
(P2–P4) never see a Beckon entry in their submenus.

### C11 — Positioning fixes (pointer / damage numbers / Cure circle)

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
  4v5 roster makes the Beckon-only rendering observable via slot-0 vs
  slot-N inspection.
- **C4.5 before C5** — the queue wires `_activePlayer` into menu
  dispatch; C4.5 makes the menu structure parameterised by active
  player. C5 then uses that plumbing. Doing them in the other order
  means C5 wires the queue to the old non-parameterised menu and we
  immediately rewrite it.
- **The queue drives 1v1 too.** Because default config is 1v1, _every
  dev run_ exercises the queue at `PlayerPartySize=1, EnemyPartySize=1`.
  The queue is not a 4v5-only code path. Correctness test for C5: at
  1v1 the emitted sequence must be P1 E1 P1 E1… matching the old
  alternation. Any divergence is a bug, not a "feature at high density."
- **C5 before C6** — layout changes at 1v1 are wasted effort; the
  formation only makes sense with full parties actually moving through
  the queue. Queue lands first so C6 has a real active-combatant
  notion to highlight against.
- **C6 before C7, C8** — UI layers depend on the final layout.
- **C7 parallel to C8** — both pure UI, no dependency. Either order
  fine.
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
  that touches multi-unit logic (C3 onward) additionally runs the 4v5
  `TestFullParty` path.
- For UI commits (C6, C7, C8): visual inspection via editor run,
  screenshots committed to `../claude_review/` alongside the diff file.

**End-of-Phase-6 acceptance, 4v5 (`TestFullParty = true`):**

- Full fight runs end-to-end without crashes.
- Queue order at equal agility: P1 P2 P3 P4 E1 E2 E3 E4 E5, round-robin.
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
- Victory fires when all 5 enemies dead. GameOver fires when all 4
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

- (Q1) 1v1 is the default config; 4v5 is TestFullParty. Both run through
  the same queue-driven machinery.
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
   C8 → C9 → C10 → C11 in §4, with per-commit diff review per
   `docs/workflow.md`.
