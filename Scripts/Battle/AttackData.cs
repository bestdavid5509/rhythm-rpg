using Godot;
using Godot.Collections;

/// <summary>
/// Broad category of an attack — used for elemental matchups, visual theming,
/// and determining which animation path BattleTest uses (hop-in vs cast).
/// </summary>
public enum AttackCategory
{
    /// <summary>Close-range strike — pairs with the hop-in animation path.</summary>
    Physical,
    /// <summary>Ranged or elemental cast — pairs with the cast_intro/loop/end path.</summary>
    Magic,
}

/// <summary>
/// A complete attack sequence — an ordered list of AttackStep objects that play out
/// one after another, each paired with a timing circle prompt.
///
/// Steps execute in order, but may overlap: the StartOffsetMs field on each step
/// controls when it starts relative to the previous step's last circle resolving.
/// Positive = gap, zero = immediate, negative = concurrent/overlapping.
///
/// Damage model:
///   BaseDamage is applied once per successful input across all steps.
///   Total damage scales with how many inputs the player lands — including all
///   passes of any Bouncing steps, whose pass count is controlled by the circle itself.
/// </summary>
[GlobalClass]
public partial class AttackData : Resource
{
    /// <summary>
    /// Broad category of the attack — Physical or Magic.
    /// Used for elemental matchups, visual theming, and future damage calculation.
    /// Default Magic covers all ranged/cast attacks; set to Physical for melee hop-ins.
    /// </summary>
    [Export] public AttackCategory Category = AttackCategory.Magic;

    /// <summary>
    /// When true, the attacker hops in close before the sequence starts and the camera
    /// zooms in. BattleTest plays PlayHopIn, triggers the attacker's melee animation,
    /// then calls StartSequence. On completion, PlayTeardown retreats the attacker.
    ///
    /// When false, the enemy stays at origin and uses the cast_intro/loop/end path.
    /// </summary>
    [Export] public bool IsHopIn = false;

    /// <summary>
    /// Ordered sequence of steps that make up this attack.
    /// Bouncing steps replay their animation once per pass — BattleSystem subscribes
    /// to PassEvaluated on circle 0 and schedules a replay after each outward pass.
    /// </summary>
    [Export] public Array<AttackStep> Steps = new();

    /// <summary>
    /// Damage applied per successful input (Hit or Perfect) across all steps.
    /// </summary>
    [Export] public int BaseDamage = 10;

    /// <summary>
    /// MP cost to use this attack. Physical attacks default to 0 (free).
    /// Magic attacks set an explicit cost. Only checked for player-side attacks;
    /// enemies do not spend MP.
    /// </summary>
    [Export] public int MpCost = 0;
}
