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

### Enemy Animation System — Data-Driven

`BuildEnemySpriteFrames()` is fully data-driven — no hardcoded enemy sprite layout remains. All animation row indices, frame counts, and spritesheet paths are read from `EnemyData.AnimationConfig` (an `EnemyAnimationConfig` resource).

**Key fields on EnemyAnimationConfig:**
- `IdleRow/Frames`, `RunRow/Frames`, `CastIntroRow/Frames`, `CastLoopRow/StartCol/Frames`
- `HasCastEnd` — when false, all `cast_end` plays are replaced with `idle` transitions
- `MeleeAttackRow/Frames/ImpactFrame` — used for hop-in melee attacks
- `HurtRow/Frames` — main-sheet hurt animation; `HurtSheetPath` + `HurtFullFrames` for enemies with a separate hurt spritesheet (e.g. 8 Sword Warrior)
- `DeathRow/Frames` — blank transparent frame appended at `FrameWidth×FrameHeight` held 0.5s

**Absorption system:** `TryTriggerAbsorption()` assigns `_absorbedMoveAttack` directly from `EnemyData.LearnableAttack` (no hardcoded path). The absorbed move's `DisplayName` field drives the submenu label.

### Input Lock and Sequence Safety

`_inputLocked` — set `true` when player attack prompts resolve (OnPlayerPromptCompleted, OnPlayerMagicSequenceCompleted), cleared when the next input-accepting state begins (ShowMenu, BeginEnemyAttack). All input is blocked during slash animations, retreat, and teardown.

`BattleSystem._sequenceActive` — set `true` on `StartSequence()`, set `false` on first `SequenceCompleted` emission. Guards `RunStep` and `OnAnyCircleCompleted` from firing after a sequence completes, preventing negative `_totalPromptsRemaining` and double `SequenceCompleted` signals.

`BeginEnemyAttack()` reentrancy guard — early-returns if `_state` is already `EnemyAttack`.

### Player Attack Prompt Cleanup

Any active player-attack `TimingPrompt` must be forcibly freed at the top of `BeginEnemyAttack()` via `FreeActivePrompt()` — do not rely on delayed flash-duration timers to clean up prompts before the enemy turn starts. The prompt may still be alive and emitting `PassEvaluated` signals when the enemy sequence begins, causing combo animation callbacks to fire on enemy circle results. `_isComboAttack` is also reset to `false` at the top of `BeginEnemyAttack()` as a secondary guard.

### SkipHopIn Flag and FloorY Constant

`[Export] public bool SkipHopIn = true` — when set, the enemy stays at origin for the entire turn. Setup and teardown tweens are skipped (no hop-in, no camera zoom). `_attackerClosePos` is set to the enemy origin so `PlayTeardown` is a zero-distance no-op. Used for large/stationary enemies like the 8 Sword Warrior. Set to `false` for the Warrior Phase 1 which hops in for melee attacks.

`const float FloorY = 750f` — world-space Y of the ground line. All character sprites are floor-anchored:
- Player: `Position.Y = FloorY - frameHeight * scale * 0.5f` (center-anchored sprite)
- Enemy: `Position.Y = FloorY - EnemyData.FrameHeight * 3f * 0.6f + EnemyData.SpriteOffsetY` (per-enemy tuned nudge for visual ground contact)
- Effects: `Position = (defenderCenter.X, FloorY) + step.Offset` — no hidden math; `step.Offset.Y < 0` moves up, `> 0` moves down. `sprite.Centered = true` is set explicitly in `SpawnEffectSprite` — the floor-baseline formula depends on this being true. `step.Scale` controls the sprite scale; default `Vector2(3, 3)` is the standard 3× world-space upscale used for all effect sheets.

`EnemyData.SpriteOffsetY` — per-enemy additional downward nudge on the enemy sprite, tuned visually. Warrior Phase 1 = 90f, 8 Sword Warrior = 130f.

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

- **Phase transition** — trigger Phase 2 when Phase 1 HP hits zero (explosion, sprite swap to 8 Sword Warrior); architecture supports it but the transition sequence is not yet implemented
- **Phase 2 boss setup** — new `EnemyData` + `EnemyAnimationConfig` for the 8 Sword Warrior; architecture already supports it
- **Audio gaps** — perfect parry shimmer replacement (more satisfying), ice sword impact sound, hop-in footstep SFX, two battle themes (Phase 1 + Phase 2), dedicated cast windup sound separate from magic_launch.wav
- **Battle menu UI polish** — layout, positioning, visual feedback
- **ATTACK_AUTHORING.md** — documentation for creating new AttackData/AttackStep resources
- **Reusable `AttackStep` resources** — refactor embedded sub-resources into standalone `.tres` files that can be referenced by multiple `AttackData` resources, avoiding duplication of shared values (frame dimensions, Fps, spritesheet path, etc.)
- **Bouncing circle color customisation** — color gradient (purple→white) and pass count are currently fixed per-type in `ApplyTypeSettings`; could be exposed as per-step inspector fields for more expressive attack authoring
- **Taunt ability (post-prototype)** — player action that baits the enemy into using their signature/learnable move
- **Self-targeting spell alignment** — Cure spell effect and target zone are not perfectly centered on the player's visual body due to the knight sprite having the character body left-of-center within its frame. Revisit when implementing the full character system — the correct fix is either adjusting the sprite frame composition or implementing a per-spell visual center offset.
