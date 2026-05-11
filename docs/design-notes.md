# Design Notes

Forward-looking design thinking for systems not yet implemented. Separate from
CLAUDE.md (which describes what the codebase *is*) — this doc captures where
design is *going*, so reasoning isn't lost between sessions.

---

## Friendly fire / same-side targeting

**Status:** architecturally supported but not exposed as a gameplay feature.

### Design principle

Same-side targeting (ally-heal, ally-buff, friendly-fire damage, self-damage) is
naturally supported by animation patterns that are already target-agnostic. The
architectural refactor in Phase 3 made this work without special-casing.

The emergent rule: **non-hop-in attacks are target-flexible by construction;
hop-in melee attacks are implicitly enemy-only.**

### Why non-hop-in is easy

Magic, cast, and ranged attacks don't assume attacker-defender positional
asymmetry. Attacker stays at origin, effect spawns at target's position,
resolution is "apply effect at target coordinate." Target can be anywhere — self,
ally, enemy — and the animation holds up.

Geometric FlipH / offset logic (Phase 3.5's `attackerOnRight` check in
`BattleSystem.SpawnEffectSprite`) already handles direction correctly for any
attacker-target pair, regardless of side relationship.

Combatant's `TakeDamage(int)` / `Heal(int)` methods (Phase 3.6) are receiver-only
operations — no attacker identity needed. Same-side targeting works without any
method changes.

### Why hop-in is impractical

The hop-in animation assumes attacker approaches a defender who is elsewhere,
lunges into them, then hops back. Self-hop-in would mean lunging into your own
position — absurd. Ally-hop-in would mean lunging into someone on your own side,
which breaks the combat-center camera framing. Combo Strike's three-pass sequence
on self or ally would compound the problem (and the hurt animation choreography
for self-damage is its own design headache).

Hop-in attacks should stay categorically enemy-only. If some future ability needs
self-hop-in semantics (self-buff leap, acrobatic cast), it should be a bespoke
animation rather than routing through the existing hop-in system.

### Likely implementation shape (when friendly-fire is added)

- **Per-attack opt-in:** `AttackData.CanFriendlyFire: bool` (default false), only
  meaningful for non-hop-in attacks. Target selection UI respects it.
- **Parry-counter suppression:** parry-counter fires only when
  `attacker.Side != target.Side`. Prevents allies auto-countering each other
  when friendly-fire damage is parried.
- **Target pool dispatch:** attacks with `CanFriendlyFire = true` expand the
  valid target pool to include allies (and possibly self) during target selection.
- **Game-over check:** ally-damage that could reduce an ally's HP to zero needs
  standard game-over handling. The current "skip game-over on same-side" branch
  in `OnPlayerMagicSequenceCompleted` is correct for heal but would
  need to narrow to "skip game-over on heal specifically" when ally-damage
  becomes real. Attack-identity check would replace the side-equality predicate
  at that one site.

### Why this matters for gameplay

Friendly-fire as a design lever gives old/weak spells late-game value:
- Ally-heal: Cure on other party members, not just self.
- Self-damage trades: sacrifice HP to trigger low-HP abilities or costs.
- Tactical friendly-fire: AoE that hits allies, forcing positioning choices.
- Late-game utility for early-game spells: weak damage spell becomes useful
  self-damage tool.

No current ability exercises this, but the architecture supports it without
further refactoring when the gameplay need arises.

---

## Beckon — dual-purpose absorption setup

**Status:** shipped in Phase 6 (force-learnable via C1; target-redirect
mechanical wiring via C9; Absorber-only learnable signal gate +
multi-Beckon HashSet refinement via C10).

### Design principle

Beckon is the Absorber's tool for manufacturing an absorption opportunity. It
has two simultaneous effects from a single menu action:

1. **Force the enemy's attack selection** to use their learnable attack on
   their next turn.
2. **Redirect the enemy's target** to the Beckoner. If the enemy was otherwise
   going to attack Combatant A, and Combatant B uses Beckon, the enemy's
   next attack lands on Combatant B instead.

Both effects are load-bearing together. The Absorber needs the enemy to use
a learnable attack **and** to aim it at the Absorber — only that combination
produces a parryable-and-absorbable learnable move. One-half-Beckon would
either waste the learnable on a non-Absorber (can be parried, can't be
absorbed) or force the Absorber to wait for the enemy's natural targeting to
coincide with a learnable selection.

