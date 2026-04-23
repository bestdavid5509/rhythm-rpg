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
  in `OnPlayerMagicSequenceCompleted` (line ~1707) is correct for heal but would
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
