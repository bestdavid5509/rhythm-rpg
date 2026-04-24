# Rhythm RPG

## Concept

A timing-based RPG battle system where circular prompts close toward a target zone and the player presses a button when each circle reaches the zone.

### Defensive (enemy attacking player)
- Each missed input deals damage to the player
- Hitting all inputs is a **perfect parry**, triggering a counter attack for bonus damage

### Absorbing an enemy move
- A perfect parry by the Absorber on any learnable move absorbs it — no kill required, no specific HP threshold
- Learnable moves are visually signaled on the enemy (highlight or color) and by the move name appearing in a distinct color

### Offensive (player using an absorbed move)
- Same timing prompts as when the enemy used the move
- The move continues as long as the player hits inputs correctly
- The move ends on the first missed input
- Total damage scales with how many inputs were hit successfully

The Absorber accumulates a library of absorbed moves over time.

## Workflow conventions

For the human-AI collaboration patterns used on this project — plan mode usage, pre-commit diff review, commit message conventions, Claude Code coordination — see `docs/workflow.md`. This file focuses on codebase architecture and conventions.

## Tech Stack

- **Engine:** Godot 4
- **Language:** C#
- **Runtime:** .NET 9

## Project Structure

### Scripts/Battle/
| File | Responsibility |
|---|---|
| `BattleTest.cs` | Core turn loop, state machine, lifecycle (`_Ready`/`_Input`), all field declarations, tween helpers, position helpers, shared combat helpers |
| `BattleAnimator.cs` | Sprite frame construction, all `AnimationFinished` callbacks, unified target-aware sprite helpers (`PlayAnim`/`StopAnim`/`SafeDisconnectAnim`/etc.), end-of-battle overlay |
| `BattleMenu.cs` | Battle menu construction, navigation, input handling — main menu and Absorbed Moves submenu |
| `BattleDialogue.cs` | Narrative dialogue component — multi-line speaker-tagged character speech with character-by-character reveal; constructed per use and `QueueFree`'d after `DialogueCompleted`. Standalone class, not a `BattleTest` partial. |
| `BattleSystem.cs` | Attack sequence runner — drives `AttackData` steps, spawns `TimingPrompt` circles and timed effect `AnimatedSprite2D` nodes, emits `StepPassEvaluated` / `SequenceCompleted` (both carry `SequenceContext` payload) |
| `Combatant.cs` | Plain C# class representing a single battle participant. Fields for HP/MP/side/sprite refs; `TakeDamage(int)` / `Heal(int)` methods. `CombatantSide` enum (Player / Enemy). |
| `SequenceContext.cs` | `RefCounted`-derived wrapper carrying per-sequence identity (Attacker, Target, CurrentAttack, SequenceId) through BattleSystem signals. Combatant isn't Variant-compatible; this is the marshalling vehicle. |
| `TimingPrompt.cs` | Single closing circle — Standard, Slow, and Bouncing variants; draws only the moving ring and hit/miss flash; does **not** draw the target ring (see `TargetZone`); emits `PassEvaluated` and `PromptCompleted`; static `ConfirmAll()` resolves all active circles on one input event |
| `TargetZone.cs` | Persistent shared target ring node in `BattleTest.tscn`; draws the stationary white ring and green hit-window band; shown/hidden by `BattleTest` at sequence start/end; has no knowledge of individual circles |
| `AttackData.cs` | `[GlobalClass]` Resource — ordered list of `AttackStep` objects, `DisplayName`, `BaseDamage`, `MpCost`, `Category`, `IsHopIn`; saved as `.tres` files |
| `AttackStep.cs` | `[GlobalClass]` Resource — per-step data: `SpritesheetPath`, `FrameWidth`/`FrameHeight`, `Fps`, `ImpactFrames[]`, `CircleType`, `BounceCount`, `StartOffsetMs`, `FlipH`, `Scale`, `Offset`, `PlayerOffset`, `SoundEffects[]`, `SoundTriggerFrames[]` |
| `EnemyData.cs` | `[GlobalClass]` Resource — enemy definition: `EnemyName`, `MaxHp`, `SpritesheetPath`, `FrameWidth`/`FrameHeight`, `SpriteOffsetY`, `AnimationConfig`, `AttackPool[]`, `LearnableAttack`, `SelectionStrategy` |
| `EnemyAnimationConfig.cs` | `[GlobalClass]` Resource — data-driven animation layout: row indices, frame counts, `HasCastEnd` flag, optional `HurtSheetPath` for separate hurt spritesheets |

All three `BattleTest` files are `public partial class BattleTest : Node2D` and compile as one class.

### Scenes/Battle/
- `BattleTest.tscn` — main battle prototype scene
- `TimingPrompt.tscn` — circle prompt scene instantiated per step

### Resources/Enemies/
`.tres` files for `EnemyData` and `EnemyAnimationConfig` resources.

| File | Description |
|---|---|
| `warrior_phase1.tres` | Warrior Phase 1 — 150 HP, 130×130, 4 attacks, learnable: ice_sword_swipe |
| `warrior_phase1_anim_config.tres` | Animation layout for Warrior (no cast_end, hurt on main sheet) |
| `8_sword_warrior_phase2.tres` | 8 Sword Warrior Phase 2 — 200 HP, 160×160, 3 attacks, learnable: repeating_comet_barrage |
| `8_sword_warrior_anim_config.tres` | Animation layout for 8 Sword Warrior (has cast_end, separate hurt sheet) |

### Resources/Attacks/
`.tres` files are Godot Resource instances of `AttackData`, editable in the inspector.

| File | Description |
|---|---|
| `red_sword_plunge.tres` | 1 step, 1 circle — `Red_Sword_Plunge_Sheet.png`, `ImpactFrames=[6]` |
| `red_triple_sword_plunge.tres` | 1 step, 3 circles — `Red_Triple_Sword_Plunge_Sheet.png`, `ImpactFrames=[6,7,8]` |
| `red_sword_combo_attack.tres` | 2 steps — triple plunge (3 circles) chained into single plunge (1 circle) |
| `red_hammer_swipe.tres` | 1 step, 1 circle — `Red_Fire_Hammer_Swipe_Sheet.png`, `ImpactFrames=[6]` ← active in `BattleSystem` |
| `blue_sword_plunge.tres` | 1 step, 1 circle — `Blue_Sword_Plunge_Sheet.png`, `ImpactFrames=[6]` |
| `blue_triple_sword_plunge.tres` | 1 step, 3 circles — `Blue_Triple_Sword_Plunge_Sheet.png`, `ImpactFrames=[6,7,8]` |
| `effect_manifest.md` | Per-frame dimensions, frame counts, layout, and impact frame indices for every spritesheet in `Assets/Effects/` |

### Assets/
```
Assets/
  Characters/
	Knight/               — player sprite strips (120×80 px per frame, horizontal)
	  _Idle.png
	  _Run.png
	  _AttackNoMovement.png
	  _AttackComboNoMovement.png   (1200×80, 10 frames — combo sheet)
	  _Attack2NoMovement.png       (720×80, 6 frames — parry source)
	  _Hit.png
	  _DeathNoMovement.png
	  _Crouch.png / _CrouchAttack.png / _CrouchTransition.png / _Roll.png
  Enemies/
	8_Sword_Warrior/
	  8_Sword_Warrior_Red/         — Phase 2 boss (21 cols × 7 rows, 160×160 px)
	  8_Sword_Warrior_Blue/
	  8_Sword_Warrior_Black/
	Warrior/
	  Warrior_Red_Sword_Silver_White_Armor/ — active Phase 1 enemy (130×130 px)
	  (other colour variants available)
	NightBorne/                    — unused in current prototype
  Effects/
	Sword_Effects/                 — plunge, swipe, hammer attacks
	Comet_Effects/                 — projectile / magic attacks
	Support_Effects/               — buff/debuff circles
```

## Design Decisions

### Party System

Party size is four characters for the current scope, with room to expand in the future. Each has a distinct combat role:

| Character | Role | Notes |
|---|---|---|
| **The Absorber** | Main character | Growing library of absorbed enemy moves; taunt ability baits enemies into using their learnable/signature move; versatile and strategic |
| **The Damage Dealer** | Pure offense | High-damage timing attacks; no utility |
| **The Buffer/Debuffer** | Party/enemy manipulation | Perfect parries apply buffs to the party or debuffs to enemies rather than direct damage |
| **The Healer** | Sustain | Innate abilities only — no absorption; timing accuracy determines heal strength |

