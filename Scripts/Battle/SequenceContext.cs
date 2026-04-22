using Godot;

/// <summary>
/// Signal payload wrapper carrying per-sequence context through BattleSystem signals.
///
/// <para>
/// <c>RefCounted</c>-derived so it marshals through Godot's Variant system — plain
/// C# class references (like <see cref="Combatant"/>) cannot be Variant-converted
/// and trigger <c>GD0202</c> at compile time when used directly in <c>[Signal]</c>
/// delegate parameters. The RefCounted wrapper solves this: it marshals through
/// Variant, and its fields (plain-C#-class refs) preserve reference identity on
/// the subscriber side. Verified in Phase 3.0 of the target-selection refactor.
/// </para>
///
/// <para>
/// Lifetime: one instance per sequence. BattleSystem creates it at
/// <c>StartSequence</c> entry, holds it for the sequence's duration, and emits it
/// unchanged through every signal during that sequence. Subscribers can use
/// reference equality to identify which sequence a signal belongs to.
/// </para>
///
/// <para>
/// Currently unused. Created as scaffolding in Phase 3.1; wired into BattleSystem
/// signals during Phase 3.5 (D-category refactor) / Phase 3.9 (J-category).
/// See <c>docs/combatant-abstraction-design.md</c> Q8 for the full design.
/// </para>
/// </summary>
public partial class SequenceContext : RefCounted
{
    public Combatant  Attacker      { get; init; }
    public Combatant  Target        { get; init; }
    public AttackData CurrentAttack { get; init; }
    public int        SequenceId    { get; init; }
}
