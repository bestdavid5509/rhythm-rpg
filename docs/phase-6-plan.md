# Phase 6 — Multi-Character Scaffold (Shipped Retrospective)

## 1. Context

Phase 6 entered with the battle system multi-character-ready at the data layer
from the Phase 3–5 arc: `Combatant` was a per-unit plain-C# class,
`SequenceContext` marshalled attacker/target through signals, the overlay
shader supported per-combatant independent tween handles, and the unified
`PlayAnim(Combatant, string)` family routed through `Combatant.AnimSprite`.
What stayed 1v1 was the _surface_: a singleton turn alternation
(`ShowMenu` ↔ `BeginEnemyAttack`), hardcoded `_playerParty[0]` /
`_enemyParty[0]` sites across 95 call locations, auto-confirmed target
selection, a single pair of hardcoded sprite positions in `BattleTest.tscn`,
and HP panels that assumed one status card per side.

Phase 6 converted the entire runtime to multi-character machinery driven by
a tick-based AP scheduler. 1v1 was preserved as an _emergent outcome_ of
`PlayerPartySize = 1, EnemyPartySize = 1` running through the same machinery
— not a separate code path. `TestFullParty = true` flipped the roster to 4v8
for density validation. The output was a close-to-shippable 4v8 Phase 1 boss
fight when the flag was on, and a fully-regression-tested 1v1 when the flag
was off. Layout redesign, Beckon target-redirect (per `docs/design-notes.md`
Option A), and the deferred pointer / damage-number / Cure-circle positioning
backlog all landed in this phase.

---

## 2. Survey findings

*Captured at Phase 6 entry; preserved as the baseline for retrospective
comparison.*

### 2.1 Turn loop and state machine

- `BattleState` enum in `BattleTest.cs`:
  `EnemyAttack, PlayerMenu, SelectingTarget, PlayerAttack, GameOver, Victory`.
- Alternation is wired through timer callbacks, not a queue:
  - `PlayTeardown` tail in `BattleTest.cs`:
    `PlayTeardown(() => GetTree().CreateTimer(0.5f).Timeout += ShowMenu)`.
  - Combo-miss path in `BattleTest.cs` defers to `ShowMenu` directly.
  - `ConfirmMenuSelection` in `BattleMenu.cs` hard-routes Defend →
    `BeginEnemyAttack()`.
- Sequence-scoped fields on `BattleTest`:
  `_sequenceAttacker`, `_sequenceDefender`, `_sequenceAttackerClosePos`,
  `_pendingGameOver`.
- `BuildInitialParties` in `BattleTest.cs` builds exactly one player
  and one enemy Combatant by direct reference to the scene-tree
  singleton nodes `_playerSprite`, `_enemyAnimSprite`, etc.

### 2.2 `_playerParty[0]` / `_enemyParty[0]` hardcoding

- ~95 occurrences across `BattleTest.cs` and `BattleAnimator.cs`.
- Partial semantic-local convention (`var player = _playerParty[0];`) exists
  at some callsites but most still index inline.
- No `foreach` / `for` loops over party lists anywhere in the codebase.

### 2.3 Defend and Beckon

- `Combatant.IsDefending` (bool) in `Combatant.cs`. Set in
  `ConfirmMenuSelection` case 2, cleared in every `ShowMenu` call
  (`BattleMenu.cs`) and in `ApplyPhase2Sprite`. **The current
  "cleared on every ShowMenu" behaviour
  is only correct at 1v1 by coincidence** — `ShowMenu` fires exactly after
  every enemy turn, so the implicit rule "lasts one enemy turn" holds. At
  multi-unit density this rule silently breaks: a player who Defends on
  their turn would lose IsDefending the moment any other player's ShowMenu
  fires, well before the defender's own next turn. Phase 6 replaces this
  with per-combatant "cleared on that combatant's own next ShowMenu" — see
  C5 notes.
- `Combatant.IsBeckoning` (bool) in `Combatant.cs`. Set in
  `PerformBeckon`, consumed in `SelectEnemyAttack` where it forces
  the learnable attack return. Replaced by `BeckoningTarget:
  Combatant?` in C1.

### 2.4 Target selection (Phase 4)

- `EnterSelectingTarget`, `ConfirmTargetSelection`,
  `CancelTargetSelection`, `HandleSelectingTargetInput` in
  `BattleTest.cs`. `ui_left/ui_right` cycling is stubbed as a no-op.
- `IsTargetPoolSingleton(Combatant) => true` in `BattleTest.cs`.
  Auto-confirms every selection today.
- `MenuContext` enum tracks return-to routing on cancel
  (Main / AbsorbedMoves / Items).
- `TargetPointer` in `TargetPointer.cs` draws a pure-code triangle;
  `SnapTo` uses `target.AnimSprite.GlobalPosition.X` with a Y offset
  derived from `target.PositionRect.Size.Y` — this is the
  ColorRect-vs-visible-sprite mismatch the user flagged.
  Damage-number origin in `ComputeDamageOrigin` (`BattleTest.cs`)
  uses hardcoded (440, 570) / (1480, 530) coordinates — same root
  cause.

### 2.5 Phase 5 threat reveal

- `_threatenedCombatants` (List<Combatant>) populated in
  `BeginEnemyAttack` (`BattleTest.cs`). Single entry today.
  `FlashCombatantThreatened` tweens `tint_amount` on that combatant's
  `FlashMaterial`; 0.6s pulse; a `CreateTimer(0.6f)` defers the attack launch
  so tint fades as the attack begins.
- Each Combatant gets its own `ShaderMaterial` instance (per-sprite,
  independent `flash_amount` / `tint_amount` uniforms and independent
  `FlashTween` / `ThreatTween` handles).

### 2.6 Menu and UI

- Main menu: `{ "Attack", "Absorbed Moves", "Defend", "Items" }` in
  `BattleMenu.cs`. No "Beckon" at top level — Beckon lives inside
  the Absorbed Moves submenu (`BattleMenu.cs`).
- `MakeMenuPanel` anchors bottom-left; `PositionMenuPanelsAbovePlayerPanel`
  reads `_playerPanel.Size.Y` to sit just above the player HP panel.
