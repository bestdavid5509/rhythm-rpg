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

## Tech Stack

- **Engine:** Godot 4
- **Language:** C#
- **Runtime:** .NET 9

## Project Structure

### Scripts/Battle/
| File | Responsibility |
|---|---|
| `BattleTest.cs` | Core turn loop, state machine, lifecycle (`_Ready`/`_Input`), all field declarations, tween helpers, position helpers, shared combat helpers |
| `BattleAnimator.cs` | Sprite frame construction, all `AnimationFinished` callbacks, dead-flag guards (`PlayPlayer`/`PlayEnemy`/etc.), safe-disconnect helpers, end-of-battle overlay |
| `BattleMenu.cs` | Battle menu construction, navigation, input handling — main menu and Absorbed Moves submenu |
| `BattleSystem.cs` | Attack sequence runner — drives `AttackData` steps, spawns `TimingPrompt` circles and timed effect `AnimatedSprite2D` nodes, emits `StepPassEvaluated` / `SequenceCompleted` |
| `TimingPrompt.cs` | Single closing circle — Standard, Slow, and Bouncing variants; draws only the moving ring and hit/miss flash; does **not** draw the target ring (see `TargetZone`); emits `PassEvaluated` and `PromptCompleted`; static `ConfirmAll()` resolves all active circles on one input event |
| `TargetZone.cs` | Persistent shared target ring node in `BattleTest.tscn`; draws the stationary white ring and green hit-window band; shown/hidden by `BattleTest` at sequence start/end; has no knowledge of individual circles |
| `AttackData.cs` | `[GlobalClass]` Resource — ordered list of `AttackStep` objects and a `BaseDamage` value; saved as `.tres` files |
| `AttackStep.cs` | `[GlobalClass]` Resource — per-step data: `SpritesheetPath`, `FrameWidth`/`FrameHeight`, `Fps`, `ImpactFrames[]`, `CircleType`, `StartOffsetMs`, `FlipH`, `Offset` |

All three `BattleTest` files are `public partial class BattleTest : Node2D` and compile as one class.

### Scenes/Battle/
- `BattleTest.tscn` — main battle prototype scene
- `TimingPrompt.tscn` — circle prompt scene instantiated per step

### Resources/Attacks/
`.tres` files are Godot Resource instances of `AttackData`, editable in the inspector.

| File | Description |
|---|---|
| `red_sword_plunge.tres` | 1 step, 1 circle — `Red_Sword_Plunge_Sheet.png`, `ImpactFrames=[6]` |
| `red_triple_sword_plunge.tres` | 1 step, 3 circles — `Red_Triple_Sword_Plunge_Sheet.png`, `ImpactFrames=[6,7,8]` |
| `red_sword_combo_attack.tres` | 2 steps — triple plunge (3 circles) chained into single plunge (1 circle) ← active in `BattleSystem` |
| `blue_sword_plunge.tres` | Misnamed legacy file — actually uses `Red_Sword_Plunge_Sheet.png`; superseded by `red_sword_plunge.tres`, safe to delete |
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
	  8_Sword_Warrior_Red/         — active enemy in BattleTest (21 cols × 7 rows, 160×160 px)
	  8_Sword_Warrior_Blue/
	  8_Sword_Warrior_Black/
	Warrior/                       — multiple colour variants (unused in current prototype)
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

`BattleSystem.RunStep(int stepIndex)` launches one `AnimatedSprite2D` per step and one `TimingPrompt` per `ImpactFrames` entry. All timings are derived from the first impact frame as an anchor:

```
animationStartDelay  = circleCloseDuration - (ImpactFrames[0] / fps)
circleSpawnDelay[i]  = (ImpactFrames[i] - ImpactFrames[0]) / fps
```

- `circleCloseDuration` comes from `TimingPrompt.DefaultDurationForType(step.CircleType)`.
- `animationStartDelay` is clamped to `≥ 0` — a negative value means the first impact frame has already passed the circle close time; animation starts immediately.
- Circle 0 always spawns at delay 0. Subsequent circles are staggered forward by one frame-time each (≈83 ms at 12 fps for consecutive frames).
- **Step scheduling is timer-driven, not completion-driven.** Each `RunStep` immediately schedules the next step's start timer:
  ```
  lastCircleResolveTime = (ImpactFrames[last] - ImpactFrames[0]) / fps + circleCloseDuration
  nextStepDelay = max(0, lastCircleResolveTime + nextStep.StartOffsetMs / 1000)
  ```
- `_totalPromptsRemaining` counts all circles across all steps. `SequenceCompleted` fires when it reaches 0 — after the last circle of the last concurrent step resolves.
- Effect sprites free themselves when their animation finishes via a self-disconnecting `Action onFinished` delegate (avoids the Godot 4 double-disconnect error from `+= QueueFree`).

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

**Wind-up hold behaviour:** after the hop-in completes, `Animation = "combo"`, `Stop()`, `Frame = 0` freezes the sprite on the first wind-up pose while the player waits to input. `Stop()` is called before `Frame =` to counteract Godot 4's reset-to-0 on a stopped non-looping animation.