Only the Absorber can learn moves.

### Absorbed Move System

- A perfect parry by the Absorber on any **learnable move** absorbs it permanently
- No enemy kill or HP condition required — absorption is purely parry-triggered
- Learnable moves are visually distinguished on the enemy (highlight/color signal) and the move name appears in a distinct color during the prompt sequence
- The Absorber's taunt baits enemies into using their learnable/signature move, giving the player control over when absorption opportunities arise

### Enemy Design

- Enemies can have multiple attacks, each with distinct prompt patterns
- Enemies can have heals and debuffs the player **cannot interfere with** — these create urgency and strategic tension
- Signature/learnable moves are a specific, marked subset of an enemy's attack pool

### Input and Damage Are Simultaneous

The button press IS the strike landing. Damage — to the player or enemy — is applied at the moment of each input resolution, not at the end of a sequence. This applies universally to both defending and attacking.

### Enemy Attacks

Enemy attacks always play their **full sequence** regardless of player input outcome. The player cannot cut a sequence short by missing. Each inward pass is an independent strike opportunity.

### Perfect Parry

A perfect parry occurs when the player registers a **Hit or Perfect on every pass** in an enemy attack sequence — no misses. A single miss at any point breaks the parry, including on intermediate passes of a Bouncing prompt.

**Effect:** Triggers an automatic 20 counter-damage after the full sequence completes. No additional input required.

### Player Attack Combos

Player attack combos continue as long as every input is hit (Hit or Perfect). **The combo ends on the first missed input.** Total damage scales with how many inputs were landed before the miss.

### Damage Model

| Event | Effect |
|---|---|
| Enemy attack — Hit or Perfect per pass | 0 damage to player (strike blocked) |
| Enemy attack — Miss per pass | Player takes 10 damage (unblocked strike) |
| Perfect parry (all passes hit) | Enemy takes 20 damage (automatic counter) |
| Player attack — Perfect | Enemy takes 13 damage |
| Player attack — Hit | Enemy takes 10 damage |
| Player attack — Miss | Enemy takes 5 damage, combo ends |

### Move Properties

- Moves have **elemental typing** for matchup advantages
- **Perfect execution** can trigger buffs or debuffs in addition to dealing damage
- Base attacks are timing-based with a **low floor, high ceiling** — functional on a miss, powerful on a perfect clear

### Bouncing Circle Mechanic

- A closing circle can reach the target zone and **bounce back outward**, then close again — requiring the player to time the same prompt multiple times
- Each bounce is an additional input opportunity on the same prompt
- First introduced in the opening boss Phase 2 as an escalation of the core mechanic

## Prototype Target: Opening Boss Fight

The first prototype is a two-phase opening boss fight. The boss is deliberately overpowered — it establishes a world with forces beyond the player, not a fair fight.

### Phase 1
- Difficult and urgent
- Most players are expected to lose; **losing is not a game over** — the narrative continues regardless of outcome

### Phase 2
- Triggered by surviving Phase 1
- Music escalates
- **First appearance of the bouncing circle mechanic**
- Harder-hitting attacks with longer prompt sequences

### Rewards for reaching Phase 2
- Dialogue acknowledgment from the boss
- A dropped item
- The possibility of absorbing a late-game move early (high-skill payoff)

### Design intent
The fight is **winnable but extremely difficult on a first run**. It serves as a skill benchmark and a compelling target for replays. Loss at any phase advances the story normally.

## Architectural Decisions

### TimingPrompt — Input Rules

Input is **only accepted** when `_currentRadius` is within `HitWindowSize` of `TargetRadius`. Input outside this range is completely ignored — no state change, no feedback, no miss registered. For Bouncing prompts, input is additionally restricted to inward passes only.

- After a **successful input** (Hit or Perfect): no lockout — the pass resolves immediately and the sequence continues.
- After an **auto-miss** (ring reaches `t = 1` without input): `InputLockoutDuration` (default 0.3s) is applied to block accidental inputs during the outward bounce.

There is no overshoot. The inward lerp endpoint is `TargetRadius - HitWindowSize` (inner edge of the window); the ring stops there and auto-misses via `OnPassComplete`.

### TimingPrompt — Bounce Timing

Variable bounce speed is removed. The outward pass is controlled by a single `BounceDuration` export (default 0.5s):

- The outward lerp **always** goes from `TargetRadius` to `StartRadius` — the same distance every time, regardless of where the player pressed or whether the pass auto-missed.
- `_t` advances at `dt / BounceDuration` on outward passes, so it always takes exactly `BounceDuration` seconds.
- This makes `Perfect@` timestamps on subsequent passes fully predictable and consistent.

Removed: `OvershootDistance`, `FixedReturnDuration`, `_bounceSpeedMultiplier`, `_bounceStartRadius`, `MinBounceSpeed`, `MaxBounceSpeed`.

### TimingPrompt — Multi-Circle Resolution

All active `TimingPrompt` instances register themselves in a static `List<TimingPrompt> _activePrompts` on `_Ready` and remove themselves on `_ExitTree`.

`TimingPrompt.ConfirmAll()` is a static method that calls `EvaluateInput()` on every registered prompt. BattleSystem calls this once per input event to resolve all in-window circles simultaneously. Prompts outside the window, locked out, or on outward passes are silently skipped by `EvaluateInput`'s existing guards.

### Combatant

The `Combatant` is a plain C# class (not a `GodotObject`) representing a single battle participant. One instance per unit; side-agnostic — same type on both sides, `CombatantSide.Player` / `CombatantSide.Enemy` distinguishes them.

Source of truth for per-unit state. Scene-tree nodes (`ColorRect`, `AnimatedSprite2D`) are owned by the scene; the Combatant holds references.

**Fields** (see `Combatant.cs`):

| Field | Purpose |
|---|---|
| `Name`, `Side` | identity; `Side` is `CombatantSide.Player`/`.Enemy` |
| `CurrentHp`, `MaxHp`, `IsDead` | combat state; mutate via `TakeDamage(int)` / `Heal(int)` |
| `Origin`, `PositionRect`, `AnimSprite` | scene-tree node refs |
| `CurrentMp`, `MaxMp`, `IsDefending`, `IsBeckoning` | player-only; null/default on enemies |
| `Data`, `HasBeenAbsorbed` | enemy-only; `Data` is `EnemyData` |
| `FlashMaterial`, `FlashTween` | per-unit flash shader state |

**Party lists** on `BattleTest`:
```csharp
private List<Combatant> _playerParty = new();
private List<Combatant> _enemyParty  = new();
```
Built once by `BuildInitialParties()` in `_Ready`. Single-entry at the 1v1 prototype surface; the scaffolding exercise grows them to 4/5. `_playerParty[0]` / `_enemyParty[0]` is the accepted access pattern today.

**Party-level state** that's not per-unit:
- `_absorbedMoves: HashSet<AttackData>` — per-move-type absorb tracking. Prototype simplification; long-term home is Absorber-character persistent skill data.

**`TakeDamage(int)` / `Heal(int)`** clamp HP to `[0, MaxHp]`. Attacker-agnostic — the receiver operates on itself without knowing the attacker. Same-side targeting (friendly-fire, ally-heal, self-damage, self-heal) is supported by construction; see `docs/design-notes.md` for the forward-looking design.

**Signal marshalling:** Combatant is not Variant-compatible, so it cannot be a direct `[Signal]` parameter. It marshals through `SequenceContext` (RefCounted wrapper) — see the next section. Full rationale in `docs/combatant-abstraction-design.md`.

### SequenceContext

`SequenceContext` is a `RefCounted`-derived wrapper carrying per-sequence identity through `BattleSystem` signals. One instance per attack sequence.

**Fields** (see `SequenceContext.cs`):
```csharp
public Combatant  Attacker      { get; init; }
public Combatant  Target        { get; init; }
public AttackData CurrentAttack { get; init; }
public int        SequenceId    { get; init; }
```

**Why a RefCounted wrapper:** Godot's `[Signal]` parameters must be Variant-compatible. Plain C# classes trigger source-generator error GD0202. A RefCounted wrapper marshals through Variant; its plain-C#-class fields preserve reference identity on the subscriber side.