- Player HP panel at bottom-left (260f min width), enemy HP panel at
  top-right (220f min width). `UpdateHPBars` (`BattleTest.cs`)
  writes fill widths for exactly one bar per side.
- `BattleTest.tscn` contains hardcoded `PlayerAnimatedSprite` at
  (440, 630) and `EnemyAnimatedSprite` at (1480, 670). Two ColorRect
  reference nodes. No CanvasLayers in the tscn — all panels built at
  runtime.
- `FloorY = 750f` in `BattleTest.cs`. Read at player Y, enemy Y, and
  `SpawnEffectSprite` positioning.
- `BattleDialogue` and `BattleMessage` are two separate classes with
  duplicated bottom-anchored panel construction. `BattleDialogue` at
  ~(0.2, 0.8) x anchor, 96px above bottom; `BattleMessage` at 0.5 center,
  100px above bottom. CLAUDE.md flags a deferred `BottomCenteredOverlayPanel`
  helper — not started.

### 2.7 Test flags

Priority resolution in the test-flag block of `BattleTest._Ready`:
Victory > GameOver > PhaseTransition. Each conflict logs a
`[TEST] X overrides Y` error. Each active flag prints
`[TEST] X active — Y.` to stdout. Intro dialogue skipped only on
Victory/GameOver paths (PhaseTransition keeps intro).

### 2.8 Handler-binding inventory

Handlers bound via `AnimationFinished +=`:

| Site | Sprite | Handler | SafeDisconnect before |
|---|---|---|---|
| BattleTest.cs cast path | `_enemyAnimSprite` | `OnCastIntroFinished` | yes |
| BattleAnimator.cs death sites | `_enemyAnimSprite` | `OnEnemyDeathFinished` | yes |
| BattleAnimator.cs retreat | `_playerAnimSprite` | `OnRetreatFinished` | yes |
| (plus parry, combo-slash, hit, hop-in, cast_end, cast_transition, magic cast) | mixed | mixed | yes |
| BattleSystem.cs spawned effect sprite | spawned effect sprite | self-disconnecting inline closure | yes |

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

### 3.1 In scope (shipped)

- **Default config shipped as 1v1; 4v8 gated on `TestFullParty`**, with
  both running through the same multi-character machinery. No separate
  1v1 code path. `PlayerPartySize` and `EnemyPartySize` are
  `[Export] int` properties on BattleTest with defaults `1, 1`. Setting
  `TestFullParty = true` overrides them to `4, 8`. `BuildInitialParties`
  loops those counts to construct the rosters — Knight copies for players,
  Warrior Phase 1 copies for enemies. (4v8 filled the staggered enemy
  grid to all 4 columns × both rows; the prior 4v5 left cols 1 and 3
  empty, masking the row/col-Z bug fixed in the C7-extra-followup.)
- Absorber identified by explicit `bool IsAbsorber` on Combatant;
  `_playerParty[0].IsAbsorber = true`, others false. Only the Absorber's
  Skills submenu contains absorbed moves. **Only the Absorber's Skills
  submenu contains a Beckon entry** — non-Absorbers do not render the
  Beckon entry at all (not greyed out). Using Beckon requires IsAbsorber
  by construction; no runtime gate needed because the menu never exposes
  the option to non-Absorbers.
- Skills submenu renamed from "Absorbed Moves". All four players share
  Combo Strike, Magic Comet, Cure. Absorber additionally has Beckon and
  any absorbed moves.
- Agility field on Combatant; all combatants share equal agility at
  Phase 6 scope.
- `TurnOrderQueue` shipped with tick-based AP scheduling and the
  players-before-enemies / party-list-index tie-break. At
  `PlayerPartySize=1, EnemyPartySize=1` the queue emits P1 E1 P1 E1…
  matching the pre-refactor `ShowMenu`/`BeginEnemyAttack` alternation —
  the old behaviour became an emergent output of the queue, not a
  retained fallback.
- Queue replaced `ShowMenu`/`BeginEnemyAttack` alternation everywhere.
- Per-player Defend persistence: cleared on that combatant's own next
  `ShowMenu` (or on death). Multiple players may be Defending
  concurrently.
- Beckon target-redirect via `Combatant.BeckoningTargets: HashSet<Combatant>`
  (Option A from `docs/design-notes.md`; replaced the original
  `IsBeckoning: bool`). Multi-Beckon stacking landed post-C10.
- Target selection: `IsTargetPoolSingleton` reads live valid-target
  count; `ui_left` / `ui_right` cycles through the pool; pointer
  originally visible when pool > 1, then gated off in C11.1 behind
  `ShowTargetPointer = false` once the yellow per-sprite highlight
  became the sole indicator.
- Enemy target selection (when not redirected by Beckon): uniform random
  over alive players. Deterministic rules (lowest-HP, frontmost)
  deferred to design work.
- Layout redesign shipped across C6 / C7-extra / C7-extra-followup /
  C11.3: staggered formation grid (player single ↘ diagonal, enemy
  two-row staggered), per-slot HP/MP panels (4-across bottom player
  strip, combined enemy panel top-right), battle menu fixed bottom-left,
  formation lifted in C11.3 to balance the top/bottom UI gaps.
- Active-combatant indicators shipped in C8: turn-strip card
  differentiation, active-player panel slide-up, active sprite tint.
- Turn-order UI strip (top-left, vertical stylised name cards, no
  portraits) shipped in C7 with the C7 follow-up's fade-on-death and
  rebuild-from-current-actor fix.
- `BottomCenteredOverlayPanel` helper deferred — structural extraction
  not landed. `BattleDialogue` and `BattleMessage` retained their
  duplicated panel construction, both with `OverlayBottomInset`
  raised to 160 in C11.3 to clear the C8 active-panel slide-up.
- `TestFullParty` test flag shipped at lowest priority. Default `false`
  → 1v1 (`PlayerPartySize = 1, EnemyPartySize = 1`). When `true` → 4v8
  (overrides those exports to 4 and 8 in the test-flag resolution
  block).
