# Combatant Abstraction — Design Proposal

## What this is
Proposed answers to the 8 design questions surfaced by the target-selection
audit (`docs/target-selection-audit.md`, "Design questions surfaced by the
audit"). Written before Phase 3 begins so the refactor has a concrete
specification to implement against.

## What this decides
The shape of the `Combatant` abstraction and the supporting architecture
around it — class form, ownership, placement in the codebase, signal
contracts, and the migration approach for the Phase 3 refactor.

## What this does NOT decide
- Visual design of the target pointer or enemy indicator (Phase 4/5).
- Exact list of valid-target rules per action type (Phase 4 implementation).
- Status panel layout at 4v5 density (Phase 6 scaffolding exercise).
- Whether `AttackStep.Offset` / `PlayerOffset` collapses into one field (D5 in
  audit; deferred to post-scaffolding).
- Enemy AI target-selection strategies (deferred; prototype uses first-living-ally).

## Non-goals (explicitly out of scope)
Per the project roadmap, this abstraction is not designed to support:
- Dynamic party-size changes mid-combat (no summons, no revives that add a slot, no enemy reinforcements).
- Factions beyond Player / Enemy (no neutrals, no charmed-ally-temporarily-enemy, no three-side combat).
- Abstract ally-swap mechanics (no party-member substitution during a battle).

If any of these become design requirements later, the abstraction gets revised then. Building flexibility for them now would be premature.

---

## Summary

| # | Question | Proposed answer | Confidence |
|---|---|---|---|
| 1 | What is Combatant? | Plain C# class, non-abstract, with a `Side` enum field; side-specific fields on the same class | **High** |
| 2 | Sprite node ownership | Combatant holds references to sprites; scene tree owns lifecycle | **High** |
| 3 | Architectural home | `List<Combatant>` fields on `BattleTest` | **High** |
| 4 | Party representation | Dynamic `List<Combatant>`, no fixed-size slots | **High** |
| 5 | Incremental vs. up-front | Combatant-first with single-entry party lists for 1v1; scaffolding grows the lists | **Medium-High** |
| 6 | Target selection (player) | New `BattleState.SelectingTarget` | **High** |
| 7 | Target selection (enemy) | Static helper returning first living ally; defer strategy enum | **High** |
| 8 | Signal payloads | BattleSystem signals carry a `SequenceContext : RefCounted` wrapper holding Attacker, Target, CurrentAttack, SequenceId; TimingPrompt stays target-free | **High** (verified) |

---

## Q1 — What is Combatant?

**Proposed answer:** A plain C# class (not a Godot `Node`, not a Godot `Resource`, not `partial`, not abstract). A `Side` enum field distinguishes player from enemy. Side-specific fields (MP, AttackPool, LearnableAttack) live on the same class; they're simply unused on the opposite side.

```csharp
public enum CombatantSide { Player, Enemy }

public class Combatant
{
    public string       Name;
    public CombatantSide Side;

    // Combat-universal state
    public int              CurrentHp;
    public int              MaxHp;
    public bool             IsDead;
    public Vector2          Origin;              // world-space origin for positioning
    public ColorRect        PositionRect;        // existing anchor node
    public AnimatedSprite2D AnimSprite;          // existing animated sprite node

    // Player-only — null/unused on enemies
    public int              CurrentMp;
    public int              MaxMp;
    public bool             IsDefending;

    // Enemy-only — null/unused on players
    public EnemyData        Data;                // reference to the EnemyData resource
    // Tracks absorption of this enemy's single Data.LearnableAttack — consistent with the
    // "one learnable per enemy" assumption in EnemyData.LearnableAttack. If future design
    // calls for multiple learnable moves per enemy, this field changes to
    // HashSet<AttackData> AbsorbedMoves or similar.
    public bool             HasBeenAbsorbed;
    public ShaderMaterial   FlashMaterial;       // the white-flash material for signalling
}
```

**Reasoning:**
- **Class (not struct):** Combatants are shared-reference entities — signals carry references, sprites associate with their owning combatant, damage handlers mutate state through the reference. Struct semantics (value copy) would break this.
- **Not a `Node`:** Combatants aren't visual or scene-tree-resident. Keeping them out of the scene tree means they aren't subject to node lifecycle concerns (no need for `IsInstanceValid` checks on combatant access, no scene-tree reparenting hazards). The sprites they reference are in the scene tree; that's where visual ownership belongs.
- **Not abstract with subclasses:** an abstract `Combatant` with `PlayerCombatant` / `EnemyCombatant` subclasses looks cleaner in isolation but introduces dispatch friction at every callsite that needs side-specific info. Code would fill with `if (c is PlayerCombatant pc)` pattern-matching. With a flat class + `Side` enum, `combatant.Side == CombatantSide.Player` is a trivial comparison, and side-specific fields are just unused on the wrong side. The class is slightly "wider" than a subclassed design but far more ergonomic at call sites.
- **Not an interface:** interfaces shine with multiple distinct implementations. We have two fixed sides that share ~80% of state; an interface adds ceremony without delivering flexibility we'll use.

**Trade-offs and alternative defensibility:**
- **Abstract base + subclasses** is defensible if the project ever grows a third faction or if side-specific fields explode in count. Today neither is the case, so the flat class wins on pragmatism.
- **Mixing player and enemy fields on one class** feels untidy — a player combatant has a null `Data` reference, and an enemy combatant has an ignored `CurrentMp`. This is a deliberate trade for callsite simplicity. If the untidy feeling grows, splitting later is mechanical.

**Implications:** Q2, Q3, Q8 downstream all assume this shape.

**Addendum — signal payload vehicle:** `Combatant` remains a plain C# class. However, Godot 4's C# `[Signal]` source generator rejects non-Variant-compatible types in delegate signatures (error `GD0202`), so `Combatant` references cannot be passed through signals directly. The marshalling vehicle is `SequenceContext : RefCounted` (see Q8) — a wrapper that holds Combatant references as fields.

Surprising-but-good finding from the Phase 3.0 verification: **plain-C#-class references nested inside a RefCounted payload preserve identity through Variant marshalling.** `ctx.Attacker` on the subscriber side is the exact same `Combatant` instance that was set on the sender side — not a copy, not null. This is what makes the wrapper approach tenable without forcing `Combatant` itself to inherit from any Godot type. The Godot-type coupling is confined to `SequenceContext`; `Combatant` keeps its lifecycle independence, serialization friendliness, and scene-tree-nonresidence intact.

---

## Q2 — Sprite node ownership

**Proposed answer:** Combatant holds references to its sprite nodes (`PositionRect`, `AnimSprite`). The scene tree owns the nodes' *lifecycle* — Combatant just points at them. When a battle starts, BattleTest builds Combatants that reference the pre-existing sprite nodes. When the scene reloads, both are rebuilt together.

**Reasoning:**
- **Direct references are ergonomic** for the use cases the codebase has: damage handlers need to trigger the right sprite's hurt flash; positioning math needs the right sprite's rect; animation ownership naturally flows from "the combatant whose turn it is."
- **Scene tree owns lifecycle** keeps combatants free of node-freeing concerns. A combatant can be freely GC'd at battle end without cleaning up anything — the scene reload handles sprites.
- **Alternative — dictionary lookup (`combatantToSprite[combatant]`)** adds an indirection and a lookup on every sprite access. Worth considering if combatants ever needed to exist without sprites (headless simulation, AI planning passes), but nothing in the roadmap needs that.

**Trade-offs:**
- Combatant holds references to Godot objects, so it's bound to a scene lifetime. Treating combatants as freely persistable across scenes would require breaking that coupling — out of scope.
- If we ever want to run combat simulation ahead of rendering (e.g. AI lookahead), the current design forces that simulation to share the same sprite references — awkward. Defer this concern; nothing on the roadmap needs it.

**Implications for Phase 3:** Every finding in B (sprite references) and much of C (positioning) resolves to `combatant.AnimSprite.*` / `combatant.PositionRect.*` reads. The `GetOrigin(ColorRect)` binary lookup becomes `combatant.Origin` field access.

---

## Q3 — Architectural home

**Proposed answer:** `BattleTest` owns two fields:

```csharp
private List<Combatant> _playerParty;
private List<Combatant> _enemyParty;
```

These are constructed in `_Ready` from existing config (`EnemyData` resource for enemies; hardcoded/scaffolded for the player for now). Subsystems (`BattleSystem`, `BattleMenu`, target selection) read from these lists via BattleTest as the orchestrator.

**Reasoning:**
- BattleTest is already the combat orchestrator. Adding party lists as fields adds no new class and requires no new wiring.
- **Alternative — new `CombatantManager` / `BattleState` class:** justified if BattleTest were shrinking or if multiple scenes needed to share combatant state. Today BattleTest is too large (2800 lines), but the right fix is the Phase 1 code review splitting responsibilities — not introducing a manager here as a premature seam.
- **Alternative — distributed scene-tree lookup:** each combatant's scene-tree node exposes a `Combatant` reference, and a scanner collects them. Clean for Godot-native designs but introduces incidental complexity (scanner runs on party change, ordering not guaranteed). Worth revisiting if the scaffolding exercise proves this would be easier; not the right starting point.

**Trade-offs:**
- BattleTest grows slightly. Offsetting this: multiple singleton fields (`_playerHP`, `_enemyHP`, `_enemyMaxHP`, `_playerMp`, `_playerDefending`, `_enemyFlashMaterial`, `_enemyAnimSprite`, `_playerAnimSprite`, `_playerSprite`, `_enemySprite`, `_playerOrigin`, `_enemyOrigin`, `_enemyData` reference) collapse into two list fields. The file gets smaller, not larger.
- **This design intentionally does not split BattleTest further as part of this refactor.** If Phase 3 surfaces pressure to split BattleTest (the file is already ~2800 lines), that pressure is noted for the Phase 1 code review, not acted on mid-refactor. Keeping scope disciplined is more important than resolving a known-but-orthogonal file-size concern inside this work.

---

## Q4 — Party representation

**Proposed answer:** Dynamic `List<Combatant>`. No fixed-size slots. No nulls. Iteration uses `foreach`; positional queries use index `[i]`.

```csharp
private List<Combatant> _playerParty = new();   // 1 entry for prototype; up to 4 post-scaffolding
private List<Combatant> _enemyParty  = new();   // 1 entry for prototype; up to 5 post-scaffolding
```

**Reasoning:**
- **Dynamic list is simpler** — no null checks during iteration, no "slot empty" semantics to handle. For prototype sizes (1, growing to 4 and 5), the distinction rarely matters.
- **UI "slot N of M"** semantics are derivable from list index. HP panel in slot 3 just means `_playerParty[2]`.
- **Alternative — fixed-size arrays with null slots** would matter if:
  - A specific slot index has persistent meaning (e.g. "slot 1 is always the Absorber"). For multi-character work, slot meaning is derivable from combatant role, not list index.
  - UI needs stable layout regardless of alive/dead state. A dead combatant stays in the list; the UI renders a "defeated" visual in that slot. Same behavior either way.

**Trade-offs:**
- If a specific use case surfaces where "slot 3 of 4 always exists" is semantically important (e.g. revive mechanics that refill a specific slot), fixed-size arrays become attractive. For the 1v1 → 4v5 roadmap, dynamic lists are strictly cleaner.

**Confidence: High.** Character roles live on the characters themselves (via the `Combatant` fields), not on list indices. Dynamic lists fit the data model decisively — the "fixed-size slot" advantage only materializes if list-index carries semantic meaning beyond "position in the party," which nothing on the roadmap requires. Revising to fixed-size later is still mechanical if that changes.

**Implications:** iteration in damage application, UI rendering, and game-over checking all uses `foreach` or LINQ `.Any()`. No index-based contracts.

---

## Q5 — Incremental refactor vs. full abstraction up-front

**Proposed answer:** **Combatant-first, single-entry parties for prototype.** Introduce the `Combatant` class and the party lists immediately. Construct one entry per side for the existing 1v1 fight. Refactor all HP/sprite/positioning findings to route through Combatant references in the Phase 3 work. Behavior stays identical because both lists have exactly one entry; all operations that used to read/write singleton fields now read/write the single list entry's fields.

The scaffolding exercise (Phase 6) validates the abstraction by growing list sizes to 4 and 5 with throwaway sprites.

**Reasoning:**
- **Option B (pure staged — parameterize first, abstract later):** would leave an intermediate state where `ColorRect attacker, ColorRect defender` parameters thread through methods that don't yet know about Combatant. That's adopting a transitional pattern the next pass has to replace. Wasted work.
- **Option A (full abstraction up-front including target selection):** too big — target selection is its own phase. Mixing the abstraction with new gameplay is how regressions get buried.
- **Option C (proposed — Combatant-first, single-entry parties):** gets the right pattern from day one. Phase 3 refactor is mechanical find-and-replace: every `_playerHP` becomes `_playerParty[0].CurrentHp`, every `_enemyAnimSprite` becomes `_enemyParty[0].AnimSprite`. The target-selection work in Phase 4 then extends the same pattern (picks which entry to target) without reshaping the underlying data.

**Trade-offs:**
- The refactor is still sizable — dozens of callsites touched. But each change is mechanical, and behavior stays identical because list size is 1.
- **Alternative defense:** pure-staged (Option B) has the virtue of being chunkable into smaller commits. Option C is also chunkable — per audit category — so this isn't a real advantage.
- **Biggest risk:** the abstraction gets baked in before the scaffolding exercise surfaces what multi-character combat actually needs. The commitment to `CombatantSide` enum and flat-field design should survive scaffolding; if they don't, the revision is still mechanical. Worth accepting that risk in exchange for not doing the refactor twice.

**Discipline during refactor:** treat list-index-zero access (`_playerParty[0]`, `_enemyParty[0]`) as a code smell. Prefer semantically-named accessors or context-specific references:

- `attacker` or `target` for the combatant relevant to the current action
- `GetPlayerCharacter(role)` or similar if role-based lookup matters
- Explicit index access is acceptable only when the code genuinely means "the first combatant in the list" as a structural fact — e.g., rendering the first slot's HP panel in a fixed-position UI.

Even when the current 1v1 setup makes `_playerParty[0]` and `attacker` reference the same object, the latter form doesn't bake in size-1 assumptions. The refactor's job is to eliminate singleton thinking; reintroducing it via `[0]` everywhere defeats the work. Reviewer callout — during Phase 3 diffs, `[0]` should be the exception, not the pattern.

**Confidence:** Medium-High. The alternative (pure staging) is defensible; neither is clearly wrong. Betting on Combatant-first because it avoids transitional patterns.

---

## Q6 — Where does target selection live?

**Proposed answer:** New `BattleState.SelectingTarget` value in the existing enum. When the player picks an action in `BattleMenu` that requires a target, BattleMenu transitions to this state and hands off to a new `TargetSelectionController` (or equivalent — could be inline helpers on BattleTest; see trade-off below). Input routes through `BattleTest._Input` to a new `HandleTargetSelectInput` handler.

```csharp
private enum BattleState {
    EnemyAttack, PlayerMenu, SelectingTarget,    // ← new
    PlayerAttack, GameOver, Victory
}
```

**Reasoning:**
- **Consistent with existing state machine.** The codebase already uses `BattleState` to partition input routing (`HandleMenuInput`, `HandleGameOverInput`, `HandleVictoryInput`). Target selection is a phase between menu and attack execution — natural to represent as a state.
- **Input routing uses the existing pattern:** `_Input` dispatches on `_state`. Adding a SelectingTarget branch is mechanical.
- **State flow:** `PlayerMenu` → (action selected with target requirement) → `SelectingTarget` → (target committed) → `PlayerAttack`. Cancel from SelectingTarget returns to PlayerMenu.

**Trade-offs:**
- Should target-selection code live as inline helpers on BattleTest, or as a separate class?
  - Inline helpers: matches the existing game-over and victory patterns (those live as methods on BattleTest). Simpler but grows BattleTest further.
  - Separate class: cleaner separation, but BattleTest already has helper methods for every other battle phase.
  - **Defer this to Phase 4 implementation.** Start inline; extract to a class if the code gets unwieldy.
- Actions that don't require target (Beckon, Defend) skip the state entirely — Q6's question doesn't specify, but the obvious answer is a per-action "requires target" flag or a per-action valid-target-pool resolver.

**Implications:** Phase 4 creates this state and its input handler. Phase 3 refactor leaves the state enum alone (doesn't pre-add `SelectingTarget` — that's Phase 4's job).

---

## Q7 — Enemy-side target selection

**Proposed answer:** New `TargetSelector` static helper parallel to `AttackSelector`. Prototype: returns the first living ally. No strategy enum yet.

```csharp
public static class TargetSelector
{
    /// For multi-ally combat (post-scaffolding), this expands into strategies.
    /// Prototype: always returns the first living ally, or null if all are dead.
    public static Combatant SelectTarget(Combatant attacker, List<Combatant> candidates)
    {
        foreach (var c in candidates)
            if (!c.IsDead) return c;
        return null;
    }
}
```

**Reasoning:**
- **Parallels `AttackSelector`** — same pattern, same file location convention, same caller integration.
- **No strategy enum yet** because:
  - Prototype is 1v1. Strategy is meaningless when there's only one possible target.
  - Strategies need tuning informed by gameplay testing at density. Scaffolding exercise is the right time to prototype them.
  - Adding the enum now creates API commitment before it's validated.
- **Enemy-side target selection is deferred in practice** — for the prototype, every enemy attack targets `_playerParty[0]`. The `TargetSelector` exists mainly as the integration seam so the call site (in `BeginEnemyAttack`) is written correctly from day one.

**Trade-offs:**
- Could equally be a method on `EnemyData` (`enemy.SelectTarget(candidates)`) or on the `Combatant` itself (`attacker.PickTarget(candidates)`). Keeping it as a static helper is consistent with `AttackSelector` and avoids adding methods to data types.
- **The static-class form commits us to adding future strategies as method parameters** (`SelectTarget(attacker, candidates, strategy)`) rather than as a redesigned API. If strategies turn out to need significant per-enemy state (e.g., targeting memory, threat tables, grudge tracking), this choice would need revisiting — but for the prototype's trajectory, parameter-based extension is fine.
- If the scaffolding exercise proves strategies are needed right away, adding `TargetSelectionStrategy` to `EnemyData` is one line. Low-cost deferral.

**Confidence:** High on the shape, Medium on deferring strategy enum. Defensible to add it now if strict parallelism with `AttackSelector` matters more than deferral discipline. Proposing defer because the strategy field would have exactly one value (`FirstLivingAlly`) for the entire prototype lifetime.

**Implications for Phase 3:** Every existing `BeginEnemyAttack` callsite that hardcoded `_defender = _playerSprite` becomes `var target = TargetSelector.SelectTarget(attacker, _playerParty)`. Behavior identical at 1v1.

---

## Q8 — Signal payload shape

**Proposed answer:** BattleSystem signals carry a single `SequenceContext : RefCounted` parameter — a wrapper holding references to the sequence's Attacker, Target, CurrentAttack, and a monotonic SequenceId. `TimingPrompt` signals stay target-free.

```csharp
/// <summary>
/// Signal payload wrapper carrying per-sequence context through BattleSystem signals.
/// RefCounted so it marshals through Variant (plain C# class references cannot — see
/// the "Why Option A was rejected" note below).
///
/// Lifetime: one instance per sequence. BattleSystem creates it when StartSequence is
/// called, holds it for the sequence's duration, emits it unchanged with every signal.
/// Subscribers can use reference equality to identify which sequence a signal belongs to.
/// </summary>
public partial class SequenceContext : RefCounted
{
    public Combatant  Attacker      { get; init; }
    public Combatant  Target        { get; init; }
    public AttackData CurrentAttack { get; init; }
    public int        SequenceId    { get; init; }
}

[Signal] public delegate void StepPassEvaluatedEventHandler(
    int result, int passIndex, int stepIndex, SequenceContext ctx);

[Signal] public delegate void StepStartedEventHandler(
    int stepIndex, SequenceContext ctx);

[Signal] public delegate void SequenceCompletedEventHandler(
    SequenceContext ctx);
```

### Field-by-field purpose

| Field | Purpose |
|---|---|
| `Combatant Attacker` | The unit executing the sequence. Subscribers read `ctx.Attacker.AnimSprite`, `ctx.Attacker.Side`, etc. to route animation, sound, and side-dependent logic without singleton state lookups. |
| `Combatant Target` | The unit being targeted. Subscribers read `ctx.Target.CurrentHp -= damage`, derive effect positions from `ctx.Target.PositionRect`, etc. |
| `AttackData CurrentAttack` | The attack being executed. Subscribers read `ctx.CurrentAttack.Category` to branch on Physical vs. Magic (miss-cancel rule, damage calc), and `ctx.CurrentAttack.IsHopIn` to branch on hop-in vs. cast paths. Eliminates the current indirection through `_battleSystem.GetCurrentAttack()`. |
| `int SequenceId` | Monotonic counter assigned by BattleSystem at `StartSequence` entry. Three uses: (1) logging traceability (`seq#5 step 2/3 circle resolved` reads better than context-free logs); (2) stale-signal detection if the existing `_sequenceActive` guard ever fails; (3) future-proofing for concurrent-sequence work without re-designing the payload. |

### Subscriber pattern

Subscribers dereference through the wrapper:

```csharp
private void OnStepPassEvaluated(int result, int passIndex, int stepIndex, SequenceContext ctx)
{
    if (ctx.Attacker.Side == CombatantSide.Player)
    {
        // player-attacker path — apply damage to enemy target
        ctx.Target.CurrentHp = Mathf.Max(0, ctx.Target.CurrentHp - damage);
        PlayEnemyHurtFlash(ctx.Target);
    }
    else
    {
        // enemy-attacker path — apply damage to player target
        ctx.Target.CurrentHp = Mathf.Max(0, ctx.Target.CurrentHp - damage);
        PlayPlayer("hit", ctx.Target);
    }
}
```

The one-extra-dereference cost (`ctx.Attacker.CurrentHp` vs. `attacker.CurrentHp`) is negligible and arguably improves readability — the `ctx.` prefix makes it obvious the field is coming from the sequence's payload, not a local or singleton.

### Lifetime and identity

**One SequenceContext per sequence.** BattleSystem constructs a `SequenceContext` at `StartSequence` entry, holds it as a field for the sequence's duration, and emits that same instance unchanged through every signal during the sequence. Attacker, Target, CurrentAttack, and SequenceId don't change mid-sequence, so the `init`-only properties are safe.

**Reference equality identifies the sequence.** If a subscriber ever needs to verify "am I still handling the sequence I started tracking?", `ReferenceEquals(trackedCtx, ctx)` answers directly. Not needed at 1v1, but valuable if concurrent sequences are ever introduced.

### Reasoning

- **Consistent payload shape across all BattleSystem signals.** Every signal has a single `SequenceContext ctx` parameter plus any signal-specific primitives (result, passIndex, stepIndex). Subscribers never wonder "does this signal have attacker? does it have target?" — everything about the sequence is on `ctx`.
- **Plain-C#-class references preserve identity inside the wrapper.** Verified in Phase 3.0 test: `ctx.Attacker`, `ctx.Target`, `ctx.CurrentAttack` all round-trip with identity preserved, even though `Combatant` and the nested classes aren't GodotObject-derived.
- **TimingPrompt stays target-free** — UI widget, no business knowing combat semantics. The attacker/target binding happens one level up, in BattleSystem, where the subscription is made.

### Trade-offs

- One extra indirection on every payload access (`ctx.Attacker` vs. direct `attacker` parameter). Trivial and arguably improves readability.
- One extra allocation per sequence (the `SequenceContext` instance). At the rate sequences start (one per turn), this is noise.
- `RefCounted` base coupling is confined to `SequenceContext` itself. `Combatant`, `AttackData`, and other data classes are unaffected.

**Implications for Phase 3:** BattleSystem constructs `SequenceContext` at `StartSequence` entry; every existing signal emission updates to include it. Subscribers in BattleTest update to take `SequenceContext ctx` and dereference accordingly. This ties Phase 3's J-category work to the broader A-category refactor.

---

## Why Option A was rejected

During Phase 3.0 marshalling verification, the original Q8 design — "BattleSystem signals carry `Combatant attacker, Combatant target` references directly" — failed at compile time. Godot's C# source generator emits `GD0202` for any `[Signal]` delegate parameter whose type isn't Variant-compatible, and plain C# classes (not derived from `GodotObject`) aren't Variant-compatible.

Three revision paths were considered. **Option A — make `Combatant` inherit from `RefCounted`** (or `GodotObject`) — would have preserved the "pass Combatant references directly in signal payloads" ergonomic. It was rejected because:

Binding `Combatant` to Godot's type hierarchy for a concern that only exists at signal boundaries is too invasive. `Combatant`'s design calls for it to be a plain C# class specifically so it stays independent of Godot's object lifecycle (no `GodotObject`-style ref counting concerns in non-signal code paths), serialization-friendly if save systems ever need it, and scene-tree-nonresident. Making the whole class derive from `RefCounted` imports Godot's lifetime semantics everywhere `Combatant` is touched — damage routing, positioning, state mutation — to solve a problem that only manifests at the signal-emit boundary.

**Option C (adopted) — confine the Godot-type coupling to a payload wrapper (`SequenceContext : RefCounted`) that holds plain `Combatant` references as fields** — isolates the Godot-hierarchy coupling to exactly the place it's needed (crossing the signal bus) without leaking it into the rest of the combat code. The Phase 3.0 verification confirmed this works: plain-C#-class references nested inside a RefCounted payload preserve identity through Variant marshalling.

Option B (pass indices through signals; subscribers resolve to Combatant via party lists) was also considered and rejected — it reintroduces a global-state dependency on the party lists at every subscriber, undermining the design's self-sufficiency goal.

---

## Implications for Phase 3

The answers above reshape the Phase 3 refactor plan as follows:

1. **Order changes:** Construct `Combatant` + party lists **first**, before any of the A/B/C/D/E/F findings. Subsequent categories' refactors are then mechanical. The Q8 marshalling verification ran and passed during Phase 3.0 — the `SequenceContext : RefCounted` wrapper approach is confirmed implementable.

2. **A category (HP/MP state):** every field becomes per-entry-on-Combatant. Lists have 1 entry for prototype; behavior identical.

3. **B category (sprite refs):** `_playerAnimSprite` becomes `_playerParty[0].AnimSprite` or, in damage-routing contexts, `target.AnimSprite`. Combat-semantics vs. animation-lifecycle distinction stays important — local sprite callbacks can continue referencing their own sprite without routing through Combatant.

4. **C category (positioning):** pair-based helpers (`ComputeCameraMidpoint`, `ComputeSlamPosition`) take explicit `Combatant attacker, Combatant target` parameters instead of reading singleton fields. `GetOrigin(ColorRect sprite)` becomes `combatant.Origin`.

5. **D category (BattleSystem signatures):** `StartSequence` takes `Combatant attacker, Combatant target` (internally constructs the `SequenceContext` for the sequence's duration), replacing `Vector2 defenderCenter, bool isPlayerAttack`. `_isPlayerAttack` becomes `_attacker.Side == CombatantSide.Player` (or read from the `SequenceContext` inside the sequence).

6. **E category (damage):** `_enemyHP = ... - damage` becomes `target.CurrentHp -= damage` (direct mutation; `TakeDamage` method can be extracted later if repetition motivates).

7. **F category (effects):** `SpawnCounterSlashEffect`, `SpawnEtherEffect`, `PlayEnemyHurtFlash`, `FlashEnemyWhite`, `ShakeEnemySprite` all take a `Combatant target` parameter.

8. **I category (caches):** `_playerMagicDefenderCenter` / `_playerMagicPromptPos` become `_playerMagicTarget` (Combatant reference) — center derived on-demand.

9. **J category (signals):** every BattleSystem signal adds a `SequenceContext ctx` parameter; subscribers update to dereference through `ctx.Attacker` / `ctx.Target` / `ctx.CurrentAttack`. BattleSystem constructs the `SequenceContext` at `StartSequence` entry and holds it through the sequence. `TimingPrompt` signals unchanged. Tied to D-category work — signature change and subscriber updates land in the same pass.

10. **Not in Phase 3 scope:** D5 (AttackStep schema), G1 (target-selection feature — Phase 4), H1 (scene structure — Part 2), K (audio — Part 2), Q6 `SelectingTarget` state (Phase 4), Q7 `TargetSelector` integration (Phase 3 adds the helper; Phase 4 wires up player-side target-selection UI).

---

## Deferred decisions

Questions whose answers are genuinely premature right now; resolve during the scaffolding exercise (Phase 6) or later:

- **Whether `Combatant` needs splitting into `PlayerCombatant` / `EnemyCombatant` subclasses.** Current answer: no. Revisit if side-specific fields proliferate past ~3 each, or if third-faction combat enters the roadmap.
- **Whether target-selection code lives inline on BattleTest or as a separate class.** Start inline; extract if BattleTest grows past comfort.
- **`TargetSelectionStrategy` enum and per-enemy strategies.** Deferred until at least one enemy needs varied target behavior.
- **Enemy HP panel layout at density.** Scaffolding exercise decides.
- **Combatant field initialization (constructor? factory method? builder?).** Write the ad-hoc construction during Phase 3; if a pattern emerges, extract then.
- **Whether `Combatant` gains methods like `TakeDamage(int)` or stays data-only.** Start data-only to minimize scope. Methods can be added during a later cleanup if code repetition motivates them.
- **D5 AttackStep schema resolution.** Audit flagged as a design decision; scaffolding exercise validates which option works at density.

---

## Refactor discipline

Every commit during Phase 3 must leave the game in a playable state — the 1v1 boss fight must play identically to its pre-refactor behavior. Intermediate commits that break the game are not acceptable; if a refactor chunk can't be committed without breaking the game, the chunk is too big and needs splitting.

This constrains how the refactor proceeds:

- **Atomic cross-cutting changes land in one commit.** If changing a signal signature requires updating N call sites, all N updates are in the same commit as the signature change — not a "signature today, callers tomorrow" split.
- **No long-lived mid-refactor branches** with "broken but will be fixed later" states. Every commit compiles, runs, and plays the fight identically.
- **Regression-test before every commit.** Play the fight end-to-end. Surface any behavioral drift immediately. "Looks fine" doesn't count; the fight actually plays through.

**Safety net — if a chunk can't be cleanly committed:**
1. Stop. Do not push through a known-broken intermediate.
2. Discard the uncommitted work with `git restore` (or `git checkout -- <paths>`). Note: this is an uncommitted-work operation, not `git revert` (which creates a new commit undoing an already-committed change — wrong tool here).
3. Re-plan the chunk smaller. Identify the true atomic unit.
4. Resume with the smaller chunk.

This discipline is more important than velocity. Shipping a broken intermediate "to unblock ourselves" is the anti-pattern that turns mechanical refactors into multi-session debugging sessions.