**Construction:** callers build the context before invoking `BattleSystem.StartSequence(parent, ctx, promptPosition)`. Monotonic IDs via `_battleSystem.NextSequenceId()`:
```csharp
var ctx = new SequenceContext {
    Attacker      = enemyAttacker,
    Target        = playerDefender,
    CurrentAttack = selectedAttack,
    SequenceId    = _battleSystem.NextSequenceId(),
};
_battleSystem.StartSequence(this, ctx, promptPos);
```

**Signal payload on BattleSystem:**
- `StepStarted(int stepIndex, SequenceContext ctx)`
- `StepPassEvaluated(int result, int passIndex, int stepIndex, SequenceContext ctx)`
- `SequenceCompleted(SequenceContext ctx)`

Every signal for a given sequence carries the same `ctx` reference. Subscribers use reference equality or `SequenceId` to identify the sequence.

**Context-preserved invariant:** BattleSystem does NOT clear `_sequenceContext` when `SequenceCompleted` fires. Animation callbacks and teardown queries (e.g. `GetLastStepPostAnimDelayMs()`) run AFTER SequenceCompleted and still need to inspect the most recent attack. The context is overwritten by the next `StartSequence` — matching the old "last attack stays resident" semantics of the retired `_currentAttack` field.

**SpawnEffectSprite geometry** derives from `ctx`:
- Target center: `ctx.Target.Origin + ctx.Target.PositionRect.Size / 2f`
- FlipH: `attackerOnRight = ctx.Attacker.Origin.X > ctx.Target.Origin.X` — invert authored `step.FlipH` for left-side attackers so the same `.tres` file works in both directions.
- Active offset: `attackerOnRight ? step.Offset : step.PlayerOffset`.

### Refactor Conventions

Patterns established during Phase 3. New combat code should follow these.

**[0]-access discipline.** At the current single-unit surface, `_playerParty[0]` / `_enemyParty[0]` are the accepted party accessors. Prefer semantic locals (`var player = _playerParty[0];`) where a single local serves multiple calls. Direct `_playerParty[0]` / `_enemyParty[0]` is accepted at one-off helper calls (e.g. `PlayAnim(_playerParty[0], "idle")`) where a local adds no clarity. Party iteration replaces this entirely when multi-unit density lands.

**Combatant-prefix naming for attacker-agnostic operations.** Methods that operate on a receiver without knowing the attacker use the `Combatant` prefix: `Combatant.TakeDamage`, `Combatant.Heal`, `FlashCombatantWhite`, `ShakeCombatantSprite`, `PlayCombatantHurtFlash`. The naming signals "target-parameterised and side-agnostic" and parallels the unified guard helpers (`PlayAnim(Combatant, string)` etc.).

**Dispatch-vs-receiver predicate.** Two semantically distinct questions that sometimes coincide:
- **Dispatch** (before target is selected): "which target should this attack pick?" — attack-identity, e.g. `_activeMagicAttack == _playerCureAttack` in `BeginPlayerMagicAttack` to decide self-vs-enemy.
- **Receiver** (after attacker/target pair is chosen): "is this a same-side scenario?" — side-equality, e.g. `_sequenceAttacker.Side == _sequenceDefender.Side` in `OnPlayerMagicPassEvaluated` to fork heal-vs-damage.

Today they coincide (Cure is the only self-targeter). They generalise differently: dispatch stays attack-specific; receiver generalises to friendly-fire / ally-heal without further refactor. See `docs/design-notes.md` on friendly-fire.

**Sequence-scoped fields on BattleTest** (Option Y from the Phase 3.4 migration): `_sequenceAttacker`, `_sequenceDefender`, `_sequenceAttackerClosePos`. Set at sequence start; read by positioning helpers and animation-callback continuations that can't receive `ctx` as a parameter (AnimationFinished callbacks use reference-equality `SafeDisconnect` patterns that closures would break). These mirror data on `_sequenceContext` inside BattleSystem but are local to BattleTest.

### Shared Target Zone

A single persistent `TargetZone` node lives in `BattleTest.tscn`. It draws the stationary white ring and green hit-window band that all closing circles aim at.

**Why shared:** with multiple `TimingPrompt` circles active simultaneously — staggered multi-hit steps, circles from different steps overlapping, rapid sequences — each prompt drawing its own target ring produced N identical stacked rings. The `TargetZone` node draws it exactly once regardless of how many circles are live.

**Lifecycle:**
- `BattleTest._Ready` grabs the node reference via `GetNode<TargetZone>("TargetZone")`.
- Shown + positioned at `ComputeCameraMidpoint()` when an enemy sequence starts (`BeginEnemyAttack`) or a player attack prompt is added (`BeginAttack` hop-in callback).
- Hidden in `OnEnemySequenceCompleted` and `OnPlayerPromptCompleted`.

**`TimingPrompt` draws only:**
- The moving ring (`_currentRadius` → `TargetRadius`)
- Hit/miss flash feedback rings

It does **not** draw the target ring or hit-window band — those belong exclusively to `TargetZone`.

**Visual constants** in `TargetZone.cs` must stay in sync with `TimingPrompt.cs`:
`TargetRadius = 28f`, `RingLineWidth = 6f`, `ColorTarget`, `ColorHitWindow`.

### Attack Data Model

**One `AttackStep` = one animation play + one or more timing circles.**

`ImpactFrames` is an array of zero-based frame indices within that single animation play. Each entry produces one independent timing circle. The animation plays exactly once regardless of how many circles the step contains.

**Multiple animation plays = multiple `AttackStep` objects** chained in `AttackData.Steps`. `StartOffsetMs` on each step controls when it starts relative to the previous step's last circle resolving:

| `StartOffsetMs` | Effect |
|---|---|
| `> 0` | Pause N ms after previous step's last circle resolves before starting this step |
| `0` | Start immediately when previous step's last circle resolves |
| `< 0` | Start N ms *before* previous step's last circle resolves — steps overlap/run concurrently |

Negative values are clamped to 0 if the overlap would push the start before the sequence began. Ignored on step 0.

**Authoring guide:**
- Single hit: `ImpactFrames = [6]` — one circle, animation starts delayed so frame 6 lands on close
- Multi-hit in one animation: `ImpactFrames = [6, 7, 8]` — three circles staggered, one animation play
- Two separate animations: two `AttackStep` objects each with their own `ImpactFrames` and `SpritesheetPath`
- Fast chained combo (overlapping): step 2 `StartOffsetMs = -300` starts while step 1's last circle is still closing

### Attack Timing System — Impact-Frame Sync

Impact-frame anchor formula: `animationStartDelay = circleCloseDuration - (ImpactFrames[0] / fps)`. Clamped to `≥ 0`. When negative, the animation skips ahead to `animStartFrame = round(|rawDelay| × fps)` — must set `sprite.Frame` **after** `sprite.Play()` because Godot 4 resets Frame to 0 on Play().

Step scheduling is timer-driven, not completion-driven. Negative `StartOffsetMs` causes steps to overlap (concurrent animations and circles). `_totalPromptsRemaining` counts all circles across all steps; `SequenceCompleted` fires when it reaches 0.

### Hop-In Per-Step Enemy Animation System

Multi-step hop-in melee attacks drive the enemy's own sprite animation per step using three mechanisms:

- **`AttackStep.EnemyAnimation`** — name of the enemy animation to play at the start of this step (e.g. `"melee_attack"`, `"light_attack"`). Empty string = no enemy animation driven (cast attacks use this). Ignored when `AttackData.IsHopIn` is false.
- **`AttackStep.WaitAnimation`** — name of an animation to freeze on frame 0 during the gap before the **next** step plays (the idle-pose substitute during `StartOffsetMs` delays between steps). Read from the **next step** in `OnEnemyAttackAnimFinished` so the enemy holds a wind-up pose during the wait. Empty = plays idle.
- **`BattleSystem.StepStarted(int stepIndex, SequenceContext ctx)`** signal — emitted at the top of `RunStep()` before circles spawn. `BattleTest.OnBattleSystemStepStarted` subscribes and plays `step.EnemyAnimation` after the impact-frame-sync delay so the animation's impact frame lands when the first circle closes.

