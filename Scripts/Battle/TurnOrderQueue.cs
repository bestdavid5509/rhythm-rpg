using System.Collections.Generic;

/// <summary>
/// Tick-based AP scheduler for the Phase 6 multi-character turn loop. Replaces
/// the round-based stable-sort queue from C5.
///
/// **Model.** Each combatant carries an <c>Agility</c> stat (per
/// <see cref="Combatant.Agility"/>). At every simulated tick, alive combatants
/// accumulate <c>Agility</c> AP. When a combatant's AP crosses
/// <see cref="Threshold"/> (= 100), they take a turn; their AP is decremented by
/// <c>Threshold</c> (excess carries over for proportional fairness across the
/// long run, FF10/Bravely Default style).
///
/// <see cref="Advance"/> simulates ticks until someone crosses, returns that
/// combatant. <see cref="Lookahead"/> snapshot-simulates without committing,
/// projecting the next N actors for UI consumers (e.g. the C7 turn-order
/// strip's vertical lookahead column).
///
/// **Tie-break** when multiple combatants cross threshold the same tick: side
/// first (Player &lt; Enemy), then party-list index. Mirrors C5's stable-sort
/// tertiary keys so equal-agility behavior is bit-identical to the prior
/// queue (P1 before E1 at agility = 10 vs 10 etc.).
///
/// **AP state** lives in this class via <c>Dictionary&lt;Combatant, float&gt;</c>;
/// not on Combatant. Combatant stays focused on combat state; the queue is
/// the single owner of scheduling. Dead combatants don't tick (skipped in
/// the per-tick accumulation loop), so death doesn't require explicit
/// invalidation; their stale AP value is harmless.
///
/// Plain C# class — not a Godot Node or Resource. Owned by
/// <see cref="BattleTest"/> via the <c>_queue</c> field; not parented to the
/// scene tree.
/// </summary>
public sealed class TurnOrderQueue
{
    /// <summary>
    /// AP threshold a combatant must cross to take a turn. Threshold of 100
    /// makes Agility values map intuitively to "AP gained per tick" — Agility
    /// 10 takes 10 ticks per turn at no-carryover; Agility 12 takes 8.33...
    /// Float AP (rather than int) avoids integer-divisibility weirdness.
    /// </summary>
    private const int Threshold = 100;

    private readonly Dictionary<Combatant, float> _ap          = new();
    private readonly Dictionary<Combatant, int>   _partyIndex  = new();  // for tie-break
    private readonly List<Combatant>              _all         = new();  // captured at Reset
    private Combatant                             _current;

    /// <summary>
    /// The combatant most recently returned by <see cref="Advance"/>; null
    /// before the first Advance after <see cref="Reset"/>. Diagnostic /
    /// strip-consumer accessor.
    /// </summary>
    public Combatant Current => _current;

    /// <summary>
    /// Captures the combatant references and zeros all AP. Called from
    /// <see cref="BattleTest._Ready"/> on first scene init and from
    /// <c>SwapToPhase2</c> after the Phase 2 boss is revived (slot-0 enemy
    /// returns to alive state with reset AP).
    /// </summary>
    public void Reset(IReadOnlyList<Combatant> playerParty,
                       IReadOnlyList<Combatant> enemyParty)
    {
        _ap.Clear();
        _partyIndex.Clear();
        _all.Clear();
        _current = null;

        for (int i = 0; i < playerParty.Count; i++)
        {
            var c = playerParty[i];
            _all.Add(c);
            _ap[c] = 0f;
            _partyIndex[c] = i;
        }
        for (int i = 0; i < enemyParty.Count; i++)
        {
            var c = enemyParty[i];
            _all.Add(c);
            _ap[c] = 0f;
            _partyIndex[c] = i;
        }
    }

    /// <summary>
    /// Simulates ticks against the live AP state until a combatant crosses
    /// <see cref="Threshold"/>. Returns that combatant (now <see cref="Current"/>).
    /// Returns <c>null</c> only if all combatants are dead (defensive — callers
    /// should have run <c>CheckGameOver</c> first).
    /// </summary>
    public Combatant Advance()
    {
        var picked = SimulateOne(_ap);
        _current = picked;
        return picked;
    }

    /// <summary>
    /// Projects the next <paramref name="n"/> turns without mutating live AP
    /// state. Snapshot-and-simulate: deep-copies the AP dictionary, runs
    /// <c>SimulateOne</c> N times against the snapshot, returns the collected
    /// list. Cost is O(N × ticks-to-threshold) — bounded and small (worst
    /// case ≈ N × 15 simulation steps at the test agility values).
    /// </summary>
    public IReadOnlyList<Combatant> Lookahead(int n)
    {
        if (n <= 0) return System.Array.Empty<Combatant>();

        var snapshot = new Dictionary<Combatant, float>(_ap);
        var result   = new List<Combatant>(n);

        for (int i = 0; i < n; i++)
        {
            var picked = SimulateOne(snapshot);
            if (picked == null) break;  // all dead — defensive backstop
            result.Add(picked);
        }
        return result;
    }

    /// <summary>
    /// One step of the scheduling loop against the supplied AP dictionary
    /// (live for <c>Advance</c>, snapshot for <c>Lookahead</c>).
    ///
    /// **Loop ordering: check-first, tick-second.** This preserves
    /// proportional fairness for combatants with carryover AP at-or-above
    /// threshold from the prior <c>Advance</c>: they act on the next call
    /// without an extra free tick of AP. (Reverse ordering — tick first,
    /// check second — would give simultaneous-crosser runners-up an extra
    /// Agility worth of AP every time they wait.) Carryover semantics flow
    /// from this ordering plus the decrement-by-Threshold behavior.
    ///
    /// Per-tick: every alive combatant's AP increases by their Agility.
    /// Dead combatants are skipped — they don't tick and they don't cross.
    /// </summary>
    private Combatant SimulateOne(Dictionary<Combatant, float> ap)
    {
        // Defensive cap — if all alive combatants somehow have Agility = 0,
        // the loop would never exit. Bound the simulation to a sane upper
        // limit (effectively unreachable at any reasonable agility).
        const int MaxTicks = 10_000;
        for (int tick = 0; tick < MaxTicks; tick++)
        {
            // Check first: pick any combatant whose AP already crossed
            // threshold (carryover from prior Advance, or accumulated this
            // call). Tie-break sides-then-party-index.
            Combatant picked = null;
            foreach (var c in _all)
            {
                if (c.IsDead || ap[c] < Threshold) continue;
                if (picked == null || PreferredOver(c, picked))
                    picked = c;
            }
            if (picked != null)
            {
                ap[picked] -= Threshold;
                return picked;
            }

            // Tick: every alive combatant accumulates Agility AP.
            bool anyAlive = false;
            foreach (var c in _all)
            {
                if (c.IsDead) continue;
                ap[c] += c.Agility;
                anyAlive = true;
            }
            if (!anyAlive) return null;  // all dead — defensive backstop
        }
        return null;  // unreachable in practice
    }

    /// <summary>
    /// True if combatant <paramref name="a"/> wins the tie-break against
    /// <paramref name="b"/>. Players before enemies; within a side,
    /// lower party-list index wins.
    /// </summary>
    private bool PreferredOver(Combatant a, Combatant b)
    {
        int sideA = a.Side == CombatantSide.Player ? 0 : 1;
        int sideB = b.Side == CombatantSide.Player ? 0 : 1;
        if (sideA != sideB) return sideA < sideB;
        return _partyIndex[a] < _partyIndex[b];
    }
}
