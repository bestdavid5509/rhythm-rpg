# Target Selection + Multi-Character Scaffolding Plan

## When to run
Before the Phase 1 code review. The scaffolding exercise will surface
layout issues and 1v1-baked architectural assumptions that make the
code review's findings sharper and more concrete. Target selection is
a prerequisite for the scaffolding to be meaningful (without it, you
can't meaningfully test effect positioning, attack routing, or layout
density).

## Setup
Consider `/model opusplan` in Claude Code. The analytical portions
(layout reasoning, surfacing baked-in assumptions, proposing new
positioning schemes) benefit from Opus; the mechanical portions
(sprite duplication, straightforward feature implementation) are fine
on Sonnet.

## Scope estimate
Multi-session. Part 1 (target selection, full bidirectional) is
realistically 2-3 focused sessions. Part 2 (scaffolding exercise) is
probably 1 more. Don't try to compress.

## Part 1: Target selection feature (bidirectional)

### Player-side target pointer — design decisions (fixed)
- **A-always behavior:** the target pointer appears for every attack,
  even in 1v1. Player always presses confirm to commit the target.
  Rationale: consistent combat flow as party size grows, early
  muscle-memory formation, deliberate-input sensibility matching the
  existing parry system. Tradeoff: extra input per attack in 1v1.
- **Applies to:** offensive actions (Attack, absorbed moves that
  target enemies), defensive/support actions (heals, buffs — target
  allies including self).
- **Doesn't apply to:** global actions (Beckon, Defend) where there's
  no target concept.

### Enemy-side target indicator — design decisions (fixed)
- **When it appears:** at the start of the enemy's turn, as soon as
  the target is decided. Persists through wind-up and the full
  attack sequence. Fades when the sequence completes.
- **Rationale:** the player needs to know who's being attacked in
  time to react with parries. Showing the target early supports the
  parry system's demand on player attention, and matches the game's
  design intent that tension comes from resource management and
  timing — not from target uncertainty.
- **Visual distinction from player pointer:** the enemy indicator
  should look visually distinct from the player's target pointer.
  Different semantics — the player's pointer is active choice
  (navigational), the enemy's indicator is warning (threatening).
  Shared visuals risk conflating "I'm picking" with "something's
  coming." Specific visual to be decided during implementation;
  likely a more urgent/threatening register than the player's
  pointer (e.g. red vs. neutral; crosshair vs. arrow).
- **Multi-target readiness:** the indicator system should naturally
  handle multiple simultaneous targets. An all-party attack
  displays indicators on every ally, no special case. When chain or
  cascading attacks are eventually built, that's a separate design
  decision — not a concern for this exercise.

### Design decisions (deferred)
- **Pointer visuals** (sprite? highlight on target? both?) — decide
  during implementation based on what reads cleanly against the
  existing combat UI and the scaffolding exercise's findings.
- **Multi-target attacks** (AoE, cleave, chain) — not yet in scope.
  Single-target selection only for now; indicator system just needs
  to *accommodate* multiple simultaneous targets structurally.
- **Cancel-back-to-action-menu timing** — should the player be able
  to back out of target selection after picking an action? Decide
  during implementation.

### Implementation sketch
- New `SelectingTarget` battle state integrated into `BattleMenu` /
  `BattleTest` flow. On entering: show player pointer on first valid
  target. Input: `ui_left` / `ui_right` (or similar) to cycle
  between valid targets, `battle_confirm` to commit, `ui_cancel` to
  return to action menu.
- Valid target pool depends on action type: Attack → enemies; Heal
  → allies (including self); Beckon → no target; etc. Actions
  declare their valid target category.
- Player pointer spawned on the selected target — positioned
  relative to target's sprite (e.g., 20px above target's head).
- After confirm, existing attack flow proceeds with the selected
  target as the defender.
- Enemy indicator: when `BeginEnemyAttack` (or equivalent) fires,
  spawn enemy indicator on the chosen player target before the
  attack wind-up starts. Free the indicator on sequence completion.