Step scheduling stays timer-driven in `BattleSystem.RunStep` — `StartOffsetMs` controls the gap between steps. For hop-in combos, authors must set `StartOffsetMs` large enough for the previous animation to complete (e.g. 16 frames at 12 fps ≈ 1.33 s) or use `WaitAnimation` to show a held pose during the gap.

`ProceedAfterHopInAnim` reads `GetLastStepPostAnimDelayMs()` — the **last** step's `PostAnimationDelayMs` controls the hold before retreat on multi-step attacks.

Enemy Physical miss cancellation is gated by `_sequenceContext.Attacker.Side == CombatantSide.Player` (inside `BattleSystem.OnAnyCircleCompleted`) so enemy attacks always play their full sequence regardless of parry outcome. Only player Physical attacks cancel on miss.

### Player Menu Structure

The battle menu is a `CanvasLayer` with two `PanelContainer` panels — only one visible at a time.

**Main menu** (shown after every enemy turn):
| Option | Action |
|---|---|
| Attack | Standard circle prompt; single-hit with `combo_slash1` animation |
| Absorbed Moves | Opens the submenu |

**Absorbed Moves submenu:**
| Option | Action |
|---|---|
| Combo Strike | Bouncing circle prompt; three-pass combo animation sequence |
| Back | Returns to main menu |

Navigation: `ui_up` / `ui_down` to move, `battle_confirm` to select. Disabled options render in grey and are skipped during navigation.

### Player Animation System — Knight Sprite

All knight animations use 120×80 px frames from horizontal-strip PNGs at `res://Assets/Characters/Knight/`.

**Combo sheet frame layout** (`_AttackComboNoMovement.png`, 10 frames 0–9):

| Named animation | Sheet frames | Usage |
|---|---|---|
| `combo` | 0–9 (all) | Frame index reference for wind-up holds |
| `combo_slash1` | 1–3 | First strike (single attack; combo passes 0 and final) |
| `combo_slash2` | 6–9 | Second strike (combo pass 1) |

**Parry** (`_Attack2NoMovement.png`, frames 2–5): plays on every successful enemy-attack block.

**Wind-up hold behaviour:** after the hop-in completes, the sprite freezes on the first wind-up pose via `Animation = "combo"; StopAnim(player); SetAnimFrame(player, 0)`. `StopAnim` is called before `SetAnimFrame` to counteract Godot 4's reset-to-0 on a stopped non-looping animation.

**Parry and hit frame-hold pattern:** `OnParryFinished` and `OnHitAnimFinished` use a variant of the combo wind-up's Stop/Frame ordering — `int lastFrame = _playerAnimSprite.Frame; StopAnim(player); SetAnimFrame(player, lastFrame);`. Where the combo wind-up deliberately sets a specific target frame (`StopAnim` then `SetAnimFrame(player, 0)`), the parry/hit handlers capture the *natural* last frame of the just-completed animation so the held pose always matches wherever the animation ended. Capturing `Frame` *before* `StopAnim` is load-bearing because Godot 4's `AnimatedSprite2D.Stop()` resets `Frame` to 0 on a finished non-looping animation; `SetAnimFrame` after `Stop` restores the actual last pose. (The `Frame` read is a direct sprite access — there is no guard-helper for read-only operations.) A `CreateTimer` then holds the frozen frame before returning to `idle` — **3 frames** (3/12 ≈ 0.25s) on parry, **4 frames** (4/12 ≈ 0.333s) on hit. The asymmetry is intentional: parry's animation (frames 2–5 of `_Attack2NoMovement.png`) already has visible follow-through that registers the action, so the post-hold needs to carry less of the beat. Hit is currently a 1-frame animation with no follow-through, so it leans harder on the hold.

**Retreat (PlayAnimBackwards):**
1. Before retreat: `SpriteFrames.SetAnimationLoop("run", false)` so `AnimationFinished` fires at frame 0.
2. `SpeedScale = 2f`, `PlayAnimBackwards(_playerParty[0], "run")` — snappy hop-back.
3. `OnRetreatFinished` resets `SpeedScale = 1f`, restores `SetAnimationLoop("run", true)`, returns to `idle` only if `Animation == "run"` (guards against a concurrent parry/hit taking ownership).

**Item use (`_ItemUse.png`, 9 frames, no loop):** `item_use` plays when the player uses the Ether item. Triggered from `BattleMenu` → `UseEtherItem()` in BattleTest. At frame 6 of the animation, `SpawnEtherEffect()` spawns the Blue Descending Circle Buff sprite overlaid on the player, plays `cure_spell.wav`, and calls `RestoreMp(20)`. The Ether effect data comes from `player_ether_item_use.tres` — an `AttackData` used as pure visual data (no circles, not routed through `BattleSystem.StartSequence`). When the animation finishes, `OnEtherAnimationFinished` returns the player to idle, then `BeginEnemyAttack` fires after 0.5 s.

**AddPlayerAnimationMixed** (BattleAnimator.cs): utility helper that registers an animation sampled from a list of `(texture path, frame index)` pairs — supports non-contiguous frames and frames pulled from multiple source strips. Retained for future custom multi-source animations; not currently used (all active player animations use the contiguous-range `AddPlayerAnimation` helper).

### Dead-Flag Guards and Unified Sprite Helpers

Once `Combatant.IsDead` is true, no further animation calls can override the death pose. All sprite interactions route through five unified target-aware helpers in `BattleAnimator.cs`:

```csharp
PlayAnim(Combatant target, string anim)
PlayAnimBackwards(Combatant target, string anim)
StopAnim(Combatant target)
SetAnimFrame(Combatant target, int frame)
SafeDisconnectAnim(Combatant target, Action handler)
```

Each guards on `!target.IsDead` before touching `target.AnimSprite`. `SafeDisconnectAnim` wraps the `Callable.From(handler)` + `IsConnected` check so redundant `-=` never throws "Attempt to disconnect a nonexistent connection".

**Call-site pattern:** callers pass the combatant the operation targets — usually `_playerParty[0]` or `_enemyParty[0]` directly, or a semantic local already in scope. Animation-callback subscriptions stay on the singleton sprite fields:

```csharp
SafeDisconnectAnim(_enemyParty[0], OnCastIntroFinished);
PlayAnim(_enemyParty[0], "cast_intro");
_enemyAnimSprite.AnimationFinished += OnCastIntroFinished;
```

**Handler signatures stay parameterless.** `OnParryFinished`, `OnCastIntroFinished`, `OnEnemyAttackAnimFinished`, etc. fire as Godot `AnimationFinished` callbacks — adding a `Combatant` parameter would require every `+=` site to wrap the handler in a closure, breaking the reference-equality that `SafeDisconnectAnim` depends on. Handler bodies pass the appropriate singleton to helpers (`PlayAnim(_enemyParty[0], "idle")`). See the Deferred Architecture Work section.

**Direct sprite access that bypasses the guards** is used deliberately in a small set of sites: death-animation plays right after `IsDead = true` is set (the guard would block), `Stop()` + `Frame = X` frame-freeze patterns after `PlayAnim`, and `Frame` reads in the capture-before-Stop pattern. Grep `_(player|enemy)AnimSprite\.(Play|Stop|Frame|IsConnected|Disconnect)` to enumerate them.

### Safe Disconnect Entry-Site Pattern

All `AnimationFinished` `+=` pairings call `SafeDisconnectAnim` on the same target **before** subscribing, to prevent handler stacking across turns:

```csharp
SafeDisconnectAnim(_enemyParty[0], OnCastIntroFinished);
PlayAnim(_enemyParty[0], "cast_intro");
_enemyAnimSprite.AnimationFinished += OnCastIntroFinished;
```

**Entry-site disconnect when state transitions away from a handler:** `PlayParryCounter` calls `SafeDisconnectAnim(_playerParty[0], OnParryFinished)` at its first line. Without this, the parry animation's natural `AnimationFinished` signal fires mid-counter-sequence and invokes `OnParryFinished`, which schedules a `PlayAnim(_playerParty[0], "idle")` ~250ms later — producing a visible idle frame between the parry and the counter's wind-up. The entry-site disconnect cancels the handler before the transition so the counter's own animation takes over cleanly.

**Pattern generalisation:** when a handler is bound to one animation's completion but the code path is superseded by another, disconnect at the new path's entry before `PlayAnim` takes sprite ownership.

