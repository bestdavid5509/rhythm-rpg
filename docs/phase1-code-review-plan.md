# Phase 1 Code Review Plan

## When to run
After Phase 1 is feature-complete (intro dialogue, victory polish, final
CLAUDE.md session update) and before starting Phase 2 dungeon work.

## Setup
Start a fresh Claude Code session with `/model opusplan`. The hybrid
mode uses Opus for the review/analysis phase and Sonnet for mechanical
refactor execution, minimizing Opus token spend while getting its
reasoning quality where it matters.

## Review philosophy
Treat CLAUDE.md as a set of claims to verify, not as ground truth.
For each architectural invariant, dead-flag guard pattern, and
convention documented in CLAUDE.md, check the actual code and report
any drift. Also look for issues the documentation doesn't mention.

A curated description of code hides exactly the sort of accumulated
mess a review should find. Drift is a finding, not a blind spot.

## Pass 0: Scope the review
Before running any narrow passes, Claude Code does a broad survey of
the codebase and proposes additional review categories based on what
it actually sees. This guards against blind spots in the initial
pass list below — which was drafted from CLAUDE.md and conversation
history, not from reading the actual source.

Prompt: "Survey the codebase at a high level. Propose 3-5 additional
review categories beyond the ones listed below that you think would
surface meaningful issues in this specific codebase. For each, give
one example of a concrete issue that pass would catch. Do not
actually run any passes yet."

The human then picks which proposed categories to add to the final
pass list.

## Starting pass list (not exhaustive — expand via Pass 0)
Each pass gets its own focused prompt to Claude Code. Narrow passes
produce actionable findings; global "review everything" requests
produce vague gestures.

1. **Dead-flag guard coverage** — Find any `_playerAnimSprite.Play(...)`,
   `Stop()`, `SetFrame(...)`, or equivalent sprite interactions that
   don't route through the guarded helpers (`PlayPlayer`,
   `PlayPlayerBackwards`, `StopPlayer`, `SetPlayerFrame`, `PlayEnemy`).
   These are latent death-pose-override bugs waiting to surface.

2. **Signal connect/disconnect discipline** — Find any `+=` to an
   `AnimationFinished` or similar signal that isn't preceded by a
   `SafeDisconnect` call. Handler stacking silently accumulates across
   turns; the pattern is a convention, not enforced by the compiler.

3. **File size and responsibility creep** — Which of `BattleTest.cs`,
   `BattleAnimator.cs`, `BattleMenu.cs` is growing fastest? Any
   functions that have grown beyond their documented responsibility?
   The original three-file split was token-motivated; if any file is
   trending back toward unmanageable size, identify why.

4. **Timer management** — Where are `GetTree().CreateTimer(...)` calls
   made? Are their callbacks guarded against scene reload
   (`IsInstanceValid(this)` checks)? Are any timers orphaned —
   created but with no path to cancel them if state changes?

5. **Convention drift** — Magic numbers that should be constants
   (check against the documented `FloorY`, `TargetRadius`,
   `RingLineWidth` pattern). Hardcoded asset paths that should be
   `[Export]` fields. Inspector-exposed values that should be
   hardcoded constants (or vice versa).

6. **Architectural consistency** — The counter-attack refactor is one
   example already on the Known Next Steps list (hand-rolled timer
   cascade vs. `BattleSystem.StartSequence`). What else fits this
   category? Places where similar logic is implemented two different
   ways in two different files, for no principled reason.

7. **Dead code and unused parameters** — `AddPlayerAnimationMixed` is
   documented as retained for future use but currently unused. What
   else is like this? Document the ones worth keeping; delete the
   rest.

## Output
A Technical Debt Ledger — new section in CLAUDE.md (or a separate
`TECHNICAL_DEBT.md`) categorizing findings:

**By severity:**
- Blocker — must fix before Phase 2
- Important — should fix before Phase 2 if time allows
- Nice-to-have — file and defer

**By scope:**
- 1 session — can be fixed in a focused day
- Multi-session — needs a dedicated mini-project
- Post-MVP — defer until after the game is playable end-to-end

## After the review
Don't treat the ledger as a mandate to fix everything. Pick blockers,
maybe some importants, fix those. The rest stays in the ledger as
documented technical debt the future you (or future Claude Code
session) knows about.

Then — and only then — start Phase 2 work.