### Absorption rule

Only the Absorber character can learn a move from a parry. (Tutorial content
may grant one other character a one-off absorption for narrative reasons —
TBD at implementation time.)

Absorption requires **all three** conditions:

1. The enemy is using a **learnable** attack — same condition that fires the
   white-flash signal on the enemy sprite today.
2. The attack is **targeted at the Absorber**.
3. The Absorber **perfect-parries** the attack (no missed inputs across the
   full sequence).

Non-Absorber characters can still parry a learnable attack to avoid damage,
but the move stays with the enemy — no absorption. This asymmetry is what
makes Beckon's target-redirect half necessary. Without it, the Absorber's
absorption opportunities are gated on the enemy's natural targeting
coincidentally aligning with a learnable selection — too rare to build a
progression system around.

### Shipped implementation

Both halves are live. `Combatant.BeckoningTargets: HashSet<Combatant>`
(player-only) is populated by the Beckon menu option after the picker
resolves an enemy target. On each enemy turn, `SelectEnemyAttack`
scans `_playerParty` for any beckoner whose set contains the active
enemy and returns `EnemyData.LearnableAttack`, consuming that entry
from the set; `SelectEnemyTarget` does the same scan read-only to
resolve the enemy's defender to the beckoner. The HashSet form
supports stacking concurrent Beckons across different enemies — each
beckoned enemy's force-learnable fires when its turn arrives. The
picker excludes enemies already in the active player's set so the
same enemy cannot be beckoned twice. `KillCombatant` removes a dying
enemy from every player's set so a stale entry cannot fire a redirect.

### Shipped model — Option A (refined)

C1 introduced `BeckoningTarget: Combatant?` (single nullable ref) on
the Beckoner side, retiring the pre-existing boolean placeholder.
C10's multi-Beckon stacking enhancement then refactored this to
`BeckoningTargets: HashSet<Combatant>` so successive Beckons on
different enemies accumulate rather than overwriting. The Beckoning
character is the one carrying the state; enemy-side target selection
scans the party at decision time (`SelectEnemyAttack` /
`SelectEnemyTarget`).

Option B (`BeckonedBy: Combatant?` on the enemy side) was considered
but not selected — its concern was that caching the decision on the
enemy at Beckon time couples Beckoner and target state. The
Beckoner-side scan that Option A required at enemy-target-selection
time shipped without friction.

The single-action dual-effect contract held: one Beckon menu
selection populates the HashSet entry that drives both the
force-learnable selection and the target-redirect in the same enemy
turn. The player doesn't pick which half to activate.

### Interaction with threat-reveal (Phase 5)

The threat-reveal flash introduced in Phase 5 reads from wherever the
enemy's target decision lives and fires on the resolved target — the
output of natural target selection after Beckon-redirect is applied.
No Phase-5-specific Beckon handling is needed; the flash machinery
iterates `_threatenedCombatants` (populated from the enemy's
current-turn target) regardless of how that list was decided.

The Beckon turn sequence is:

- Player picks Beckon → target picker resolves an enemy →
  `BeckoningTargets.Add(enemy)` on the active player → MP deducted →
  queue advances.
- Beckoned enemy's turn begins → `SelectEnemyTarget` scans for a
  beckoner whose set contains this enemy → target resolves to the
  Beckoner → `_threatenedCombatants` populated with the Beckoner →
  threat-reveal fires on the Beckoner (matching the player's
  expectation: "I called it; now it comes for me").
- `SelectEnemyAttack` performs the same scan, consumes the set entry,
  and returns the learnable attack → enemy white-flash fires
  (learnable signal, gated on `target.IsAbsorber` for the
  introspective "If I watch carefully…" text) → attack proceeds.
- Both visual signals (enemy white flash + Beckoner red tint) run
  concurrently on their respective sprites, consistent with the
  Phase 5 composite-shader design.

### Why this matters for gameplay

Beckon is how a skilled player accelerates the Absorber's library growth.
Without it, absorption is a coincidence of enemy choice. With it, each
absorption is something the player earned — they set up the opportunity,
they parried the timing perfectly, they got the reward.

The design also creates a risk/reward tension: Beckon pulls a hard-hitting
learnable attack directly onto the Absorber, who is often the most
progression-important character to keep alive. A mistimed Beckon into a
missed parry loses HP on the exact character the player most wants to
protect. That tension is the intended loop.