### Enemy Animation System — Data-Driven

`BuildEnemySpriteFrames()` is fully data-driven — no hardcoded enemy sprite layout remains. All animation row indices, frame counts, and spritesheet paths are read from `EnemyData.AnimationConfig` (an `EnemyAnimationConfig` resource).

**Key fields on EnemyAnimationConfig:**
- `IdleRow/Frames`, `RunRow/Frames`, `CastIntroRow/Frames`, `CastLoopRow/StartCol/Frames`
- `HasCastEnd` — when false, all `cast_end` plays are replaced with `idle` transitions
- `MeleeAttackRow/Frames/ImpactFrame` — used for hop-in melee attacks
- `HurtRow/Frames` — main-sheet hurt animation; `HurtSheetPath` + `HurtFullFrames` for enemies with a separate hurt spritesheet (e.g. 8 Sword Warrior)
- `DeathRow/Frames` — blank transparent frame appended at `FrameWidth×FrameHeight` held 0.5s

**Absorption system:** `TryTriggerAbsorption(ctx)` adds the move to `_absorbedMoves` (a party-level `HashSet<AttackData>` for per-move-type tracking) and assigns the latest to `_absorbedMoveAttack` for the submenu label. `EnemyData.LearnableAttack` sources the move; the absorbed move's `DisplayName` drives the submenu label.

### Input Lock and Sequence Safety

`_inputLocked` — set `true` when player attack prompts resolve (OnPlayerPromptCompleted, OnPlayerMagicSequenceCompleted), cleared when the next input-accepting state begins (ShowMenu, BeginEnemyAttack). All input is blocked during slash animations, retreat, and teardown.

`BattleSystem._sequenceActive` — set `true` on `StartSequence()`, set `false` on first `SequenceCompleted` emission. Guards `RunStep` and `OnAnyCircleCompleted` from firing after a sequence completes, preventing negative `_totalPromptsRemaining` and double `SequenceCompleted` signals.

`BeginEnemyAttack()` reentrancy guard — early-returns if `_state` is already `EnemyAttack`.

### End-Screen Input Routing

**Rule:** in `BattleTest._Input`, end-screen state routing happens **before** the `_inputLocked` early-return:

```csharp
public override void _Input(InputEvent @event)
{
	if (_state == BattleState.GameOver) { HandleGameOverInput(@event); return; }
	if (_state == BattleState.Victory)  { HandleVictoryInput(@event);  return; }

	if (_inputLocked) return;
	// ... combat-phase routing ...
}
```

**Why:** `_inputLocked` is a *combat-phase* signal — it blocks input during slash animations, retreats, and teardowns. End-screens (`GameOver`, `Victory`) are *post-combat* states whose entire purpose is to accept player input for option selection. Gating them on a combat-phase flag is a category error.

**Historical context:** the Victory panel was silently non-responsive as initially wired. The killing-blow player attack leaves `_inputLocked = true` at the end of `OnFinalSlashFinished` and no subsequent code path clears it. With an `_inputLocked`-first ordering, every keypress on the Victory panel early-returned before reaching `HandleVictoryInput`. Game Over was coincidentally unaffected because `BeginEnemyAttack` clears `_inputLocked = false` at the start of every enemy turn, so by the time player death fires, the lock is already down — but the same class of bug was latent on any future Game Over trigger site where the lock happens to be up.

**Rule for adding a new end-screen state:** route it above the `_inputLocked` guard, and do not rely on any combat-phase code path to clear the lock for you. End-screen handlers should assume the lock can be in any state on entry and ignore it.

### Player Attack Prompt Cleanup

Any active player-attack `TimingPrompt` must be forcibly freed at the top of `BeginEnemyAttack()` via `FreeActivePrompt()` — do not rely on delayed flash-duration timers to clean up prompts before the enemy turn starts. The prompt may still be alive and emitting `PassEvaluated` signals when the enemy sequence begins, causing combo animation callbacks to fire on enemy circle results. `_isComboAttack` is also reset to `false` at the top of `BeginEnemyAttack()` as a secondary guard.

### Phase 1 → Phase 2 Transition

Triggered automatically on Warrior (Phase 1) death when `BattleTest.Phase2EnemyData` is assigned — defaults to `res://Resources/Enemies/8_sword_warrior_phase2.tres` via a `_Ready()` fallback.

**Inspector exports:**
- `Phase2EnemyData` — Phase 2 `EnemyData` resource. `null` disables the transition; default fallback loaded at boot.
- `TestPhaseTransition` — start the battle with Warrior HP = 1 so the first hit triggers the transition.

**State flags** (in `BattleTest.cs`):
| Flag | Purpose |
|---|---|
| `_phaseTransitionConsumed` | Point-of-no-return. Set at the top of `ApplyPhase2Sprite`; `IsPhaseTransitionPending()` returns false once true. Blocks re-entry for any subsequent enemy death. |
| `_phase2SpriteApplied` | Idempotent guard on `ApplyPhase2Sprite`. |
| `_phase2Finalised` | Idempotent guard on `SwapToPhase2` — the state-finalisation step runs exactly once. |
| `_revealSprite` | One-off `AnimatedSprite2D` node; freed in `ApplyPhase2Sprite`. |
| `_enemyZIndexBeforeReveal` | Snapshot of the warrior's ZIndex, restored after the reveal ends. |

**Sequence** (at Warrior HP = 0 with player attack):
1. Enemy sprite plays `death` (slowed to 6 fps on the Warrior via `EnemyAnimationConfig.DeathFps`).
2. `ScheduleBossRevealIfPhase1()` at every enemy-death-trigger site fires a `4 / DeathFps` timer (~0.67s) → `SpawnBossReveal()`.
3. `SpawnBossReveal()` constructs a single `"default"` animation **in code** from two textures: 12 frames from `8_sword_warrior__red_boss_reveal.png` (row 0, 160×160) **followed by 3 cycles of the 8 Sword Warrior idle** (row 0 of `8_sword_warrior_red-Sheet.png`, 14 frames × 3 = 42 frames). Total 54 frames @ 12 fps, `loop = false`. `FlipH = true` to mirror-match the 8 Sword Warrior's gameplay orientation. Position = warrior's local position − `(0, 10)` (10px lift to align visually with the Phase 2 idle).
4. Reveal plays through concurrently with the warrior's slowed death. Warrior AnimationFinished fires `OnEnemyDeathFinished`.
5. `OnEnemyDeathFinished` calls `ApplyPhase2Sprite()` which: sets `_phaseTransitionConsumed = true`, frees the reveal, reassigns `EnemyData = Phase2EnemyData`, disconnects every stale enemy `AnimationFinished` handler (`OnEnemyDeathFinished`, `OnCastIntroFinished`, `OnCastEndFinished`, `OnEnemyAttackAnimFinished`, `OnEnemyHurtFlashFinished`), nulls `SpriteFrames` to defeat the `BuildEnemySpriteFrames` early-return, rebuilds frames for the new enemy, repositions via the standard floor-anchored formula, and plays `idle`.
6. 0.5s pause → `ShowBattleMessage("You've only just begun to suffer.")` → 3s timer → `SwapToPhase2()` finalises state: resets the enemy Combatant (`MaxHp`, `CurrentHp`, `IsDead = false`, reassigns `Data`/`Name`), updates the enemy name label, calls `UpdateHPBars()`, clears per-fight state on the player Combatant (`IsBeckoning`, `IsDefending`) and BattleTest (`_parryClean`, `_pendingGameOver`, hop-in rendezvous flags, `_lastAttackIndex`). `_absorbedMoves` is NOT reset — per-move-type absorb tracking persists across the phase transition so Phase 2's learnable is absorbable regardless of Phase 1 state.
7. 0.5s → `ShowMenu()` — Phase 2 begins.

**ZIndex layering** during the reveal sequence (everything ≥ 0 so the default-ZIndex `Background` ColorRect stays at the bottom):
| Layer | ZIndex |
|---|---|
| Background ColorRect (scene, tree-order 0) | 0 |
| Reveal sprite | 1 |
| Warrior (bumped during reveal) | 2 |
| Effect sprites (`SpawnEffectSprite`) | 3 |

