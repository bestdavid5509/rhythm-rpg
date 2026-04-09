using Godot;

/// <summary>
/// Picks an attack from an enemy's attack pool based on the configured selection strategy.
/// Stateless — the caller owns the lastIndex ref for Sequential tracking.
/// </summary>
public static class AttackSelector
{
    /// <summary>
    /// Returns an AttackData from <paramref name="enemy"/>'s AttackPool, or null if the
    /// pool is empty or <paramref name="enemy"/> is null.
    ///
    /// <paramref name="lastIndex"/> tracks the previously used index for future Sequential
    /// support. Updated on every call.
    /// </summary>
    public static AttackData SelectAttack(EnemyData enemy, ref int lastIndex)
    {
        if (enemy == null || enemy.AttackPool == null || enemy.AttackPool.Length == 0)
            return null;

        int index;

        switch (enemy.SelectionStrategy)
        {
            case AttackSelectionStrategy.Sequential:
                GD.PrintErr("[AttackSelector] Sequential strategy not yet implemented — falling back to Random.");
                goto case AttackSelectionStrategy.Random;

            case AttackSelectionStrategy.Weighted:
                GD.PrintErr("[AttackSelector] Weighted strategy not yet implemented — falling back to Random.");
                goto case AttackSelectionStrategy.Random;

            case AttackSelectionStrategy.Random:
            default:
                index = (int)(GD.Randi() % (uint)enemy.AttackPool.Length);
                break;
        }

        lastIndex = index;
        return enemy.AttackPool[index];
    }
}
