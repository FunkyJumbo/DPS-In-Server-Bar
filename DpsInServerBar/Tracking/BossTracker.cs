using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace DpsInServerBar.Tracking;

/// <summary>The enemy with the most HP in the current fight, as read from the game.</summary>
public sealed record BossInfo(string Name, uint NameId, uint CurrentHp, uint MaxHp, uint EntityId = 0, uint BaseId = 0)
{
    public double HpFraction => MaxHp > 0 ? (double)CurrentHp / MaxHp : 0;
}

/// <summary>
/// Finds the "boss" of the current fight: the hostile enemy in combat with the highest max HP. The game exposes
/// HP for every enemy, but not enrage timers, which therefore have to be entered by hand. Also remembers the last
/// cast an enemy started, to tell how far into the fight a pull went. Game thread only.
/// </summary>
public sealed class BossTracker
{
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly Dictionary<ulong, uint> casting = new();
    private readonly Dictionary<uint, string> actionNames = new();
    private bool wasInCombat;

    public BossTracker(IObjectTable objectTable, ICondition condition, IDataManager dataManager)
    {
        this.objectTable = objectTable;
        this.condition = condition;
        this.dataManager = dataManager;
    }

    /// <summary>Boss of the fight in progress (kept after the fight ends, until the next one).</summary>
    public BossInfo? Current { get; private set; }

    /// <summary>Name of the last cast an enemy started in the fight in progress (kept after it ends), empty if none.</summary>
    public string LastCast { get; private set; } = string.Empty;

    public void Update()
    {
        var inCombat = condition[ConditionFlag.InCombat];
        if (inCombat && !wasInCombat)
        {
            Current = null;
            LastCast = string.Empty;
            casting.Clear();
        }

        wasInCombat = inCombat;

        if (!inCombat)
            return;

        IBattleNpc? best = null;
        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc npc
                || npc.BattleNpcKind != BattleNpcSubKind.Combatant
                || (npc.StatusFlags & StatusFlags.Hostile) == 0)
            {
                continue;
            }

            // Helpers without HP or a name cast most mechanics: their casts count too.
            WatchCast(npc);
            if (npc.MaxHp == 0)
                continue;

            // Prefer enemies actually fighting, then the biggest HP pool.
            if (best is null || Rank(npc).CompareTo(Rank(best)) > 0)
                best = npc;
        }

        if (best is not null)
            Current = new BossInfo(best.Name.TextValue, best.NameId, best.CurrentHp, best.MaxHp, best.EntityId, best.BaseId);
    }

    // A cast just started by an enemy becomes the last one.
    private void WatchCast(IBattleNpc npc)
    {
        var action = npc.IsCasting ? npc.CastActionId : 0;
        if (action == 0)
        {
            casting.Remove(npc.GameObjectId);
            return;
        }

        if (casting.TryGetValue(npc.GameObjectId, out var known) && known == action)
            return;

        casting[npc.GameObjectId] = action;
        if (ActionName(action) is { Length: > 0 } name)
            LastCast = name;
    }

    private string ActionName(uint action)
    {
        if (!actionNames.TryGetValue(action, out var name))
        {
            name = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(action, out var row)
                       ? row.Name.ExtractText()
                       : string.Empty;
            actionNames[action] = name;
        }

        return name;
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