`PlayTeardown` normally resets `_enemyAnimSprite.ZIndex = 0` at the end of every attack tween — that clobber would knock the warrior below the reveal mid-sequence, so it is guarded with `if (!_enemyParty[0].IsDead)`. `SwapToPhase2` restores the pre-reveal snapshot explicitly.

**Player retreat in the killing-blow path:** `OnFinalSlashFinished` and `BeginComboMissRetreat`'s `_pendingGameOver` branches run the same 0.3s hold + `PlayAnimBackwards(_playerParty[0], "run")` + `PlayTeardown` + `OnRetreatFinished → PlayAnim(_playerParty[0], "idle")` treatment as the non-game-over path. Without this the player freezes on the last slash frame because `PlayTeardown(null)` only tweens positions; the run-backwards animation and idle return are what make the retreat visually complete. Required for Phase 2 to start with the player idling.

**Stale-handler disconnect in `ApplyPhase2Sprite`:** every named handler that might still be bound to `_enemyAnimSprite.AnimationFinished` from Phase 1 is disconnected before `BuildEnemySpriteFrames` runs. Without this, the first Phase 2 animation completion (idle → hurt → cast_intro) would fire the stale `OnEnemyDeathFinished` and drop straight into the Victory path.

**Reveal-sprite missing-asset fallback:** if `8_sword_warrior__red_boss_reveal.png` fails to load, `SpawnBossReveal` logs an error and calls `SwapToPhase2()` directly. If the appended idle sheet is missing, the reveal plays with only the 12 reveal frames.

### Victory Screen

Two-panel structure: the unchanged fullscreen `"Victory!"` label (font 64, center, fades in over 0.5s — constructed by `ShowEndLabel` in `BattleAnimator.cs`) plus a lower Retry/Close options panel that fades in after a 1.5s beat. The beat lets the player experience the win before being offered the next action.

**State:** `BattleState.Victory` (added to the enum alongside the existing `GameOver`). `_state` transitions to `Victory` at the moment the options panel's fade-in tween starts, inside `ShowVictoryOptionsPanel`. `_Input` routes to `HandleVictoryInput` while `_state == Victory` — see the End-Screen Input Routing section for the routing rule.

**Panel scheduling** (in `ShowEndLabel`, Victory branch): after the wrapper's 0.5s fade-in tween is kicked off, a `GetTree().CreateTimer(2.0f).Timeout` (0.5s for the label fade-in + 1.5s beat) calls `ShowVictoryOptionsPanel`. Guarded by `IsInstanceValid(this)` to handle mid-timer scene reloads.

**Options panel layout** (built in `ShowVictoryOptionsPanel` in `BattleTest.cs`):
- Dedicated `CanvasLayer` named `"VictoryOptionsLayer"`.
- Wrapper `Control` (FullRect, modulate alpha 0 → 1 over 0.5s).
- `MakeLayeredPanel(minWidth: 400f)` (shared Kenney-border panel helper) anchored viewport-center (0.5/0.5 all axes, `GrowDirection.Both`) with `OffsetTop = OffsetBottom = 200f` — places the panel 200px below viewport center so the `"Victory!"` label at center stays legible above it on a 1080p viewport.
- 8px top spacer, `AddVictoryOptions(content)`, 8px bottom spacer. **No divider** — the Victory! label outside the panel already serves as the headline, so a divider inside would be decorative without purpose.

**Parallel field set** — kept independent from the Game Over panel's field set even though the two look similar:
| Field | Purpose |
|---|---|
| `_victoryOptionIndex` | 0 = Retry, 1 = Close |
| `_victoryTextLabels` | Centered option text; yellow when selected, white otherwise (same convention as Game Over) |
| `_victoryArrows` | ▶ cursor labels; `Visible` toggled per selection |
| `_victoryInputUnlockedAtMsec` | 150ms input buffer — input ignored while `Time.GetTicksMsec() < this value` |
| `VictoryOptionLabels` | `{ "Retry", "Close" }` |

The independence is deliberate. `AddVictoryOptions` / `RefreshVictoryOptions` / `HandleVictoryInput` are structurally parallel to `AddGameOverOptions` / `RefreshGameOverOptions` / `HandleGameOverInput` but operate on their own field set. Sharing state would couple the two end-screens — a future change to one (e.g. adding a third option) would need to consider its effect on the other. (This structural duplication is a candidate for extraction — see the Phase 1 code review plan at `docs/phase1-code-review-plan.md`.)

**Input buffer (150ms):** `_victoryInputUnlockedAtMsec = Time.GetTicksMsec() + 150` is set at the top of `ShowVictoryOptionsPanel` (before the fade-in tween starts, not after). `HandleVictoryInput` early-returns while the current tick count is below this timestamp. The buffer prevents a held `battle_confirm` from the killing blow from immediately selecting an option the moment the panel appears.

**Actions:**
- Retry → `FadeToBlackAndReload()` — reuses the existing Game Over Retry handler (0.5s fade to black + 0.5s hold + `ReloadCurrentScene`).
- Close → `GetTree().Quit()` — exits the game.

### Test Flags

Development scaffolding — `[Export] bool` flags on `BattleTest` that shortcut the battle into specific states for testing end-of-battle screens and phase behavior without playing through a full fight. All are forgiving dev scaffolding, not production config: should be left `false` in committed state.

| Flag | Effect | Skips intro? |
|---|---|---|
| `TestVictoryScreen` | Swaps `EnemyData` to `Phase2EnemyData` before sprite build, sets `SkipHopIn = true`, sets `_enemyParty[0].CurrentHp = 1`, sets `_phaseTransitionConsumed = true` so enemy death goes straight to Victory (not the Phase 1 → Phase 2 reveal), starts Phase 2 music. First player attack triggers Victory. | Yes |
| `TestGameOverScreen` | Sets `_playerParty[0].CurrentHp = 1`, starts Phase 1 music. First missed parry triggers Game Over. | Yes |
| `TestPhaseTransition` | Sets `_enemyParty[0].CurrentHp = 1` at battle start so the first player hit against the Warrior triggers the Phase 1 → Phase 2 reveal at full fidelity (reveal sprite, battle message, music swap all play normally). Documented in context in the Phase 1 → Phase 2 Transition section. | No |

**Priority** (resolved at the top of `_Ready` before any state is applied): `TestVictoryScreen` > `TestGameOverScreen` > `TestPhaseTransition`. If multiple flags are `true`, the highest-priority one wins and the others are logged-and-ignored with a `[TEST]` warning rather than erroring out — the flags are forgiving rather than strict.

**Startup log:** whichever flag is active emits a `[TEST] <FlagName> active — <what was changed>` line. Makes it obvious in the Godot output panel that a test flag is on, so "test output" isn't mistaken for "actual game behavior" later.

**Intro-dialogue skip:** `TestVictoryScreen` and `TestGameOverScreen` both skip `PlayIntroDialogue` — sitting through 5 lines of dialogue every iteration defeats the purpose of the flag. `TestPhaseTransition` does not skip the intro (its purpose is to exercise the phase transition, not the end-screen flow, so the usual intro is part of the path being tested).

**Phase2EnemyData fallback hoist:** the default `Phase2EnemyData` resource load (from `res://Resources/Enemies/8_sword_warrior_phase2.tres`) is hoisted to the top of `_Ready` — earlier than strictly needed for non-test paths — so `TestVictoryScreen` can reassign `EnemyData = Phase2EnemyData` before the enemy sprite is built downstream. Non-test flow is unchanged.

### SkipHopIn Flag and FloorY Constant

`[Export] public bool SkipHopIn = true` — when set, the enemy stays at origin for the entire turn. Setup and teardown tweens are skipped (no hop-in, no camera zoom). `_sequenceAttackerClosePos` is set to the enemy origin so `PlayTeardown` is a zero-distance no-op. Used for large/stationary enemies like the 8 Sword Warrior. Set to `false` for the Warrior Phase 1 which hops in for melee attacks.