- **Phase 2 transition suppression under TestFullParty:** when the flag
  is active, the test-flag resolution block sets `Phase2EnemyData =
  null` and logs `[TEST] TestFullParty suppresses Phase 1 → Phase 2
  transition.` The default 1v1 path retains the fallback load and
  Phase 2 works as before.
- Positioning fixes folded in across C11.1 / C11.2 / C11.3: pointer
  switched to fixed-pixel margin then gated off; magic-circle anchor
  static at viewport center + formation-derived Y; damage-number anchor
  sprite-content-top with a tuned 0.25 multiplier; `SpawnEffectSprite`
  and `ComputeCameraMidpoint` migrated off ColorRect-derived geometry
  onto `AnimSpriteOrigin` with a calibration-restore
  `SpriteContentYOffset`.

### 3.2 Deliberately not in Phase 6

- Phase 1 → Phase 2 transition expanded to multi-unit. At 1v1 default
  it works as before; at 4v8 `TestFullParty` it is explicitly
  suppressed.
- Parry counter refactor to route through `BattleSystem.StartSequence`.
- `AttackStep.Offset` / `PlayerOffset` schema consolidation (D5).
- Character-specific move sets (all players share Knight moves).
- Stats beyond HP/MP/Agility (strength, defence, etc.).
- Turn-order UI portraits / art polish.
- Balance tuning passes.
- Friendly-fire exposure (`CanFriendlyFire` opt-in). Architecture
  permits it; no menu option exposes it in Phase 6.
- Promotion to typed-array roster config (`EnemyData[]` per slot) —
  current config is uniform copies only.

Items catalogued here remain candidates for successor phases; see §10
for the consolidated deferred-from-Phase-6 catalogue including
follow-ups that surfaced during the chunk work.

---

## 4. Work breakdown (shipped)

Implementation order. Each chunk shipped as a standalone commit under the
pre-commit diff-review workflow (`../claude_review/<name>-review.txt`).

### C1 — Combatant field additions (data-only)

- Added `int Agility = 10` and `bool IsAbsorber` to `Combatant.cs`.
- Added `Combatant BeckoningTarget` (nullable); removed `IsBeckoning` bool.
  Updated the one write site (`PerformBeckon`) and the one read site
  (`SelectEnemyAttack`) to operate on `BeckoningTarget != null`. Target
  defaulted to `_enemyParty[0]` for this commit so behaviour was preserved.
  Phase 2 transition cleanup site (`ApplyPhase2Sprite`) updated in the
  same commit (cleared `BeckoningTarget` instead of `IsBeckoning`).
- No other behaviour change. `BuildInitialParties` set
  `_playerParty[0].IsAbsorber = true`, `Agility = 10` for both entries.

### C2 — Handler refactor: subscription sites + body references

**Subscription sites** — every `_<side>AnimSprite.AnimationFinished +=
handler` in `BattleTest.cs` and `BattleAnimator.cs` became
`<combatant>.AnimSprite.AnimationFinished += handler` (routed through
a helper `ConnectAnim(Combatant, Action)` that mirrored `SafeDisconnectAnim`
semantically). The combatant sourced from `_sequenceAttacker` /
`_sequenceDefender` at the subscription moment — same rule as the body
refactor.

**Handler bodies** — within every `AnimationFinished` handler body,
replaced hardcoded `_playerParty[0]` / `_enemyParty[0]` with reads
against `_sequenceAttacker` / `_sequenceDefender` (via semantic locals
like `var attacker = _sequenceAttacker;`). See §6 for the justification.

**Parry counter edge case:** at `PlayParryCounter` entry, the counter's
attacker is the prior sequence's defender and vice versa. C2 used no
scope-field swap — the scope fields stay bound to the original sequence
throughout the counter, with two locals inside `PlayParryCounter` naming
the reversed roles explicitly (`counterAttacker = _sequenceDefender;
counterTarget = _sequenceAttacker;`). The original plan considered an
explicit swap but landed on the no-swap form during implementation; the
correction is captured in commit `dd7cea1`.

**Party-scoped handlers** (death-animation ends, game-over / victory
checks that iterate both parties) used explicit party-iteration, not the
sequence-scoped fields.

Still 1v1 at this point; this was a pure refactor that made C5 safe.

**C2 grep checklist — run at start and end of the commit:**

- `_(player|enemy)AnimSprite\.AnimationFinished` — zero hits post-refactor.
- `_(player|enemy)AnimSprite\.(Play|PlayBackwards|SpeedScale|SpriteFrames)`
  inside handler bodies — zero hits post-refactor.
- Direct `_(player|enemy)AnimSprite\.(Frame|Stop|Material|ZIndex)` reads
  inside handler bodies — allowed only for the deliberate exceptions
  documented in CLAUDE.md's "Direct sprite access that bypasses the
  guards" paragraph (capture-before-Stop frame reads, ZIndex writes
  during reveal sequence).

### C3 — Party expansion infrastructure (still 1v1 default)

- Added `[Export] int PlayerPartySize = 1;` and
  `[Export] int EnemyPartySize = 1;` on `BattleTest`.
- Rewrote `BuildInitialParties` to loop each size, spawning additional
  `AnimatedSprite2D` + `ColorRect` nodes at runtime, building frames for
  each, applying per-combatant `ShaderMaterial` instances, and appending
  to `_playerParty` / `_enemyParty`. The existing tscn-placed pair
  served as slot 0 on each side; additional slots instantiated in code.
- Each slot got a positional offset (C6 computed the real staggered
  formation; C3 laid slots out on a simple horizontal line). Positions
  data-derived, not hardcoded.
- `UpdateHPBars`, `CheckVictory`, `CheckGameOver` iterate both party
  lists. Victory fires when every enemy `IsDead`; GameOver fires when
  every player `IsDead`.
- **Intermediate-state note (deliberate):** C3 made rosters multi-unit
  but the queue didn't land until C5. During the C3-through-C5 interval,
  the existing alternation still targeted `_playerParty[0]` as the
  active player and `_enemyParty[0]` as the active enemy — other party
  members sat idle on their slots. Intentional scaffolding flagged in
  the C3 review note. C5 closed the mismatch.
