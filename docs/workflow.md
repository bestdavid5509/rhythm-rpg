# Workflow Conventions

How chat-Claude, Claude Code, and the user collaborate on this project.
Captures the human-AI workflow that has emerged over the Phase 3–5 arc so
fresh sessions can ramp up on it without re-deriving from scratch. Separate
from CLAUDE.md (which describes the codebase) and docs/design-notes.md
(which captures forward-looking gameplay design) — this file is about
process, not product.

---

## Two-Claude architecture

The project uses two Claude instances in different roles:

- **Chat-Claude** (Claude.ai web/desktop interface): planning, architectural
  decisions, design discussions, diff review, prompt drafting for Claude
  Code. Does not edit files in the project directly. Operates on context
  the user provides (pasted reports, uploaded diff files, plan-file content).
- **Claude Code** (CLI in the terminal, run from the project directory):
  codebase-level execution. Edits files, runs builds and tests, executes
  greps, stages git changes, writes diff files to disk. Operates on the
  live working tree and reports results back to the user in-terminal.

**Flow:** user interacts with chat-Claude for planning → chat-Claude drafts
a prompt the user pastes into Claude Code → Claude Code executes and
reports + writes the staged diff to a file → user uploads the diff file to
chat-Claude for review → iterate on feedback → on approval, user tells
Claude Code to commit and push.

Neither Claude instance has a direct channel to the other. The user is the
bridge. This is a feature, not a limitation — it forces each stage to
produce an artifact (a prompt, a diff, a report) that the other side can
scrutinise.

---

## Plan mode usage

Plan mode is a Claude Code feature that forces a plan-first workflow — the
agent surveys the codebase, writes a plan to a designated plan file, and
pauses for approval before executing. Used selectively:

