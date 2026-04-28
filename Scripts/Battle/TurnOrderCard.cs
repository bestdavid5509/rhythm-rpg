using Godot;

/// <summary>
/// One card in the C7 vertical turn-order strip — a small Kenney-chrome panel
/// at the top-left of the screen showing a single combatant's name and side
/// colour. Cards persist across <c>RefreshTurnOrderStrip(animate: true)</c>
/// calls (instance-bound, not position-bound) so the slide animation moves
/// real card identities between slots rather than rebuilding from scratch.
///
/// On every <see cref="TurnOrderQueue.Advance"/>: the top card slides off
/// the right and is freed, cards 1..N-1 shift up by one slot via geometric
/// tween, and a new card spawns from below at slot N-1.
///
/// On <see cref="TurnOrderQueue.Reset"/> (Phase 2 transition, scene init):
/// hard-rebind — wipe all cards and rebuild from the new Lookahead. No
/// animation; Reset is a "fresh start," not a "turn resolved."
///
/// Plain C# class (not a Godot <c>Node</c> or <c>Resource</c>): scene-tree
/// nodes are owned by the strip's <c>CanvasLayer</c>; this class just holds
/// references alongside the bound combatant. Mirrors the
/// <see cref="PartyPanel"/> / <see cref="Combatant"/> ownership pattern.
/// </summary>
public class TurnOrderCard
{
    public PanelContainer Panel;          // outer mini-card; child of strip CanvasLayer
    public Label          NameLabel;      // combatant name; set at construction
    public Combatant      BoundCombatant; // identity for diagnostic + tint lookup
}
