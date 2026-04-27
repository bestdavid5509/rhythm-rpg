using Godot;

/// <summary>
/// Per-slot HP/MP status panel — one instance per combatant on each side of
/// the field. Replaces the pre-Phase-6 singleton fields
/// (<c>_playerHPFill</c>, <c>_playerHPLabel</c>, <c>_playerMPFill</c>,
/// <c>_playerMPLabel</c>, <c>_enemyHPFill</c>, <c>_enemyHPLabel</c>,
/// <c>_enemyNameLabel</c>, <c>_playerPanel</c>) so multi-unit combat (4 player
/// slots + 5 enemy slots at <c>TestFullParty</c>) can render each combatant's
/// HP / MP / name independently.
///
/// Plain C# class (not a Godot <c>Node</c> or <c>Resource</c>): scene-tree
/// nodes are owned by the panel's <c>CanvasLayer</c>; this class just holds
/// references to them alongside the bound combatant. Mirrors the
/// <see cref="Combatant"/> ownership pattern.
///
/// <c>MpFill</c> / <c>MpLabel</c> are null on enemy panels (enemies have no MP
/// bar — see <c>BuildEnemyRow</c>). Player panels populate all fields.
///
/// <c>Panel</c> is the outer <c>PanelContainer</c> for player panels (each player
/// has its own panel). For enemy rows it is null — all enemies share a single
/// combined <c>PanelContainer</c> held in <c>BattleTest._enemyCombinedPanel</c>.
///
/// <c>ModulateTarget</c> is the Control whose <c>Modulate</c> is set by the
/// active-player highlight / dead-slot grayout helpers. For player panels it
/// equals <c>Panel</c> (modulating the outer container cascades to all its
/// children). For enemy rows it is the row's <c>HBoxContainer</c>, so the
/// alive/dead/active styling applies to that row only without affecting sibling
/// rows in the combined panel.
/// </summary>
public class PartyPanel
{
    public PanelContainer Panel;          // outer layered panel (player only; null on enemy rows)
    public Control        ModulateTarget; // Control whose Modulate carries dead/active styling
    public Label          NameLabel;      // combatant name; re-read from BoundCombatant.Name on each refresh
    public Control        HpFill;         // HP bar fill Control — Size.X tweens with HP %
    public Label          HpLabel;        // HP "current/max" overlay text
    public Control        MpFill;         // MP bar fill Control (player only; null on enemy)
    public Label          MpLabel;        // MP "current/max" overlay text (player only; null on enemy)
    public Combatant      BoundCombatant; // the combatant this panel renders for
}
