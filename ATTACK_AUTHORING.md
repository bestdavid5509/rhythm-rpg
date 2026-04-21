# ATTACK_AUTHORING.md

## Overview

An attack in this system is an `AttackData` resource containing one or more `AttackStep` objects. Each step drives one animation play and one or more timing circles. Steps are sequenced or overlapped via `StartOffsetMs`. The system handles all timing math automatically from the values you provide.

## AttackData Fields

| Field | Type | Default | Notes |
|---|---|---|---|
| `DisplayName` | string | `""` | Shown in menus and absorbed-move submenu |
| `Category` | enum | `Magic` | `Physical` (0) = hop-in path; `Magic` (1) = cast path |
| `IsHopIn` | bool | `false` | Attacker hops in close; camera zooms; melee animation plays |
| `Steps` | `AttackStep[]` | empty | Ordered sequence — see below |
| `BaseDamage` | int | `10` | Damage per successful input across all steps |
| `MpCost` | int | `0` | Player-only; enemies don't spend MP |

## AttackStep Fields

### Spritesheet

| Field | Default | Notes |
|---|---|---|
| `SpritesheetPath` | `""` | `res://` path to effect sheet. Empty = no effect sprite (common for melee steps) |
| `FrameWidth` / `FrameHeight` | `64` / `64` | Pixel dimensions of one frame |
| `Fps` | `12` | Playback speed |
| `Scale` | `(3, 3)` | Standard world-space upscale. Override per sheet as needed |
| `FlipH` | `false` | Mirror the effect sprite horizontally |

### Timing Circles

| Field | Default | Notes |
|---|---|---|
| `ImpactFrames` | `[0]` | Zero-based frame indices where hits land. One entry = one circle. `ImpactFrames[0]` anchors the animation start |
| `CircleType` | `Standard` (0) | Standard=0, Slow=1, Bouncing=2. All circles in a step share the type |
| `BounceCount` | `2` | Bouncing only. Additional inward passes after the first. Total inputs = `BounceCount + 1` |

### Positioning

| Field | Default | Notes |
|---|---|---|
| `Offset` | `(0, 0)` | World-space offset when enemy is attacker (effect lands on player). Y < 0 = up |
| `PlayerOffset` | `(0, 0)` | World-space offset when player is attacker (effect lands on enemy) |

Effect sprites always spawn at `(defenderCenter.X, FloorY=750) + Offset/PlayerOffset`. `Centered=true` is implicit — the floor-baseline math depends on this.

### Step Sequencing

| Field | Default | Notes |
|---|---|---|
| `StartOffsetMs` | `0` | Relative to previous step's last-circle resolve time. `>0` = pause; `0` = immediate; `<0` = overlap/concurrent. Ignored on step 0 |
| `PostAnimationDelayMs` | `0` | Hold after animation finishes before retreat (hop-in) or effect cleanup |

### Damage Override

| Field | Default | Notes |
|---|---|---|
| `BaseDamageOverride` | `0` | Overrides `AttackData.BaseDamage` for this step only. `0` = use parent value |

### Hop-In Enemy Animation (enemy attacks only)

| Field | Default | Notes |
|---|---|---|
| `EnemyAnimation` | `""` | Enemy sprite animation to play at step start (e.g. `"melee_attack"`, `"light_attack"`). Only used when `IsHopIn=true` |
| `WaitAnimation` | `""` | Frozen at frame 0 during the gap before this step's `EnemyAnimation` plays. Empty = idle. Only applies on hop-in steps when delay > 0 |

### Audio

| Field | Default | Notes |
|---|---|---|
| `SoundEffects` | `[]` | `res://Assets/Audio/SFX/` paths to sounds |
| `SoundTriggerFrames` | `[]` | Zero-based frame indices, paired 1:1 with `SoundEffects`. On Bouncing steps, sounds replay each inward pass |

## Timing Math

The system automatically syncs the animation so `ImpactFrames[0]` plays exactly when circle 0 closes:

```
circleCloseDuration = DefaultDurationForType(CircleType)
                      Standard = 1.0s, Slow = 2.0s, Bouncing = 1.0s

animStartDelay      = circleCloseDuration - (ImpactFrames[0] / Fps)
                      clamped to ≥ 0

animStartFrame      = if rawDelay < 0: round(|rawDelay| × Fps)
                      else: 0
```

If `rawDelay < 0`: the impact frame takes longer to reach than the circle close duration. The animation starts immediately and skips ahead to `animStartFrame`. A warning is logged. Fix by raising `Fps` or lowering `ImpactFrames[0]`.

Multi-circle stagger: circles 1+ spawn at `(ImpactFrames[i] - ImpactFrames[0]) / Fps` after circle 0.

Step scheduling is timer-driven, not completion-driven. The next step's timer is scheduled the moment the current step starts running.

## Step Sequencing Reference