### Integration — audit and replace 1v1 assumptions
The existing attack pipeline assumes a single defender. Before or
alongside target selection implementation, audit and replace:
- `BattleSystem.StartSequence` / `SpawnEffectSprite` — currently
  assume "the enemy" or "the player" as defender. Replace with
  "the targeted unit" (attacker and defender passed in).
- `ComputeCameraMidpoint` — depends on attacker-defender pair;
  confirm it still makes sense with arbitrary pairs.
- `TargetZone` positioning — currently centered between the one
  player and the one enemy. Decide: does it stay centered between
  attacker and target, or find a new anchor?
- Effect positioning formula (`Position = (defenderCenter.X,
  FloorY) + step.Offset`) — replace single-enemy reference with
  "defender center" for the currently targeted unit.
- Any hardcoded `_enemyAnimSprite` / `_playerAnimSprite` references
  in attack resolution that should be "the target sprite" /
  "the attacker sprite."

Produce an audit document as part of this work listing every
location where a 1v1 assumption was found and how it was replaced.
Useful reference for the scaffolding exercise and the code review.

## Part 2: Multi-character scaffolding exercise

### Goal
Produce **findings, not fixes**. Output is documentation of:
- What layout values are hardcoded for 1v1
- What breaks or overlaps at higher density
- Proposed positioning scheme for up to 4 players / 5 enemies
- Confidence levels on each proposal
- UI elements that need rethinking (dialogue placement, HP panels,
  target pointer positioning)

Resist the pull to rearchitect during the exercise. Fixes come
later, during the code review cleanup phase.

### Setup
- Duplicate existing sprites as stand-ins: 4 copies of the Knight
  sprite, 5 copies of the Warrior Phase 1 sprite. These are
  throwaway test assets — don't commit them as permanent content,
  or if committed, clearly mark as scaffolding-only.
- Create a test scene or test mode that instantiates the scaled-up
  party and enemy line-up. Doesn't need to be a playable battle;
  needs to be a layout preview with at least enough behavior to
  verify targeting works.

### What to test
With target selection in hand:
- **Sprite spacing** — how far apart should 4 players be? 5
  enemies? Does the existing FloorY formula support this, or does
  it need to become a pair of formulas (floor line + per-unit
  horizontal offset from a centered anchor)?
- **Effect placement** — when attacking enemy #3 of 5, does the
  slash effect land correctly? When healing ally #2 of 4, does the
  buff sprite center on them?
- **Dialogue panel placement** — with party HP panels stacked at
  the bottom (4 panels), does the current dialogue placement still
  work? Does it need to move to the top? Somewhere else?
- **Enemy HP panel placement** — currently top-right for one
  enemy. Where do 5 go?
- **Camera / framing** — does the existing camera show all
  characters at density, or does it need to zoom out? Does framing
  change dynamically based on party size?
- **Target pointer visibility** — can the player clearly tell
  which of 5 enemies is currently selected?
- **Enemy indicator visibility** — can the player clearly tell
  which of 4 allies is being targeted?
- **Multiple simultaneous indicators** — if an all-party attack
  fires, is the visual noise manageable?

### Output
A new document (e.g. `docs/multi-character-findings.md`) or a
dedicated section in CLAUDE.md under Architectural Decisions,
listing:
- Each 1v1-baked assumption identified
- Each layout issue observed at density
- Proposed resolution (with confidence)
- Scope category (code-review cleanup / Phase 2 work / post-MVP)

This document feeds directly into the Phase 1 code review,
sharpening its findings and giving the review concrete examples to
work against.

## After this exercise
- Phase 1 code review runs with targeting and scaffolding findings
  in hand
- Cleanup phase after review addresses blockers
- Phase 2 (dungeon + regular encounters) builds on the cleaned
  foundation

## One clear non-goal
This is not the place to actually implement multi-character party
or multi-enemy battles. That's Phase 2 work. This exercise is
*preparatory* — it ensures Phase 2 doesn't start against a
foundation that assumes a shape it won't keep.
