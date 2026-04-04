using Godot;
using Godot.Collections;

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
    /// Ordered sequence of steps that make up this attack.
    ///
    /// NOTE — Bouncing steps: A Bouncing circle runs multiple inward passes.
    /// Future work — the animation for a Bouncing step will need to replay once per
    /// pass so the visual stays in sync with each approach. Not yet implemented;
    /// see the matching note in AttackStep.cs for the hook point.
    /// </summary>
    [Export] public Array<AttackStep> Steps = new();

    /// <summary>
    /// Damage applied per successful input (Hit or Perfect) across all steps.
    /// </summary>
    [Export] public int BaseDamage = 10;
}
