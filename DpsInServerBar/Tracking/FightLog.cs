using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Tracking;

/// <summary>A cast started by an enemy. <see cref="AtSeconds"/> counts from the start of the pull.</summary>
/// <param name="Caster">Name of the enemy casting, empty for the nameless helpers that cast many mechanics.</param>
/// <param name="CastSeconds">Cast time, 0 when unknown.</param>
public sealed record BossCast(double AtSeconds, string Caster, uint ActionId, string Action, float CastSeconds);

/// <summary>A debuff on a party member, from when it was applied until it ran out (or was cleansed).</summary>
/// <param name="EndSeconds">When it went away, null when it was still on at the end of the pull.</param>
/// <param name="Source">Who applied it, empty when unknown.</param>
/// <param name="Param">The status value sent by the game (its strength or stacks for some), 0 when none.</param>
public sealed record DebuffSpan(
    double AppliedSeconds, double? EndSeconds, string Player, string Job, bool IsYou, uint StatusId, string Status, string Source,
    int Param = 0);

/// <summary>
/// Log of the fight in progress (or the last one): the casts started by enemies, and every debuff put on a party
/// member with when it was applied and when it ran out. Reset when combat starts. Game thread only.
/// </summary>
public sealed class FightLog
{
    private const byte DetrimentalCategory = 2;
    // Bounds, for a pull that would never end.
    private const int MaxCasts = 2000;
    private const int MaxDebuffs = 3000;

    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;

    private readonly List<(DateTime At, string Caster, uint ActionId, string Action, float CastSeconds)> casts = [];
    private readonly Dictionary<ulong, uint> casting = new();
    private readonly List<Span> debuffs = [];
    private readonly Dictionary<(uint Target, uint Status, uint Source), Span> open = new();
    private readonly HashSet<(uint, uint, uint)> seen = [];
    private readonly Dictionary<uint, (bool Detrimental, string Name)> statuses = new();
    private readonly Dictionary<uint, string> actionNames = new();
    private bool wasInCombat;

    public FightLog(IObjectTable objectTable, IPartyList partyList, ICondition condition, IDataManager dataManager)
    {
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.condition = condition;
        this.dataManager = dataManager;
    }

    /// <summary>Called every frame.</summary>
    public void Update()
    {
        var now = DateTime.UtcNow;
        var inCombat = condition[ConditionFlag.InCombat];
        if (inCombat && !wasInCombat)
        {
            casts.Clear();
            casting.Clear();
            debuffs.Clear();
            open.Clear();
        }
        else if (!inCombat && wasInCombat)
        {
            // What is still on when the fight ends stays open: "until the end".
            open.Clear();
        }

        wasInCombat = inCombat;
        if (!inCombat)
            return;

        WatchCasts(now);
        WatchDebuffs(now);
    }

    /// <summary>The casts of the current (or last) fight, timed from <paramref name="startUtc"/>.</summary>
    public List<BossCast> CastsSince(DateTime startUtc) =>
        casts.Where(c => c.At >= startUtc.AddSeconds(-2))
             .Select(c => new BossCast(Math.Max(0, (c.At - startUtc).TotalSeconds), c.Caster, c.ActionId, c.Action, c.CastSeconds))
             .ToList();

    /// <summary>The debuffs of the current (or last) fight, timed from <paramref name="startUtc"/>.</summary>
    public List<DebuffSpan> DebuffsSince(DateTime startUtc) =>
        debuffs.Where(d => d.Applied >= startUtc.AddSeconds(-2))
               .Select(d => new DebuffSpan(Math.Max(0, (d.Applied - startUtc).TotalSeconds),
                                           d.End is { } end ? Math.Max(0, (end - startUtc).TotalSeconds) : null,
                                           d.Player, d.Job, d.IsYou, d.StatusId, d.Status, d.Source, d.Param))
               .ToList();

    private void WatchCasts(DateTime now)
    {
        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc npc || (npc.StatusFlags & Dalamud.Game.ClientState.Objects.Enums.StatusFlags.Hostile) == 0)
                continue;

            var action = npc.IsCasting ? npc.CastActionId : 0;
            if (action == 0)
            {
                casting.Remove(npc.GameObjectId);
                continue;
            }

            if (casting.TryGetValue(npc.GameObjectId, out var known) && known == action)
                continue;

            casting[npc.GameObjectId] = action;
            if (casts.Count < MaxCasts && ActionName(action) is { Length: > 0 } name)
                casts.Add((now, npc.Name.TextValue, action, name, npc.TotalCastTime));
        }
    }

    private void WatchDebuffs(DateTime now)
    {
        seen.Clear();
        foreach (var (chara, isYou) in PartyMembers())
        {
            var job = dataManager.GetExcelSheet<ClassJob>(ClientLanguage.English).TryGetRow(chara.ClassJob.RowId, out var cj)
                          ? cj.Abbreviation.ExtractText()
                          : string.Empty;
            foreach (var status in chara.StatusList)
            {
                if (status.StatusId == 0 || StatusInfo(status.StatusId) is not { Detrimental: true } info)
                    continue;

                var key = (chara.EntityId, status.StatusId, status.SourceId);
                seen.Add(key);
                if (open.ContainsKey(key) || debuffs.Count >= MaxDebuffs)
                    continue;

                var source = status.SourceId is 0 or 0xE0000000 ? string.Empty
                             : objectTable.SearchByEntityId(status.SourceId)?.Name.TextValue ?? string.Empty;
                var span = new Span(now, chara.Name.TextValue, job, isYou, status.StatusId, info.Name, source) { Param = status.Param };
                debuffs.Add(span);
                open[key] = span;
            }
        }

        // Gone since the last frame: ran out, cleansed, or its bearer died. A member out of reach keeps it open.
        foreach (var key in open.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            if (objectTable.SearchByEntityId(key.Target) is null)
                continue;
            open[key].End = now;
            open.Remove(key);
        }
    }

    private IEnumerable<(IBattleChara Chara, bool IsYou)> PartyMembers()
    {
        var local = objectTable.LocalPlayer;
        if (local is not null)
            yield return (local, true);

        for (var i = 0; i < partyList.Length; i++)
        {
            var member = partyList[i];
            if (member is null || member.EntityId == 0 || member.EntityId == local?.EntityId)
                continue;
            if (objectTable.SearchByEntityId(member.EntityId) is IBattleChara chara)
                yield return (chara, false);
        }
    }

    private (bool Detrimental, string Name)? StatusInfo(uint id)
    {
        if (!statuses.TryGetValue(id, out var info))
        {
            info = dataManager.GetExcelSheet<Status>().TryGetRow(id, out var row)
                       ? (row.StatusCategory == DetrimentalCategory, row.Name.ExtractText())
                       : (false, string.Empty);
            statuses[id] = info;
        }

        return info.Name.Length > 0 ? info : null;
    }

    // Names in the client's language, like the game shows them.
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

    private sealed class Span(DateTime applied, string player, string job, bool isYou, uint statusId, string status, string source)
    {
        public DateTime Applied { get; } = applied;
        public DateTime? End { get; set; }
        public string Player { get; } = player;
        public string Job { get; } = job;
        public bool IsYou { get; } = isYou;
        public uint StatusId { get; } = statusId;
        public string Status { get; } = status;
        public string Source { get; } = source;
        public int Param { get; init; }
    }
}