`const float FloorY = 750f` — world-space Y of the ground line. All character sprites are floor-anchored:
- Player: `Position.Y = FloorY - frameHeight * scale * 0.5f` (center-anchored sprite)
- Enemy: `Position.Y = FloorY - EnemyData.FrameHeight * 3f * 0.6f + EnemyData.SpriteOffsetY` (per-enemy tuned nudge for visual ground contact)
- Effects: `Position = (targetCenter.X, FloorY) + activeOffset` where `targetCenter = ctx.Target.Origin + ctx.Target.PositionRect.Size / 2f` and `activeOffset = attackerOnRight ? step.Offset : step.PlayerOffset` (`attackerOnRight` derived from attacker-vs-target X positions in `BattleSystem.SpawnEffectSprite`). `step.Offset.Y < 0` moves up, `> 0` moves down. `sprite.Centered = true` is set explicitly in `SpawnEffectSprite` — the floor-baseline formula depends on this being true. `step.Scale` controls the sprite scale; default `Vector2(3, 3)` is the standard 3× world-space upscale used for all effect sheets.

`EnemyData.SpriteOffsetY` — per-enemy additional downward nudge on the enemy sprite, tuned visually. Warrior Phase 1 = 90f, 8 Sword Warrior = 130f.

### Text UI Systems

Two distinct text-display components serve different UX purposes. They are **not** candidates for unification — their UX roles genuinely differ. The rule for adding new text UI:

- **Character speech during a narrative pause** → `BattleDialogue`
- **System notification during a mechanical event** → `BattleMessage`

| System | Use for | Skippable? | Speaker tag? | Multi-line? | Auto-dismiss |
|---|---|---|---|---|---|
| `BattleDialogue` | Intro dialogue, mid-battle boss taunts, cutscene-style narrative beats | Yes — player advances with `battle_confirm` | Yes — per-line speaker field drives name-tag color | Yes — sequence of `DialogueLine` entries | Per-line `AutoAdvanceSeconds`, or on input |
| `BattleMessage` | Learnable-move signal, phase-transition flavor, absorb feedback, any system-level prompt | **No** — duration-based auto-dismiss only | No | No — single line | Fixed `holdDuration` |

**Why `BattleMessage` is intentionally non-skippable:** the player's input channel is reserved for parry / combat during the moments `BattleMessage` is shown. Making it input-skippable would hijack that channel and make system feedback feel like it requires acknowledgment. Do not add skip-on-input to `BattleMessage`.

**Why `BattleDialogue` is intentionally skippable:** during narrative pauses the player is explicitly not engaging with combat mechanics; the input channel is free and players have different reading speeds.

**Signals emitted by `BattleDialogue`:**
- `FadeOutStarted` — fires when the final line's advance kicks off the panel's fade-out tween. Callers use this to begin cross-fading other elements (e.g. music fade-in) concurrent with the visual handoff, rather than strict after-the-fact sequencing.
- `DialogueCompleted` — fires after the fade-out tween plus `PostDialogueBufferSec` (default 150ms, prevents final input from bleeding into the next input-accepting state).

**Dialogue node lifetime:** a `BattleDialogue` instance is constructed for each dialogue sequence and `QueueFree`'d after `DialogueCompleted`. Future dialogue (e.g. Phase 2 boss taunts) constructs a fresh instance. This keeps state clean and avoids hidden reuse bugs.

**Shared infrastructure note:** both `BattleMessage` and `BattleDialogue` duplicate low-level patterns — a high-Layer `CanvasLayer`, a bottom-anchored `MakeLayeredPanel`, a `modulate:a` fade tween, inline panel-inset constants. Worth extracting a shared `BottomCenteredOverlayPanel` helper during the Phase 1 code review; the systems themselves stay separate.

### Battle Start Flow and Intro Dialogue

`BattleTest._Ready` does not start music or show the menu directly — both are gated on the intro dialogue completing. The critical path is `_Ready` → `PlayIntroDialogue` → signal chain → music + menu + turn flow.

**`_Ready` sequencing:**
1. Build UI (status panels, music player with no stream playing yet).
2. Load resources, build sprites, connect `BattleSystem` signals.
3. Resolve test flags. Priority: `TestVictoryScreen` > `TestGameOverScreen` > `TestPhaseTransition` (overridden flags log `[TEST]` warnings). If either `TestVictoryScreen` or `TestGameOverScreen` is active, the intro dialogue is skipped — music and menu start immediately.
4. `BuildMenu` + `UpdateHPBars` (menu is built but `_menuLayer.Visible = false`).
5. If intro was skipped: start phase-appropriate music (`StartPhase1Music` or `StartPhase2Music`) and call `ShowMenu` immediately. **Else:** set `_inputLocked = true` and call `PlayIntroDialogue`.