**Retreat (PlayBackwards):**
1. Before retreat: `SpriteFrames.SetAnimationLoop("run", false)` so `AnimationFinished` fires at frame 0.
2. `SpeedScale = 2f`, `PlayBackwards("run")` — snappy hop-back.
3. `OnRetreatFinished` resets `SpeedScale = 1f`, restores `SetAnimationLoop("run", true)`, returns to `idle` only if `Animation == "run"` (guards against a concurrent parry/hit taking ownership).

**Combo pass sequence:**
- Pass 0: `combo_slash1` → `OnComboPass0SlashFinished` holds `combo` frame 5 (second wind-up)
- Pass 1: `combo_slash2` → `OnComboPass1SlashFinished` holds `combo` frame 0 (first wind-up again)
- Pass 2 (final): `combo_slash1` → `OnFinalSlashFinished` holds last frame 0.3 s, then starts retreat

### Enemy Animation Arc — 8 Sword Warrior Red

Sheet: `8_sword_warrior_red-Sheet.png` — 160×160 px per frame, 21 cols × 7 rows.

| Animation | Row | Frames | Loop | Usage |
|---|---|---|---|---|
| `idle` | 0 | 14 | yes | Default between turns |
| `run` | 1 | 8 | yes | (reserved) |
| `attack` | 2 | 15 | no | (reserved) |
| `cast_intro` | 3 | cols 0–3 | no | Wind-up once before prompt appears |
| `cast_loop` | 4 | 14 | yes | Holds during entire prompt sequence |
| `cast_end` | 3 | cols 18–20 | no | Release once after sequence resolves |
| `death` | 6 | 15 + 1 blank | no | 15 sheet frames + 1 transparent 160×160 frame held 0.5 s |

**Turn arc:** `cast_intro` → (`OnCastIntroFinished`) → `cast_loop` → (sequence resolves) → `cast_end` → (`OnCastEndFinished`) → `idle`

**Blank death frame:** a fully transparent `ImageTexture` (160×160, RGBA8) is appended to the `death` animation with `duration: 6.0f` (at 12 fps → 0.5 s). This ensures the Victory label appears only after death particles have fully dissipated.

### Dead-Flag Guards

Once `_playerDead` or `_enemyDead` is set, no further animation calls can override the death pose. All sprite interaction routes through five helpers in `BattleAnimator.cs`:

```csharp
PlayPlayer(string anim)          // guarded by !_playerDead
PlayPlayerBackwards(string anim) // guarded by !_playerDead
StopPlayer()                     // guarded by !_playerDead
SetPlayerFrame(int frame)        // guarded by !_playerDead
PlayEnemy(string anim)           // guarded by !_enemyDead
```

### Safe Signal Disconnect Pattern

All `AnimationFinished` disconnects route through `SafeDisconnectPlayerAnim(Action)` / `SafeDisconnectEnemyAnim(Action)`, which check `IsConnected` before calling `Disconnect`. This prevents the Godot 4 "Attempt to disconnect a nonexistent connection" error.

Connect sites also call `SafeDisconnect` **before** `+=` to prevent handler stacking across turns:

```csharp
SafeDisconnectEnemyAnim(OnCastIntroFinished);
_enemyAnimSprite.AnimationFinished += OnCastIntroFinished;
```

### SkipHopIn Flag and FloorY Constant

`[Export] public bool SkipHopIn = true` — when set, the enemy stays at origin for the entire turn. Setup and teardown tweens are skipped (no hop-in, no camera zoom). `_attackerClosePos` is set to the enemy origin so `PlayTeardown` is a zero-distance no-op. Used for large/stationary enemies like the 8 Sword Warrior.

`const float FloorY = 750f` — world-space Y of the ground line. All character sprites are floor-anchored:
- Player: `Position.Y = FloorY - frameHeight * scale * 0.5f` (center-anchored sprite)
- Enemy: `Position.Y = FloorY - 160f * 3f * 0.6f + EnemySpriteOffsetY` (tuned nudge for visual ground contact)
- Effects: `centerY = FloorY - step.FrameHeight * EffectScale * 0.5f` + `step.Offset`

`const float EnemySpriteOffsetY = 130f` — additional downward nudge on the enemy sprite, finalized visually.

## Known Next Steps

- **Screen shake** — add a `Camera2D` shake tween on Miss (enemy hits player) and on player attack landing
- **Audio** — hit/miss/parry/perfect SFX; music layers for phase transitions
- **Real boss attack sequence** — replace `blue_sword_plunge.tres` with a multi-step `AttackData` resource representing the opening boss Phase 1 pattern
- **Bouncing animation replay** — `AttackStep` has a documented hook: replay the effect animation once per pass by subscribing to `TimingPrompt.PassEvaluated` (currently the effect plays once regardless of bounce count)
- **Learnable move signalling** — visual highlight on enemy and colored move-name label during learnable-move sequences
- **Taunt ability** — player action that baits the enemy into using their signature/learnable move