- **Plan mode ON** for architecturally-loaded work: first-of-its-kind
  features (Phase 4's SelectingTarget, Phase 5's threat reveal), schema
  changes, refactor arcs (Phase 3's chunks), cross-cutting concerns,
  anything where scope could surprise us during execution.
- **Plan mode OFF** for mechanical execution: applying an already-approved
  plan, small tuning commits, well-scoped follow-ups (menu keybind
  additions, shader value tweaks), documentation edits.

Plans land in a file managed by Claude Code's plan-mode tooling —
typically a markdown file whose path Claude Code announces at plan-mode
entry. Chat-Claude reviews the plan file's contents before approving via
ExitPlanMode. After execution, the plan file is stale context; the next
plan-mode session overwrites it.

**Rule of thumb:** if chat-Claude thinks the request might surface
unexpected couplings during survey, plan mode ON. If the change is clearly
scoped ahead of time and mechanical to apply, plan mode OFF. When in
doubt, ON — the cost of a planning session is small compared to the cost
of an under-scoped commit that needs re-work.

---

## Pre-commit diff review pattern

The core quality-control loop. Every commit — with rare exceptions for the
most trivial docs fixes, and even those usually reviewed — goes through:

1. Claude Code completes edits in the working tree.
2. Claude Code runs build + headless-load verification (`dotnet build`,
   Godot `--headless --quit`).
3. Claude Code stages changes: `git add -A` or an explicit file list.
4. Claude Code writes the staged diff to a file:
   `git diff --cached > ../claude_review/<phase-or-feature-name>-review.txt`.
5. Claude Code reports to the user: implementation summary, grep
   verification results, mid-execution deviations from the plan.
6. Claude Code **pauses** — does NOT commit.
7. User uploads the diff file to chat-Claude.
8. Chat-Claude reviews the diff, asks questions or flags issues, approves
   or requests changes.
9. On approval, the user tells Claude Code to commit with the pre-agreed
   commit message. Claude Code commits and pushes.

The `../claude_review/` directory lives outside the project repo to keep
review artifacts out of git. Diff file names are descriptive
(`phase-4-review.txt`, `phase-5-tuning-review.txt`, `beckon-design-note-review.txt`)
so multiple review passes on the same work overwrite predictably.

**Why this matters:** Claude Code is capable of silently expanding scope
or misinterpreting instructions. The diff review is where chat-Claude
catches those before they land in a commit. Historically about 1 in 3
commits on this project has required a push-back, a tuning pass, or a
re-review before landing. The diff-review gate is what makes that rate
observable and correctable.

---

## Claude Code invocation

- Always verify `/model opusplan` is active at the start of a Claude Code
  session. Current model selection for this project; may change if
  Anthropic updates model offerings. (Convention: the user types
  `Confirm /model opusplan is active` at the top of each Claude Code
  prompt as a reminder; Claude Code acknowledges but cannot self-verify
  the slash-command state.)
- Working directory is the project root (`C:\Users\dtbes\Documents\rhythm-rpg`).
- Claude Code uses its own `TodoWrite` for execution tracking. Users don't
  need to interact with this; it surfaces in the terminal output.
- For long-running investigations (multi-file surveys, codebase-wide
  catalogues), Claude Code can delegate to internal Haiku agents via its
  Explore tooling — fine to let it happen when the user's prompt
  explicitly allows or when scope warrants.

---

## Commit message conventions

Conventional Commits format with a battle-system scope:

- `feat(battle):` — new features in the battle system.
- `fix(battle):` — bug fixes in the battle system.
- `refactor(battle):` — behaviour-preserving code reorganisation.
- `tune(battle):` — visual, timing, or numerical tuning that doesn't
  change mechanics (e.g. shader values, animation durations).
- `docs:` — documentation-only changes (no scope prefix; docs are
  project-wide).
- `chore:` — tooling, dependency updates, non-code maintenance.

Messages are imperative mood (`add`, not `added`). Phase tag at the end in
parentheses when relevant, e.g.
`feat(battle): add SelectingTarget state and player pointer (Phase 4)`.
Keep the subject line under ~72 characters when practical.

- Commits do NOT include a `Co-Authored-By` trailer or any AI-attribution
  trailer. Commit authorship is the committer, full stop.

Commit body is used only when the scope needs explanation beyond the
subject. Most commits don't need one — the pre-commit diff review already
captured the rationale for the reviewer.

---

## Branch and push cadence

- Feature work happens on dedicated branches (e.g.
  `target-selection-and-scaffolding` for the Phase 3–5 arc).
- Push after each reviewed commit. Don't batch multiple commits locally —
  each commit should be independently reviewed and pushed so the remote
  reflects the review history as it actually happened.
- Merging to `main` is a separate explicit decision, not automatic.
  Typically happens after a phase or arc is stable.
- Pre-refactor exploratory work stays on separate branches from the
  eventual implementation branch.

---

## Mid-execution deviations

When Claude Code encounters something during execution that wasn't in the
approved plan:

- **Flag it explicitly** in the pre-commit report as a "mid-execution
  deviation." Use a dedicated section heading in the report so the
  reviewer can't miss it.
- **Don't silently expand scope.** A deviation that lands in the diff
  without being flagged is a trust-erosion event — see the Anti-patterns
  section.
- **Explain the reasoning.** What was discovered, why the change was
  necessary or preferable, what the alternative would have been. The
  reviewer needs enough context to judge whether the deviation is
  acceptable or should be reverted.
- **Offer a revert path** when the deviation introduced additional scope,
  so chat-Claude or the user can choose to drop the change back to the
  approved plan shape. Example: "If you prefer the minimalist version, a
  ~10-line backout restores the pre-deviation state."

**Historical examples worth referencing:**

- The `fromSubmenu: bool` → `MenuContext` enum migration during the Ether
  routing commit: the three-menu-context reality (main / Absorbed Moves /
  Items) only surfaced during execution. Flagged as scope extension with
  reasoning and a revert-to-minimalist option.
- The `AddMenuDivider` definition-location verification during the Game
  Over / Victory visual parity refactor: verified `AddMenuDivider` lived
  outside the deletion path before removing its caller, to avoid
  accidentally orphaning the method.

---

## Anti-patterns

Documented for durability — these are the failure modes we've seen
(or narrowly avoided) on this project:

- **Committing without diff review.** Removes the quality-control loop.
  Never happens except for trivial documentation corrections, and even
  those are usually reviewed.
- **Silent scope expansion.** A mid-execution deviation that lands in the
  commit without being flagged in the report. The fix is the dedicated
  "mid-execution deviations" section in the pre-commit report.
- **Commit message mismatch.** Commit message says "fix X" but the diff
  also tunes Y and refactors Z. If scope drifted during execution, either
  drop the extra scope or update the commit message to reflect reality.
  Mismatches make the git log unreliable for future archaeology.
- **Putting workflow conventions in CLAUDE.md.** Workflow lives in this
  file; CLAUDE.md is for codebase architecture. Keep the separation so
  each document has a single clear purpose.
- **Trusting Claude Code's report without reading the diff.** Chat-Claude
  should always look at the actual code changes, not just the summary.
  Reports describe intent; diffs describe reality. When they diverge —
  rarely but it happens — the diff wins.