- Sequence-scoped fields drove the active attacker/defender.

### C4 — `TestFullParty` flag

- Added `[Export] bool TestFullParty = false;` on `BattleTest`.
- Priority resolution extended in the existing test-flag block: Victory
  > GameOver > PhaseTransition > FullParty. Conflicts log
  `[TEST] Victory/GameOver/PhaseTransition overrides TestFullParty`.
- When active: sets `PlayerPartySize = 4, EnemyPartySize = 8`, sets
  `Phase2EnemyData = null`, and logs both
  `[TEST] TestFullParty active — 4 players vs 8 enemies.` and
  `[TEST] TestFullParty suppresses Phase 1 → Phase 2 transition.`
- The Phase 2 suppression was essential — without it, the first Warrior
  death would have triggered the transition logic, which assumes
  exactly one enemy and would corrupt state at 7 remaining live
  enemies.

### C4.5 — Menu restructure for multi-character Skills

- Main menu became `{ "Attack", "Skills", "Defend", "Items" }`. Rename
  only — same dispatch slot (index 1).
- Skills submenu (renamed from "Absorbed Moves") base entries became
  `{ "Combo Strike", "Magic Comet", "Cure", "Back" }` for all players.
- **Absorber-only conditional entries:** when the active player's
  `.IsAbsorber` is true, the submenu additionally includes a "Beckon"
  entry and any absorbed moves (from `_absorbedMoves`). For non-Absorbers,
  Beckon does not render at all — not greyed-out, not present. Uses the
  `RebuildSubMenu` pattern parameterised by the active player.
- This commit still ran on the pre-queue alternation (C5 hadn't landed
  yet), so "active player" was still `_playerParty[0]`. The menu
  conditional had no visible effect at 1v1 default (slot 0 is always
  the Absorber) but became observable at 4v8 TestFullParty once C5's
  queue rotated the active player.
- `InitSubMenuData` / `PopulateSubMenuPanel` restructured to rebuild
  per-active-player rather than at BuildMenu time. The existing
  rebuild-on-absorption pattern generalised to
  rebuild-on-active-player-change.

### C5 — Turn-order queue

- New `TurnOrderQueue` class shipped initially with round-based
  ordering, then refactored to a tick-based AP scheduler in the
  C7-prerequisite commit (`59bdaa0`). `Advance()` pops next alive
  combatant; dead combatants are skipped during advance via the per-tick
  `if (c.IsDead) continue` in `SimulateOne`.
- `BattleTest._queue` field populated at `_Ready` after
  `BuildInitialParties`.
- Replaced legacy alternation:
  - `ShowMenu()` / `BeginEnemyAttack()` became the two branches of a
    new `AdvanceTurn()` method that reads `_queue.Current`. Player →
    `ShowMenu` with `_activePlayer` set; enemy → `BeginEnemyAttack`
    with the active enemy passed in.
  - At every sequence-completion site (`OnPlayerPromptCompleted`,
    `OnEnemySequenceCompleted`, parry counter teardown, combo-miss,
    Beckon, Defend, ItemUse, Victory/GameOver branches), the
    `ShowMenu` or `BeginEnemyAttack` invocation was replaced with
    `_queue.Advance(); AdvanceTurn();`.
- `BeginEnemyAttack` / `ExecuteEnemyAttack` accept the active enemy
  Combatant as parameter — derived from the queue, not
  `_enemyParty[0]`.
- `ShowMenu` sources `_activePlayer` from the queue. All menu paths
  (`ConfirmMenuSelection`, `ConfirmSubMenuSelection`,
  `ConfirmItemMenuSelection`) use `_activePlayer` instead of
  `_playerParty[0]`. C4.5's rebuild is triggered by the
  `_activePlayer` change.
- **Defend semantics:** each player's `Combatant.IsDefending` persists
  from the turn they pressed Defend until their own next `ShowMenu`.
  Multiple players can be Defending simultaneously. The enemy attack
  miss path checks the specific target's `IsDefending`. `IsDefending`
  is cleared in `ShowMenu` only when `_activePlayer` matches the
  defender, and is cleared on death by the death-handler branch.
- **Enemy target selection (uniform random):** new helper
  `SelectEnemyTarget(Combatant attacker)` — if any player's
  `BeckoningTargets` contains the attacker, return that player; the
  full plumbing landed inside the C9 bundle. Otherwise pick a
  uniform-random alive player. The resolved target populates
  `_threatenedCombatants` and becomes the sequence's defender.
- **1v1 correctness:** at `PlayerPartySize = 1, EnemyPartySize = 1`
  the queue emits P1 E1 P1 E1… — the old alternation became an emergent
  output of the queue, not a retained fallback path.

### C6 — Layout redesign (per-slot panels)

- Per-slot HP/MP panels shipped: 4-across bottom strip for players
  (only `PlayerPartySize` of them visible) with the slot-0-at-rightmost
  inversion via `(partySize - 1 - slotIndex)`, and a combined enemy
  panel top-right with one row per enemy (only `EnemyPartySize` rows
  visible).
- Active-player highlight cascade through panel modulate (see C8 for
  the active-tint refinement).
- Battle menu stays fixed bottom-left. The original plan called for
  `PositionMenuPanelsAbovePlayerPanel` replacement with a fixed-Y
  offset against the bottom HP strip; shipped with the menu anchored
  at viewport bottom-left independent of the panel strip.
- The `FloorY` lift (originally targeted at C6 for 750 → 650) did not
  ship in C6 — formation stayed at the pre-C6 floor. The C11.3 layout
  polish later lifted `FloorY` from 750 to 700 with the 4-anchor
  lockstep update.
- The `BottomCenteredOverlayPanel` helper from the original plan did
  not ship. `BattleDialogue` and `BattleMessage` retained their
  duplicated panel construction (flagged for a future structural
  extraction; out of Phase 6 scope).

### C7 — Turn-order strip UI