**Intro dialogue signal chain:**
- `PlayIntroDialogue` constructs a new `BattleDialogue`, hardcodes the 5 opening lines, connects `FadeOutStarted` → `OnIntroDialogueFadeOutStarted` and `DialogueCompleted` → `OnIntroDialogueCompleted`, then a `CreateTimer(0.3)` kicks off `PlayDialogue(lines)`.
- `OnIntroDialogueFadeOutStarted` (fires when the final line's advance starts the panel fade-out tween) → `FadeInPhase1Music(1.5f)`. Music begins rising *during* the panel fade-out, not after, for a smoother audio-visual handoff than strict sequencing.
- `OnIntroDialogueCompleted` (fires at the end of the fade-out tween's chained timeline: `TweenProperty(modulate:a, 0, PanelFadeOutSec)` → `TweenInterval(PostDialogueBufferSec ≈ 150ms)` → `EmitSignal(DialogueCompleted)`) → `ShowMenuWithFadeIn(0.5f)` then `QueueFree` the dialogue node. Future dialogue constructs a fresh instance.

**Helpers added alongside the flow:**
- `FadeInPhase1Music(durationSec)` in `BattleTest` — symmetric to the existing `FadeOutMusic`. Plays the Phase 1 stream at `-80 dB` and tweens `volume_db` to `0` over the duration.
- `ShowMenuWithFadeIn(durationSec)` in `BattleMenu` — wraps `ShowMenu` with a pre-set `_mainMenuPanel.Modulate.a = 0` and a follow-up Modulate alpha tween, so the panel rises under the music rather than popping opaque.

**`DialogueLine` struct (consumed by `BattleDialogue.PlayDialogue`):**
| Field | Type | Purpose |
|---|---|---|
| `Speaker` | `string` | Displayed in the name-tag label; also the key for name-tag color selection (see below). |
| `Text` | `string` | Body text, revealed character-by-character. |
| `AutoAdvanceSeconds` | `float` | Seconds to wait after full reveal before auto-advancing. `battle_confirm` skips this wait. |
| `RevealSpeed` | `float` | Characters per second. `0` = use the component's `DefaultRevealSpeed` (40 cps). |

**Name-tag color dispatch** (in `BattleDialogue.StartNextLine`): `line.Speaker == "The Harbinger"` selects `HarbingerNameColor` (cold blue-grey); any other speaker string falls through to `ApprenticeNameColor` (parchment / off-white). The body-text color is `BodyColor` regardless of speaker — only the name tag carries the speaker's visual identity. The current in-game speaker label for the non-Harbinger voice is `"Knight"` (not `"Apprentice"`); the `ApprenticeNameColor` field name remains as the internal identifier for the non-Harbinger fallback color. The label-vs-identifier mismatch is intentional — see "Senior's fixed starting move set" in "Design Decisions — Pending Implementation" for the narrative rationale.

### Deferred Architecture Work

Three pieces of the Phase 3 refactor were deliberately deferred and remain out-of-scope until the triggering need arises.

**Handler signature generalization.** Animation callbacks (`OnParryFinished`, `OnCastIntroFinished`, `OnEnemyAttackAnimFinished`, etc.) stay parameterless and singleton-bound — see Dead-Flag Guards and Unified Sprite Helpers. Generalizing them to take `Combatant` would require every `+=` subscription site to use a closure, breaking the reference-equality that `SafeDisconnectAnim` depends on. Deferred until multi-character density actually needs per-unit handler dispatch.

**AttackStep.Offset / PlayerOffset schema consolidation (audit D5).** Today each `AttackStep` has two offset fields (`Offset` for right-side attackers, `PlayerOffset` for left-side). `SpawnEffectSprite`'s geometric `attackerOnRight` check already routes the right one. The fields could collapse to a single `TargetOffset` authored once. Deferred to an AttackData authoring pass where `.tres` files would need re-editing anyway.

**`PlayCombatantHurtFlash` load-bearing constraint.** Method body uses the unified guard helpers (post-3.7b), but the paired callback `OnEnemyHurtFlashFinished` is still singleton-bound (plays "idle" on `_enemyAnimSprite`). Passing a non-enemy `target` to `PlayCombatantHurtFlash` today would split the sprite between Play (target's) and Finished (enemy's). Safe because all current callers pass the enemy; constraint lifts when handler signatures generalize. Documented inline in the method's doc comment.

**Parry counter-attack refactor.** `PlayParryCounter` uses a hand-rolled nested `CreateTimer` cascade (wind-up → impact → slash effect → damage → hold) rather than routing through `BattleSystem.StartSequence` like every other attack. The hand-rolled approach makes timing fragile (changing one delay can desync the slash spawn from the player pose), duplicates logic in `SpawnEffectSprite`, and is not inspector-tunable. Proper fix: author the counter as an `AttackData` resource with its own `AttackStep`s and drive it through `BattleSystem.StartSequence`. Deferred because the counter has unique properties (no player prompt, fixed 20 damage, triggered from sequence completion rather than menu selection) that don't cleanly fit `StartSequence`'s current flow — the refactor is focused work, not a quick change. Triggers when combat pipeline changes (e.g., multi-character target selection) make the special-case inconsistency cost higher than the refactor cost.

## Audio Trigger Reference

### Event-Based Triggers (no frame sync needed)
| Sound | File | Trigger |
|---|---|---|
| Parry clash | parry_clash.wav | Each successful enemy attack block (Hit or Perfect result) |
| Perfect parry shimmer | perfect_parry_shimmer.wav | Full enemy sequence parried with no misses |
| Player hit | player_hit.wav | Player takes damage from a missed block |
| Enemy hit | enemy_hit.wav | Enemy takes damage from any player attack hit |
| Absorbed ability acquired | absorbed_ability_acquired.wav | Learnable move successfully absorbed |
| Learnable signal | learnable_signal.wav | Enemy selects their learnable move |
| Enemy defeat | enemy_defeat.mp3 | Enemy HP reaches zero |
| Victory | victory.wav | Victory screen appears |
| Game over | game_over.mp3 | Game over screen appears |

### Frame-Synced Triggers
| Sound | File | Animation | Frame | Notes |
|---|---|---|---|---|
| Player attack swing | player_attack_swing.wav | _AttackNoMovement | 0 | Basic attack swing |
| Counter swing | counter_swing.wav | _AttackNoMovement | 1 | Impact frame during parry counter |
| Counter slash multi hit | counter_slash_multi.wav | — | — | Plays when slash effect spawns |
| Magic launch | magic_launch.wav | CrouchAttack | 0 | When comet leaves player |
| Magic impact | magic_impact.wav | Blue_Magic_Comet_Sheet | 5 | At circle close on magic attacks |
| Fire hammer | fire_hammer.wav | Red_Fire_Hammer_Swipe_Sheet | 6 | At impact frame on hammer attacks |
| Parry clash | parry_clash.wav | _Attack2NoMovement | 2 | First frame of parry animation (sheet frames 2–5) |

## Known Next Steps

- **Battle menu UI polish** — layout, positioning, visual feedback
- **Hover descriptions / tooltips for menu options** (post-Phase-1 polish) — short flavor + mechanical description for each main-menu and submenu option (Attack, Defend, Beckon, Combo Strike, etc.) shown on selection focus, so the player understands what each ability does without trial-and-error
- **ATTACK_AUTHORING.md** — documentation for creating new AttackData/AttackStep resources
- **Reusable `AttackStep` resources** — refactor embedded sub-resources into standalone `.tres` files that can be referenced by multiple `AttackData` resources, avoiding duplication of shared values (frame dimensions, Fps, spritesheet path, etc.)
- **Self-targeting spell alignment** — Cure spell effect and target zone are not perfectly centered on the player's visual body due to the knight sprite having the character body left-of-center within its frame. Revisit when implementing the full character system — the correct fix is either adjusting the sprite frame composition or implementing a per-spell visual center offset.
- **Target selection + multi-character scaffolding** — Phase 3 landed on branch `target-selection-and-scaffolding`: Combatant abstraction, SequenceContext signal payload, unified guard helpers, TakeDamage/Heal, per-sequence cache cleanup. Phase 4+ remains: `SelectingTarget` state with player pointer, enemy target indicator, multi-character scaffolding exercise (4v5 party wiring). See docs/target-selection-and-scaffolding-plan.md for the phase plan.
- **Target selection audit** — see docs/target-selection-audit.md. Catalogues pre-refactor 1v1 assumptions; drove Phase 3's chunks. A/B/C/D/E/F/I/J categories landed; D5 (AttackStep.Offset/PlayerOffset schema consolidation) and full multi-character work remain — see the Deferred Architecture Work section below.
- **Phase 1 code review** — see docs/phase1-code-review-plan.md. Run before starting Phase 2 using /model opusplan in Claude Code.

## Design Decisions — Pending Implementation

These are resolved design decisions whose implementation is deferred to
future phases. They aren't tasks; they're constraints that future work
must honor.

### Prototype vs. full-game: Harbinger absorption
In the current prototype, the apprentice CAN absorb The Harbinger's
learnable move and use it mid-fight. This is retained for the
prototype because it's the only place in the prototype scope where
the full absorb loop (parry → learn → use) can be demonstrated to
the player.

In the full game (once the tutorial forest, cutscenes, and item
system exist), The Harbinger's move will NOT be absorbable in the
fight. Instead, winning (or surviving with good performance) drops
an item containing the move. The player cannot use the item during
the fight because the post-fight cutscenes (apprentice dies, senior
survives, time skip) run immediately. When the senior is playable
in the present, the item appears in his inventory and can be used
to learn the move at a cost.

This reinforces The Harbinger's otherworldliness — his moves are not
something you can simply witness and replicate; they require a
hard-won vessel to carry them out of the fight.

**Cleanup task when implementing:** remove the apprentice's ability
to absorb The Harbinger's learnable move; wire an item drop on fight
resolution; ensure the move is learnable from the item post-cutscene.

### Item-taught moves
Items represent a second progression axis distinct from absorption:

- **Absorption** — skill-based, in-the-moment, rewards perfect parrying
  against any enemy willing to show a learnable move. Covers the
  common enemy pool.
- **Items** — scarce, narrative-bound. Used for two categories:
  (1) exclusive moves that cannot be learned any other way, and
  (2) missable moves (e.g. unique boss moves) that would otherwise be
  permanently lost. Item-taught moves may be expensive to learn,
  particularly for missable-boss-move items, creating a real decision
  about when to spend resources.

The two systems don't overlap, so neither makes the other obsolete.

### Senior's fixed starting move set
When the senior knight becomes playable in the present day, his
starting move set is FIXED and includes the tutorial forest moves
regardless of what the apprentice absorbed during the tutorial.

This serves three simultaneous purposes depending on player state:
- **First-time player who absorbed normally:** no dissonance; the
  mechanical continuity reinforces their (false) belief that they are
  still the apprentice, strengthening the eventual identity twist.
- **First-time player who skipped tutorial absorptions:** a small
  moment of dissonance — "why do I have moves I didn't learn?" —
  which later resolves as "the senior taught those techniques; of
  course he knows them" once the twist lands.
- **Replay player who knows the twist:** can deliberately skip
  absorptions to confirm the senior has the moves regardless,
  verifying the game's design intent.

**Cleanup task when implementing:** the senior's move set should be
hardcoded/defined at character creation, not derived from the
apprentice's tutorial playthrough state.

### Intro dialogue skip-on-retry
The Phase 1 intro dialogue (`BattleTest.PlayIntroDialogue`) replays on
every battle scene reload, including Retry from Game Over. This is
retained for the prototype because:

- It verifies the dialogue system works correctly across scene reloads.
- All 5 lines are skippable in under a second with rapid `battle_confirm`
  presses, so the cost to a retrying player is low.

**Cleanup task when implementing:** before any real release, track whether
the intro has already been seen (session-scoped flag at minimum, ideally
persisted across sessions once a save system exists) and skip
`PlayIntroDialogue` on subsequent loads — proceed straight to the
post-dialogue state (start music, show menu). Retry flows should land
the player on the first input-accepting frame, not on a re-playing
cutscene.
