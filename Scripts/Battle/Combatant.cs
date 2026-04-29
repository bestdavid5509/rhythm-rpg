using Godot;

/// <summary>
/// Faction / side a combatant belongs to. Replaces binary `_isPlayerAttack` /
/// `attacker == _playerSprite` comparisons throughout the combat pipeline
/// once the Phase 3 refactor migrates consumers off singleton fields.
/// </summary>
public enum CombatantSide
{
    Player,
    Enemy,
}

/// <summary>
/// A single combatant in the battle system — one per unit, regardless of side.
///
/// Plain C# class (not a Godot <c>Node</c> or <c>Resource</c>): lifecycle is
/// independent of the scene tree, references to scene nodes point at existing
/// nodes rather than owning them, and the class is safe to pass around as a
/// shared reference.
///
/// Signal-marshalling note: <c>Combatant</c> cannot be passed directly through
/// Godot <c>[Signal]</c> payloads (plain C# classes aren't Variant-compatible —
/// the source generator emits GD0202). The marshalling vehicle is
/// <see cref="SequenceContext"/>, a <c>RefCounted</c>-derived wrapper that
/// holds <c>Combatant</c> references as fields. Plain-C#-class refs nested
/// inside a RefCounted payload preserve identity through Variant marshalling;
/// verified in Phase 3.0 of the target-selection refactor.
///
/// See <c>docs/combatant-abstraction-design.md</c> (Q1, Q8) for the full rationale.
/// </summary>
public class Combatant
{
    public string        Name;
    public CombatantSide Side;

    // ---- Combat-universal state (both sides use these) ----
    public int              CurrentHp;
    public int              MaxHp;
    public int              Agility = 10; // Per-tick AP gain for the C7-prerequisite tick-based scheduler (TurnOrderQueue). Higher Agility = more turns. Test values are assigned per slot in BuildPlayerCombatantForSlot / BuildEnemyCombatantForSlot; this default (10) is a fallback for any future code path that constructs a Combatant outside the slot-builder. Tie-break at simultaneous threshold-crossings: players-before-enemies, then party-list index.
    public bool             IsDead;
    public Vector2          Origin;        // world-space origin for positioning (ColorRect-based)
    public Vector2          AnimSpriteOrigin;  // AnimatedSprite2D position snapshot at scene-init time, after floor-anchor + per-slot offset. Distinct from Origin (different formulas per side). Read by PlayHopIn / PlayTeardown for the AnimSprite tween's destination so each slot retreats to its own origin instead of slot 0's.
    public ColorRect        PositionRect;  // existing anchor node (formerly _playerSprite / _enemySprite)
    public AnimatedSprite2D AnimSprite;    // existing animated sprite node (formerly _playerAnimSprite / _enemyAnimSprite)

    // C7-extra feet-anchor metadata for hop-in feet-to-feet alignment. Cached at slot
    // construction (BuildPlayerCombatantForSlot / BuildEnemyCombatantForSlot) — never
    // mutated during gameplay. FeetAnchorY is the Y-pixel offset from the TOP of the
    // sprite frame to the character's ground line (where their feet meet the floor);
    // the centering math (Centered=true puts AnimSpriteOrigin at sprite center) is done
    // at use time via `(FeetAnchorY - FrameHeight/2) * AnimSpriteScale.Y`. AnimSpriteScale
    // is uniform (3, 3) at Phase 6 scope but cached here so future per-character scaling
    // doesn't require touching the close-stance helpers.
    public float            FeetAnchorY;
    public float            FrameHeight;
    public Vector2          AnimSpriteScale;

    // ---- Player-only — null/unused/default on enemies ----
    public int       CurrentMp;
    public int       MaxMp;
    public bool      IsDefending;
    public Combatant BeckoningTarget;  // Target enemy this combatant has beckoned — consumed on the next SelectEnemyAttack call for that enemy. Null = no active beckon. Target defaults to _enemyParty[0] until C10 wires proper target selection.
    public bool      IsAbsorber;       // True for the single Absorber on the player side; gates absorbed-move learning and Beckon menu visibility in the Skills submenu.

    // ---- Enemy-only — null/unused/default on players ----
    public EnemyData Data;

    // Tracks absorption of this enemy's single Data.LearnableAttack — consistent with the
    // "one learnable per enemy" assumption in EnemyData.LearnableAttack. If future design
    // calls for multiple learnable moves per enemy, this field changes to
    // HashSet<AttackData> AbsorbedMoves or similar.
    public bool HasBeenAbsorbed;

    public ShaderMaterial FlashMaterial;

    // Currently-running flash tween (e.g., the white-flash on learnable-move selection).
    // Per-enemy because simultaneous enemies in multi-combat might each run their own
    // flash concurrently. Lifetime is short (~0.6s); safe to hold as a reference until
    // the tween self-destructs.
    public Tween FlashTween;

    // Mirror of FlashTween — independent handle for the Phase 5 red-tint threat
    // pulse. Tweens tint_amount on FlashMaterial; the two effects (white flash +
    // red tint) coexist via separate uniforms on CombatantOverlay.gdshader, so
    // both tween handles must be independent to avoid stomping each other when
    // white-flash and threat-reveal fire in the same turn (e.g., enemy uses a
    // learnable move — white flash on enemy + red tint on player).
    public Tween ThreatTween;

    // ---- Damage / heal application --------------------------------------------
    //
    // Friendly-fire-readiness note: TakeDamage and Heal are attacker-agnostic —
    // they operate on the receiver only. Dispatch sites that need to distinguish
    // ally-target from enemy-target scenarios should check
    // `attacker.Side == target.Side` rather than an attack-specific flag; that
    // predicate generalizes to friendly-fire / ally-heal / self-damage / self-heal
    // by construction. Geometric positioning (FlipH, PlayerOffset) is already
    // handled attacker-agnostically by BattleSystem.SpawnEffectSprite's
    // attackerOnRight check (Phase 3.5). No current ability needs same-side
    // targeting; this design simply removes the prior limitation.

    /// <summary>
    /// Applies damage to this combatant's HP, clamping to [0, MaxHp]. Target-agnostic
    /// with respect to attacker — works for any attacker-target pair including same-side
    /// (ally attacks ally, self-damage). Caller is responsible for damage calculation
    /// (defend modifiers, crit, etc.) before passing the final value here.
    /// </summary>
    public void TakeDamage(int amount)
    {
        CurrentHp = Mathf.Max(0, CurrentHp - amount);
    }

    /// <summary>
    /// Heals this combatant's HP, clamping to MaxHp. Works for self-heal
    /// (attacker == target) and ally-heal (attacker != target, same side). Caller
    /// is responsible for heal amount calculation before passing the final value here.
    /// </summary>
    public void Heal(int amount)
    {
        CurrentHp = Mathf.Min(MaxHp, CurrentHp + amount);
    }
}
