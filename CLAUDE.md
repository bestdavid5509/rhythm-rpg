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

_Decisions will be documented here as they are made._
