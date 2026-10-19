using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace DpsInServerBar.Tracking;

/// <summary>The enemy with the most HP in the current fight, as read from the game.</summary>
public sealed record BossInfo(string Name, uint NameId, uint CurrentHp, uint MaxHp)
{
    public double HpFraction => MaxHp > 0 ? (double)CurrentHp / MaxHp : 0;
}

/// <summary>
/// Finds the "boss" of the current fight: the hostile enemy in combat with the highest max HP. The game exposes
/// HP for every enemy, but not enrage timers, which therefore have to be entered by hand. Game thread only.
/// </summary>
public sealed class BossTracker
{
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private bool wasInCombat;

    public BossTracker(IObjectTable objectTable, ICondition condition)
    {
        this.objectTable = objectTable;
        this.condition = condition;
    }

    /// <summary>Boss of the fight in progress (kept after the fight ends, until the next one).</summary>
    public BossInfo? Current { get; private set; }

    public void Update()
    {
        var inCombat = condition[ConditionFlag.InCombat];
        if (inCombat && !wasInCombat)
            Current = null;
        wasInCombat = inCombat;

        if (!inCombat)
            return;

        IBattleNpc? best = null;
        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc npc
                || npc.BattleNpcKind != BattleNpcSubKind.Combatant
                || (npc.StatusFlags & StatusFlags.Hostile) == 0
                || npc.MaxHp == 0)
            {
                continue;
            }

            // Prefer enemies actually fighting, then the biggest HP pool.
            if (best is null || Rank(npc).CompareTo(Rank(best)) > 0)
                best = npc;
        }

        if (best is not null)
            Current = new BossInfo(best.Name.TextValue, best.NameId, best.CurrentHp, best.MaxHp);
    }

    private static (bool InCombat, uint MaxHp) Rank(IBattleNpc npc) =>
        ((npc.StatusFlags & StatusFlags.InCombat) != 0, npc.MaxHp);

    /// <summary>Projected kill time at the given party DPS, or null if it cannot be estimated.</summary>
    public static TimeSpan? ProjectedKill(BossInfo? boss, TimeSpan elapsed, double partyDps)
    {
        if (boss is null || partyDps <= 0)
            return null;
        return elapsed + TimeSpan.FromSeconds(boss.CurrentHp / partyDps);
    }
}