- New `TurnOrderStrip` CanvasLayer built in `BattleTest._Ready` after
  the queue lands.
- Vertical strip at top-left of the screen, each slot a stylised
  `MakeLayeredPanel` mini-card with a name label and a side-coded
  border colour. No portraits.
- Current-turn slot visually distinct via the `StripActiveModulate` /
  `StripAliveModulate` constants — brightened active card + dimmed
  non-active cards (C8 sub-feature 1).
- Strip repopulates at `_queue.Rebuild()` (each round) and refreshes
  current-turn highlight at each `_queue.Advance()`. The C7 follow-up
  commit (`b746923`) added `FadeDeadCombatantFromStrip` and
  rebuild-from-current-actor behaviour for ally deaths.
- At 1v1 the strip shows two slots alternating — still renders, not
  hidden.

### C7-extra — Combat sprite layout cleanup

C7-extra was inserted as a C8 prerequisite, identified during C7
interactive verification — the strip lookahead surfaced the underlying
multi-character sprite layout as untenable at 4v5 density (sprites
trailed off-screen with the `PlayerSlotSpacing=140` /
`EnemySlotSpacing=160` single-row math from the C3 scaffolding).
Sprites had to be visible to be highlighted.

- FF-style mirrored diagonal columns shipped. The player diagonal
  slopes ↘ (slot 0 at top-right, "leader confronting"; slots 1-3 step
  down-left at partySize=4). Player anchors: the legacy slot-0 tscn
  position became the front anchor (preserving 1v1 bit-identical
  visual), with linear interpolation to a back anchor for slots
  1..N-1. Player party capped at 4 in Phase 6 scope — no two-row
  extension on the player side.
- Constant scale `(3, 3)` across all slots — no depth scaling. Hop-in
  tweens stayed pure-translate (critical for animation simplicity at
  multi-character density).
- Enemy formation shipped as a staggered two-row diagonal grid (in
  the follow-up commit — the original C7-extra shipped a single
  diagonal column with linear-Lerp + back-column extension at 6-8
  enemies; the 4v5 default cramped sprites at 80-px slot spacing vs
  390-px Warrior sprite width, and the staggered two-row grid replaced
  it): two parallel down-and-right diagonals. The front row anchored
  at slot 0's runtime position (per-EnemyData: Warrior `(1480, 606)`,
  Phase-2 boss `(1480, 592)`); each subsequent column in either row
  steps `+80 X, +24 Y`. The back row sits BELOW (`+96 Y`) and to the
  LEFT (`-40 X`) of the corresponding front-row column — reads as a
  "second wave below" depth-staggered formation, not "depth-receding
  behind." Slot index → `(row, col)` via `EnemySlotToGridPosition`
  lookup; the pattern alternates front/back and fills outer columns
  (col 0, col 2) before inner (col 1) before far-outer (col 3). At 4
  enemies, slots filled `FC0/BC0/FC2/BC2` leaving cols 1 and 3
  empty — reads as a "pinch" formation around the inner column. Slot
  0 at `(row=0, col=0)` returned the slot 0 runtime position
  unchanged → 1v1 bit-identical to ship state by construction. No
  depth-sort / Z-index changes shipped in this pass; the follow-up
  commit added them.
- HP/MP panels at bottom-center reordered so slot 0 sat at the
  RIGHTMOST panel position via `(partySize - 1 - slotIndex)`
  inversion. Spatial correlation: damage on sprite → eye drops →
  matching panel directly below.
- Position-consuming code (`PlayHopIn`, `PlayTeardown`,
  `ComputeClosePosition`, `ComputeSlamPosition`,
  `ComputeCameraMidpoint`, `ComputeDamageOrigin`,
  `BattleSystem.SpawnEffectSprite`) all read per-combatant
  `Origin` / `AnimSpriteOrigin`. New diagonal positions flowed
  through automatically; no migration needed at consumer sites.
- Per-encounter override system stayed out of scope. JRPG genre
  convention is hand-placed positions per encounter for set-piece
  fights; C7-extra shipped only a universal default formula. A future
  override mechanism (e.g. via `EnemyData` per-slot offsets) is
  post-Phase-6 work.
- Magic timing-circle off-axis (already C11 scope): the diagonal
  layout amplified the `ComputeCameraMidpoint`-vs-sprite-positions
  mismatch for cross-formation magic attacks. Flagged for C11
  priority; addressed in C11.2 / C11.3.

### C7-extra follow-up — Per-slot Z-index for combatant depth-sort

Identified after the staggered two-row diagonal grid landed: at
multi-character density, back-row sprites overlapping front-row
sprites in the same column inherited only the scene-tree spawn order
for render priority, producing the wrong "back wave on top of front
wave" occlusion. Bounded slot-derived Z values fixed the depth-sort
without risking the `CANVAS_ITEM_Z_MAX (~4096)` overflow the prior
Y-tied attempt hit. As a consequence, `TestFullParty` was bumped from
4v5 to 4v8 — the prior 4v5 had left enemy cols 1 and 3 empty,
masking the class of bug this commit fixed.

- **Formation Z values shipped spaced 2 apart** so the hop-in
  attacker's `defender.Z + 1` bump always landed at an odd Z that's
  unique by construction — strictly between formation members on
  either side. Without the spacing, an enemy attacker bumped to Z=1
  would have tied with player slot 1 at Z=1, and scene-tree order
  (enemies added after players in `BuildInitialParties`) would have
  let the enemy win regardless of attacker side. Captured in
  interactive verification: enemy Warrior 3 attacking Knight slot 0
  bumped to the wrong side of a Z-tie under the prior unit-spaced
  scheme.
- **Player Z = `slotIndex * 2`.** Player formation is a single ↘
  diagonal column where Y is monotonically increasing in slot index,
  so slot index already matched Y rank.
