using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Round-order queue for the Phase 6 multi-character turn loop.
///
/// Holds the ordered list of combatants for one "round" — a complete pass
/// through every combatant alive at <see cref="Rebuild"/> time. Order is
/// computed by stable sort: descending Agility, players-before-enemies,
/// then party-list index. At equal agility (the Phase 6 default — every
/// combatant has Agility 10) this emits players first, then enemies, in
/// party-list order.
///
/// Public surface mirrors the iterator pattern: <see cref="Rebuild"/>
/// resets the round, <see cref="Advance"/> moves the cursor to the next
/// alive combatant (skipping <c>IsDead</c>), <see cref="Current"/> peeks
/// the active combatant. <see cref="Advance"/> returns false when no live
/// combatant remains; the caller (BattleTest.AdvanceTurn) then calls
/// <see cref="Rebuild"/> for a fresh round.
///
/// Plain C# class — not a Godot Node or Resource. Owned by
/// <see cref="BattleTest"/> via the <c>_queue</c> field; not parented to
/// the scene tree.
/// </summary>
public sealed class TurnOrderQueue
{
    private readonly List<Combatant> _order  = new();
    private int                      _cursor = -1;

    /// <summary>Diagnostic accessor — the round's full order in turn sequence.</summary>
    public IReadOnlyList<Combatant> Order => _order;

    /// <summary>
    /// The combatant whose turn is now active, or null before the first
    /// <see cref="Advance"/> after <see cref="Rebuild"/>.
    /// </summary>
    public Combatant Current =>
        (_cursor >= 0 && _cursor < _order.Count) ? _order[_cursor] : null;

    /// <summary>
    /// Recomputes the round order from the supplied parties and resets the
    /// cursor to -1 (so the first <see cref="Advance"/> moves to index 0).
    /// Wipes any prior round.
    /// </summary>
    public void Rebuild(IReadOnlyList<Combatant> playerParty,
                         IReadOnlyList<Combatant> enemyParty)
    {
        // Capture each combatant's party-list index so the tertiary sort key is
        // stable across the merged list.
        var partyIndex = new Dictionary<Combatant, int>(playerParty.Count + enemyParty.Count);
        for (int i = 0; i < playerParty.Count; i++) partyIndex[playerParty[i]] = i;
        for (int i = 0; i < enemyParty.Count;  i++) partyIndex[enemyParty[i]]  = i;

        _order.Clear();
        _order.AddRange(playerParty.Concat(enemyParty)
            .OrderByDescending(c => c.Agility)
            .ThenBy(c => c.Side == CombatantSide.Player ? 0 : 1)
            .ThenBy(c => partyIndex[c]));
        _cursor = -1;
    }

    /// <summary>
    /// Advances the cursor to the next alive combatant. Skips
    /// <see cref="Combatant.IsDead"/> entries silently. Returns true if a
    /// live combatant is now <see cref="Current"/>; returns false when the
    /// round has no remaining live combatants (caller's cue to
    /// <see cref="Rebuild"/> or trigger end-of-battle).
    /// </summary>
    public bool Advance()
    {
        for (int i = _cursor + 1; i < _order.Count; i++)
        {
            if (!_order[i].IsDead)
            {
                _cursor = i;
                return true;
            }
        }
        _cursor = _order.Count;  // exhausted
        return false;
    }
}
