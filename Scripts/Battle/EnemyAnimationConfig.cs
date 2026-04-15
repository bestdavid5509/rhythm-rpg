using Godot;

/// <summary>
/// Data-driven animation layout for an enemy spritesheet.
/// Each field maps a named animation to a row, start column, and frame count
/// in the enemy's sprite grid. Assigned to EnemyData via the inspector.
/// </summary>
[GlobalClass]
public partial class EnemyAnimationConfig : Resource
{
    // ── Idle ──────────────────────────────────────────────────────────
    [Export] public int IdleRow;
    [Export] public int IdleFrames;

    // ── Run ──────────────────────────────────────────────────────────
    [Export] public int RunRow;
    [Export] public int RunFrames;

    // ── Cast intro (wind-up, plays once before prompt) ───────────────
    [Export] public int CastIntroRow;
    [Export] public int CastIntroFrames;

    // ── Cast loop (holds during prompt sequence) ─────────────────────
    [Export] public int CastLoopRow;
    [Export] public int CastLoopStartCol;
    [Export] public int CastLoopFrames = 1;

    // ── Cast end (release after prompt — optional) ───────────────────
    [Export] public bool HasCastEnd;
    [Export] public int CastEndRow;
    [Export] public int CastEndStartCol;
    [Export] public int CastEndFrames;

    // ── Melee attack (hop-in strike) ─────────────────────────────────
    [Export] public int MeleeAttackRow;
    [Export] public int MeleeAttackFrames;
    [Export] public int MeleeImpactFrame;

    // ── Light attack (alternate hop-in strike; optional) ─────────────
    /// <summary>Row for the light_attack animation. Only registered when LightAttackFrames > 0.</summary>
    [Export] public int LightAttackRow;
    [Export] public int LightAttackFrames;
    [Export] public int LightAttackStartCol;

    // ── Hurt (damage reaction) ───────────────────────────────────────
    /// <summary>Row on the main spritesheet for the hurt animation. Used when HurtSheetPath is empty.</summary>
    [Export] public int HurtRow;
    /// <summary>Frame count for the hurt animation on the main sheet.</summary>
    [Export] public int HurtFrames;

    /// <summary>
    /// Optional separate spritesheet for hurt animations (e.g. 8 Sword Warrior).
    /// When non-empty, hurt_flash and hurt_full are loaded from this sheet instead of the main sheet.
    /// </summary>
    [Export] public string HurtSheetPath = "";
    /// <summary>Frame count for hurt_full on the separate hurt sheet. Ignored when HurtSheetPath is empty.</summary>
    [Export] public int HurtFullFrames;

    // ── Death ────────────────────────────────────────────────────────
    [Export] public int DeathRow;
    [Export] public int DeathFrames;
}
