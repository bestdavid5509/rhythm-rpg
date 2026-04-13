using Godot;

/// <summary>
/// Selection strategy for choosing attacks from an enemy's pool.
/// </summary>
public enum AttackSelectionStrategy
{
    /// <summary>Pick a random attack each turn.</summary>
    Random,
    /// <summary>Cycle through attacks in order (not yet implemented).</summary>
    Sequential,
    /// <summary>Pick randomly with per-attack weights (not yet implemented).</summary>
    Weighted,
}

/// <summary>
/// Defines an enemy combatant — name, stats, and the pool of attacks it can use.
/// Assigned to BattleTest via the inspector to configure which enemy the player fights.
/// </summary>
[GlobalClass]
public partial class EnemyData : Resource
{
    /// <summary>Display name shown in UI and debug logs.</summary>
    [Export] public string EnemyName = "";

    /// <summary>Maximum hit points for this enemy.</summary>
    [Export] public int MaxHp = 100;

    /// <summary>res:// path to the enemy's spritesheet PNG.</summary>
    [Export] public string SpritesheetPath = "";

    /// <summary>Width of a single frame in the spritesheet, in pixels.</summary>
    [Export] public int FrameWidth = 160;

    /// <summary>Height of a single frame in the spritesheet, in pixels.</summary>
    [Export] public int FrameHeight = 160;

    /// <summary>
    /// Y offset applied on top of the floor-anchor formula to fine-tune the enemy's
    /// visual ground contact. Positive values move the sprite down.
    /// </summary>
    [Export] public float SpriteOffsetY = 130f;

    /// <summary>Animation layout configuration for this enemy's spritesheet.</summary>
    [Export] public EnemyAnimationConfig AnimationConfig;

    /// <summary>
    /// All attacks this enemy can use. AttackSelector picks from this pool each turn
    /// based on SelectionStrategy.
    /// </summary>
    [Export] public AttackData[] AttackPool = { };

    /// <summary>
    /// How attacks are chosen from AttackPool each turn.
    /// Currently only Random is implemented; Sequential and Weighted fall back to Random.
    /// </summary>
    [Export] public AttackSelectionStrategy SelectionStrategy = AttackSelectionStrategy.Random;

    /// <summary>
    /// The attack the player can absorb from this enemy via perfect parry.
    /// When this attack is selected, a narrative signal is shown to the player.
    /// Null means no learnable attack.
    /// </summary>
    [Export] public AttackData LearnableAttack;
}