- **Enemy Z = `(row * 4 + col) * 2`** read from
  `EnemySlotToGridPosition`. The slot fill pattern
  (`FC0/BC0/FC2/BC2/FC1/BC1/FC3/BC3` — outer-cols-first, alternating
  front/back) is deliberately non-monotonic in Y, so slot index ≠ Y
  rank. `(row*4+col)` maps to Y rank: front row 0..3 (FC0..FC3
  ascending Y) then back row 4..7 (BC0..BC3 ascending Y, all behind
  the front row). Slot 0 still got Z=0 (FC0 = (0,0)*2 = 0),
  preserving 1v1 bit-identity. Both sides' assignments live in
  `BuildInitialParties` after the rect/sprite pair is resolved
  (covers tscn-placed slot 0 and dynamically spawned slots through
  one site each).
- **Hop-in attacker takes `defender.Z + 1`.** "Joins the defender's
  row" depth band, landing at an odd Z slot uniquely free under the
  2-apart spacing — guarantees the attacker renders strictly in
  front of the defender AND of any same-side or opposing combatant
  whose formation Z would otherwise tie. Pre-bump Z snapshotted into
  a new `_attackerZIndexBeforeHopIn` sentinel field (-1 = no active
  snapshot) at `PlayHopIn` start; restored at `PlayTeardown`'s
  `tween.Finished`. `IsDead` check on restore preserves the
  Phase 1 → Phase 2 reveal contract (dead Phase 1 warrior keeps its
  `SpawnBossReveal`-bumped Z until `SwapToPhase2`'s own snapshot
  pattern restores it). Sentinel cleared unconditionally so a leak
  cannot persist into the next sequence even when restore is
  skipped.
- The previous unconditional `defender.AnimSprite.ZIndex = 0` in
  `PlayHopIn` was dropped — at 1v1 it was already a no-op; at
  multi-character density it was a latent clobber of the defender's
  slot Z. Defender Z is left untouched throughout the sequence.
- `SpawnEffectSprite` (BattleSystem) reads `target.AnimSprite.ZIndex`
  instead of the hardcoded `Z = 3`. Effects "join the row" of their
  target visually. Tree order keeps the effect rendering on top of
  the target sprite at equal Z (effect added later). Phase 2 reveal
  sequence is unaffected — no `SpawnEffectSprite` calls fire during
  the reveal, so the legacy reveal-layer Z values (reveal = 1,
  warrior bumped = 2) keep their hardcoded constants in
  `BattleAnimator.SpawnBossReveal`.
- Damage numbers (Z = 100 design lock) were not implemented at the
  time of this commit; the constraint was reserved for a future
  commit.
- Hit flashes are shader-on-sprite — they follow the sprite's Z
  naturally, no separate handling needed.
- **TestFullParty roster bump (4v5 → 4v8) folded into this commit.**
  Verification was meaningless at 4v5 because cols 1 and 3 stayed
  empty — slot 4 (FC1) and slot 6 (FC3) didn't exist, so the
  Y-rank-vs-slot-index divergence that produces the bug never
  manifested. Aligning the in-source flag with the verification
  density keeps future regression checks from missing the same class
  of bug.

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

### C9 — Target-pool expansion and cycling

- `EnterSelectingTarget` took an explicit `CombatantSide targetSide`
  hint instead of a `Combatant defaultTarget`. The five menu
  callsites in `BattleMenu.cs` (Attack, Combo Strike, Magic / Cure,
  Beckon, Ether) passed the side directly: offensive attacks passed
  `CombatantSide.Enemy`; Cure and Ether passed `CombatantSide.Player`.
  Reused the existing `CombatantSide` enum from `Combatant.cs` rather
  than introducing a parallel `TargetSide` enum.
- New helper `GetTargetPool(CombatantSide side)` returned alive
  combatants on the named side, originally sorted by
  `AnimSpriteOrigin.Y` then `AnimSpriteOrigin.X` (top-to-bottom in
  formation, tie-breaking left-to-right within a row). AnimSpriteOrigin
  is the snapshotted post-floor-anchor sprite center; during
  SelectingTarget no sprite tween is active (hop-in fires after
  confirm), so this matches live screen position. Sort axis later
  branched per side in the post-C9 cycling-direction fix below.
- `EnterSelectingTarget` built the pool once at entry and cached it
  into `_targetPool`/`_targetPoolIndex`. Default starting target is
  pool index 0 (topmost, leftmost on tie) — not the menu-supplied
  default, not slot 0, not last-selected. Per-actor / cross-battle
  target memory deferred.
- `IsTargetPoolSingleton` removed; the named method was no longer
  needed. Auto-confirm fires on `_targetPool.Count == 1` directly in
  `EnterSelectingTarget`. Empty-pool case (structurally unreachable
  today — Victory / GameOver fire before all-dead-side menu entry)
  defended with a `PrintErr` + `CancelTargetSelection`.
- `HandleSelectingTargetInput` wired `ui_left` / `ui_right` to cycle
  the cursor through `_targetPool` with wraparound. Cycling only
  mutates pool cursor + `_selectedTarget` + pointer position;
  attack-identity state (`_isComboAttack`, `_activeMagicAttack`) is
  left untouched so the launcher closure captured at menu-pick time
  stays valid.
- Cure-on-allies in scope: heal attacks pass `CombatantSide.Player`,
  so the pool includes all alive players (including the active
  player). At 1v1 default the pool is singleton and Cure auto-
  confirms onto the active player as before.
- Friendly-fire damage exposure (`AttackData.CanFriendlyFire`) and
  Beckon target-redirect on the enemy turn stayed out of scope here
  (the latter landed in C10). C9's Beckon picker just selected which
  enemy populated `_activePlayer.BeckoningTarget`.
- Single-resolved-target launcher contract preserved:
  `ConfirmTargetSelection` still reads `_selectedTarget` and invokes
  the captured launcher; no launcher-side changes.

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
  `BeginEnemyAttack` flow in `BattleTest.cs` resolves
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
  scene init so the lockstep self-maintains (flagged in §10).

---

## 5. Ordering rationale (retrospective)

- **C1 landed first** because data additions were low risk and
  unblocked every downstream commit that referenced `IsAbsorber`,
  `Agility`, or `BeckoningTarget`.
- **C2 landed before C3** so the handler refactor (both subscription
  sites and body references) could ship behaviour-preserving on the
  still-1v1 codebase. Correctness was verified under the existing
  test surface (full fight, parry counter, cure, ether) before party
  count changed.