| StartOffsetMs | Effect |
|---|---|
| `500` | 500ms pause after previous step's last circle resolves |
| `0` | Starts immediately when previous step's last circle resolves |
| `-500` | Starts 500ms before previous step's last circle resolves — steps overlap |

Negative values create concurrent animations and circles. Clamped to 0 if overlap would precede sequence start. Ignored on step 0.

## Hop-In Attack Rules

When `IsHopIn = true` and `Category = Physical`:

- `EnemyAnimation` must be set on each step or no enemy animation plays for that step
- `StartOffsetMs` between steps must be large enough for the previous animation to complete, or set `WaitAnimation` on the next step to hold a pose during the gap
- Minimum gap formula: `StartOffsetMs ≥ (frameCount / Fps) * 1000`
  - `melee_attack` (16 frames at 12fps) → minimum 1333ms
  - `light_attack` (11 frames at 12fps) → minimum 917ms
- `WaitAnimation` on a step freezes on frame 0 of that animation during the gap. Use this when a short `StartOffsetMs` is intentional and you want a wind-up pose rather than idle
- `PostAnimationDelayMs` on the last step only controls the hold before retreat. Earlier steps should have it set to 0
- Player Physical attacks cancel on first miss. Enemy Physical attacks always play their full sequence

## Common Patterns

### Single hit (enemy cast)

```
Steps[0]: SpritesheetPath="...", ImpactFrames=[6], CircleType=Standard
```

### Multi-hit in one animation

```
Steps[0]: ImpactFrames=[6, 7, 8], CircleType=Standard
```

One animation play, three staggered circles.

### Two separate animations, gapped

```
Steps[0]: ImpactFrames=[6], StartOffsetMs=0
Steps[1]: ImpactFrames=[6], StartOffsetMs=500  ← 500ms pause between
```

### Two separate animations, overlapping

```
Steps[0]: ImpactFrames=[6,7,8]
Steps[1]: ImpactFrames=[6], StartOffsetMs=-200  ← starts before step 0's last circle
```

### Hop-in melee combo (enemy)

```
AttackData: Category=Physical, IsHopIn=true
Steps[0]: EnemyAnimation="melee_attack", StartOffsetMs=0
Steps[1]: EnemyAnimation="light_attack", WaitAnimation="light_attack", StartOffsetMs=1400
Steps[2]: EnemyAnimation="melee_attack", WaitAnimation="melee_attack", StartOffsetMs=1400, PostAnimationDelayMs=200
```

### Bouncing circle

```
Steps[0]: CircleType=Bouncing, BounceCount=4  ← 5 total inputs
          EnemyAnimation="melee_attack" (replays each bounce automatically)
```

### Self-targeting player spell

```
AttackData: Category=Magic
Steps[0]: PlayerOffset=(-15, -70), BaseDamage=30 (used as heal magnitude)
```

### Visual-only effect (no circles, not routed through BattleSystem)

```
AttackData: BaseDamage=0, MpCost=0
Steps[0]: SpritesheetPath="...", ImpactFrames=[6]  ← used as timing data by BattleTest directly
```

See `player_ether_item_use.tres` and `BattleTest.SpawnEtherEffect`.

## Effect Sprite Positioning Tips

- `Offset.Y = -300` roughly puts an effect at chest height on the player
- `Scale = (3, 3)` is standard. Larger sheets (hammer) use `(4, 4)`. Smaller UI effects use `(2, 2)`
- For absorbed moves that work both as enemy and player attacks, set both `Offset` and `PlayerOffset`
- Check `effect_manifest.md` in `Resources/Attacks/` for per-sheet frame dimensions, counts, and recommended impact frames

## CircleType Quick Reference

| Type | Value | Duration | Use when |
|---|---|---|---|
| Standard | `0` | 1.0s | Default — fast, responsive |
| Slow | `1` | 2.0s | Heavy hits, telegraphed attacks, dramatic moments |
| Bouncing | `2` | 1.0s per pass | Multi-hit sustained attacks, escalating sequences |

## Checklist for New Attacks

- [ ] `DisplayName` set (required for absorbed moves)
- [ ] `Category` correct — Physical for hop-in melee, Magic for cast/projectile
- [ ] `IsHopIn` set if enemy hops in
- [ ] `FrameWidth`/`FrameHeight` match the actual spritesheet (check `effect_manifest.md`)
- [ ] `ImpactFrames` count matches intended circle count
- [ ] `StartOffsetMs` on hop-in steps ≥ minimum gap formula, or `WaitAnimation` set
- [ ] `EnemyAnimation` set on all hop-in steps
- [ ] `PostAnimationDelayMs` set on last hop-in step only
- [ ] `PlayerOffset` set if attack can be used by player (absorbed move)
- [ ] `SoundEffects` and `SoundTriggerFrames` arrays same length