- **C3 landed before C4** because infrastructure had to exist first;
  the flag was a convenience override that set the exports, so the
  exports had to exist first.
- **C4 landed before C4.5** because the menu restructure needed a
  way to test under multi-player conditions. Even though C4.5 landed
  before the queue, the 4v8 roster made the Beckon-only rendering
  observable via slot-0 vs slot-N inspection.
- **C4.5 landed before C5** because the queue wired `_activePlayer`
  into menu dispatch; C4.5 made the menu structure parameterised by
  active player. C5 then used that plumbing. The other order would
  have wired the queue to the old non-parameterised menu and forced
  an immediate rewrite.
- **The queue drove 1v1 too.** Because default config was 1v1, every
  dev run exercised the queue at `PlayerPartySize=1, EnemyPartySize=1`
  — the queue was not a 4v8-only code path. Correctness target for
  C5: at 1v1 the emitted sequence had to be P1 E1 P1 E1… matching the
  old alternation. Any divergence would have been a bug, not a
  "feature at high density."
- **C5 landed before C6** because layout changes at 1v1 would have
  been wasted effort; the formation only made sense with full parties
  actually moving through the queue. Queue landed first so C6 had a
  real active-combatant notion to highlight against.
- **C6 landed before C7 / C8** because UI layers depended on the
  final layout.
- **C7 was parallel to C8** in principle — both pure UI, no
  dependency — but C7 shipped first.
- **C7-extra landed before C8** because sprites had to be visible at
  multi-character density to be highlighted. C7-extra was identified
  during C7 interactive verification, after the original chunk plan.
- **C9 landed before C10** because Beckon's target-redirect reused
  target-selection UI.
- **C11 was orthogonal to the queue and could have landed anywhere
  after C6;** positioning fixes benefited most when there were many
  combatants to test against. The split into C11.1 / C11.2 / C11.3
  was a shippability decision made after the C9/C10 bundles landed,
  to keep each positioning concern independently reviewable.

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

## 7. Verification (outcomes)

**Per-commit gates:** every commit passed `dotnet build` on
`rhythm-rpg.sln` with zero errors and zero warnings, and Godot
`--headless --quit` load with zero runtime errors. Every commit
exercised the 1v1 default path (full fight); every commit that
touched multi-unit logic (C3 onward) additionally exercised the 4v8
`TestFullParty` path. UI commits (C6, C7, C8, C11.x) added visual
inspection via editor run with screenshots committed to
`../claude_review/` alongside the diff file.

**End-of-Phase-6 outcomes, 4v8 (`TestFullParty = true`):**

- Full fight ran end-to-end without crashes.
- Queue order at equal agility shipped as P1 P2 P3 P4 E1 E2 E3 E4 E5
  E6 E7 E8, round-robin.
- Defend persisted across queue advances: defending with P2 left P2's
  IsDefending true through every intervening turn (E-turns, P3, P4,
  E-turns) until P2's next ShowMenu, at which point it cleared.
  Multiple concurrent Defenders supported.
- Enemy target selection ran uniform-random over alive players when
  not Beckon-redirected.
- Absorber's Skills submenu contained absorbed moves and Beckon;
  non-Absorbers' Skills submenu did not render Beckon at all and did
  not list absorbed moves.
- Beckon target-redirect verified: P1 Absorber beckoning E3, E3's
  turn attacked P1 with its learnable, P1 absorbed on perfect parry,
  absorbed move appeared in P1's Skills submenu. Multi-Beckon
  stacking (post-C10) verified by beckoning multiple enemies in one
  turn cycle — each enemy's force-learnable fired when its turn
  arrived; the picker excluded enemies already in the set.
- Positioning outcomes after C11.1/2/3 landed and the calibration
  restore tune (`SpriteContentYOffset = 52f`, damage-number
  multiplier 0.25): damage numbers, hop-in midpoint circles, magic
  circle anchor, and effect spawns aligned with visible sprite bodies
  across all slots both sides. Target highlight (yellow per-sprite
  shader uniform) replaced the pointer as the sole selection cue.
- Victory fired when all 8 enemies were dead. GameOver fired when all
  4 players were dead.
- Phase 2 transition was suppressed when `TestFullParty` was active;
  the `[TEST] TestFullParty suppresses Phase 1 → Phase 2 transition.`
  log line appeared at scene start as expected.

**End-of-Phase-6 outcomes, 1v1 (default config, `TestFullParty =
false`):**

- Queue emitted P1 E1 P1 E1…, bit-identical turn flow to pre-Phase-6.
- Phase 2 transition worked exactly as before (Phase2EnemyData
  fallback loaded; Phase 1 boss death triggered the reveal and
  transition).
- All other combat mechanics (parry counter, absorb, cure, ether
  item, defend, beckon-force-learnable) regression-passed.
- Intro dialogue played, then the menu appeared.

---

## 8. Resolved design questions

All Phase 6 design questions resolved during the chunk work.

- **Q1. Roster shape.** 1v1 shipped as the default config; 4v8 as
  `TestFullParty` (originally 4v5; bumped to 4v8 in the
  C7-extra-followup commit so the staggered enemy grid filled both
  rows × all four columns and the row/col-derived Z fix was
  exercised — 4v5 had left cols 1 and 3 empty). Both run through the
  same queue-driven machinery.
- **Q2. Roster wiring.** `[Export] int PlayerPartySize /
  EnemyPartySize`, defaulting to 1/1. Typed-array promotion
  (`EnemyData[]` per slot) deferred past Phase 6 (see §10).
- **Q3. Enemy target selection.** Uniform random over alive players
  when not Beckon-redirected.
- **Q4. Defend semantics.** Per-player; cleared on the defender's own
  next ShowMenu or on death; concurrent Defenders allowed.
- **Q5. Overlay panel extraction.** `BottomCenteredOverlayPanel`
  helper deferred — structural-only refactor with no UX rule changes
  to BattleDialogue or BattleMessage; not landed in Phase 6 (see
  §10).
- **Q6. Phase 2 transition under TestFullParty.** Suppressed when the
  flag is active; `Phase2EnemyData = null` set in the test-flag
  resolution block and logged.
- **Q7. Mid-turn deaths.** Resolved by C5's queue (filters dead
  combatants at `Advance()`) plus C9's per-target death handling
  (`KillCombatant` helper at each TakeDamage site, with combo
  `_comboTargetDied` flag and offensive-magic dead-defender guard
  for mid-sequence cancellation). A dying combatant's in-flight
  sequence runs to completion; the queue then skips the dead
  combatant's subsequent turn slots. Death during a sequence does
  not cancel the in-flight sequence even when the dying unit is the
  active attacker.

---

## 9. Closeout

Phase 6 converted the battle system from a 1v1 prototype to
multi-character machinery driven by a tick-based AP scheduler. Major
additions: queue-driven turn flow, per-slot HP/MP panels, staggered
formation grid, active-combatant indicators, multi-target picker
with per-target death handling, Beckon target-redirect (with
multi-Beckon stacking), and the C11 positioning architecture
migration moving pointer + magic anchor + effect geometry off
ColorRect-derived positions onto sprite-derived `AnimSpriteOrigin`
with a calibration-restore `SpriteContentYOffset`.

Two roster configurations exercised the runtime: 1v1 (`TestFullParty
= false`, default) and 4v8 (`TestFullParty = true`). Both ran
end-to-end through the same queue-driven machinery — 1v1 is an
emergent output of the queue at `PlayerPartySize 1 / EnemyPartySize 1`,
not a separate code path. The Phase 1 → Phase 2 boss transition works
at 1v1; at 4v8 the transition is explicitly suppressed (multi-unit
transition is future work, see §10).

This document is now retrospective. New work tracks in successor
phase plans. Items deferred from Phase 6 are catalogued in §10
below.

---

## 10. Deferred from Phase 6

Catalogue of work surfaced during the Phase 6 chunks that did not
land in scope. Architecture-level follow-ups cross-reference
`CLAUDE.md`'s "Deferred Architecture Work" subsection rather than
duplicating overlapping items.

### Position / layout follow-ups

- **Per-sprite content-top authoring for `TargetPointer` (C11.1).**
  The pointer is gated off behind `ShowTargetPointer = false`;
  re-enabling it would benefit from per-character head-offset
  metadata so the tip lands on the visible head rather than at a
  multiplier-derived fraction above sprite center. The yellow
  per-sprite highlight does the selection job alone in the
  meantime.
- **`SpriteContentYOffset` asymmetry split (C11.3).** The single
  `52f` constant currently doubles as "frame-center → body-center"
  (effects / midpoint) AND "frame-top → content-top" (damage
  numbers). Split into `SpriteContentCenterOffset` and
  `SpriteContentTopOffset` if asymmetry surfaces during broader
  playtesting.
- **Per-sprite damage-number multiplier (C11.3).** The current
  `0.25f` multiplier in `ComputeDamageOrigin` works across the
  Phase 6 sprite roster (Knight 240px, Warrior 390px, 8 Sword
  Warrior 480px rendered). Per-character content data would be a
  follow-up if a future sprite needs different proportional
  placement.
- **`FloorY` / anchor refactor (C11.3).** Derive
  `PlayerFrontAnchor` / `PlayerBackAnchor` / `PlayerFrontAnchorRect`
  / `PlayerBackAnchorRect` from `FloorY` at scene init so the
  4-constant lockstep self-maintains. Currently the four anchor Y
  values are hand-tuned to match `FloorY`-derived positions and
  must shift in unison when `FloorY` changes (as in C11.3's −50px
  lift).
- **`BottomCenteredOverlayPanel` helper (Q5).** `BattleDialogue` and
  `BattleMessage` continue to duplicate the bottom-anchored
  `MakeLayeredPanel` + `modulate:a` fade construction. Structural
  extraction out of Phase 6 scope; both classes lift together via
  the shared `OverlayBottomInset` constant in the meantime.

### UX follow-ups

- **Cure-on-self input-vs-feedback disconnect (C11.2).** Timing
  input lands at static center; heal effect lands on the
  recipient's sprite. Acceptable consistency tradeoff today; revisit
  if it reads jarringly during broader playtesting.
- **Beckon picker filter for already-absorbed learnables (C10
  area).** The picker shows the enemy even when the absorber has
  already absorbed its learnable; force-learnable falls through
  gracefully when `LearnableAttack` is null or already absorbed.
  Could add a picker-side filter as cosmetic polish.

### Roster / scope follow-ups

- **Typed-array roster config (Q2).** `[Export] EnemyData[]` per
  slot. Today `PlayerPartySize` / `EnemyPartySize` are integers
  with uniform copies (all Knights / all Warriors). Promotion
  deferred past Phase 6 per original scope.
- **Friendly-fire damage exposure (`AttackData.CanFriendlyFire`).**
  Architecture supports same-side targeting by construction
  (`Combatant.TakeDamage` is attacker-agnostic; receiver-side fork
  on `Side` equality), but no menu option exposes friendly fire.
- **Multi-unit Phase 1 → Phase 2 transition.** At 1v1 the transition
  works as before; at 4v8 the test flag explicitly suppresses it via
  `Phase2EnemyData = null` in the test-flag resolution block.
  Designing the transition for an N-enemy formation is future work.

### Architecture follow-ups

Catalogued in `CLAUDE.md`'s "Deferred Architecture Work" subsection;
listed here for cross-reference completeness:

- Handler signature generalisation (Option A from §6 — deferred
  until multi-character density actually needs per-unit handler
  dispatch).
- `AttackStep.Offset` / `PlayerOffset` schema consolidation (audit
  D5).
- `PlayCombatantHurtFlash` load-bearing singleton-binding on the
  paired `OnEnemyHurtFlashFinished` callback.
- Parry-counter refactor to route through
  `BattleSystem.StartSequence` (currently a hand-rolled `CreateTimer`
  cascade in `BattleAnimator.cs PlayParryCounter`).
