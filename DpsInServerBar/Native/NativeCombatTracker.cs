using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using DpsInServerBar.Combat;
using DpsInServerBar.Rdps;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;
using Lumina.Excel.Sheets;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace DpsInServerBar.Native;

/// <summary>
/// Built-in damage meter: hooks the game's action effect and DoT tick handlers and accumulates
/// damage dealt by party members (pets folded into their owner). Less exhaustive than ACT, but needs nothing else.
/// All hooks fire on the game thread, as does <see cref="Tick"/>, so no locking is needed.
/// </summary>
public sealed unsafe class NativeCombatTracker : ICombatSource, IDisposable
{
    private const uint InvalidId = 0xE0000000;
    private const uint LimitBreakId = 0xFFFFFFFE;
    private const uint ActorControlDeath = 6;
    private const uint ActorControlDotHot = 23;
    private const uint DotHotTypeDamage = 3;

    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly RdpsEngine rdps;
    private readonly DeathRecorder deaths;

    private readonly Hook<ActionEffectHandler.Delegates.Receive>? effectHook;
    private readonly Hook<PacketDispatcher.Delegates.HandleActorControlPacket>? actorControlHook;

    private readonly HashSet<uint> limitBreakActions;
    private readonly Dictionary<uint, PartyMemberInfo> party = new();
    private readonly Dictionary<uint, ActorStats> actors = new();
    private readonly Dictionary<uint, TargetStats> targets = new();

    private bool active;
    private DateTime firstHitUtc;
    private DateTime lastHitUtc;
    private DateTime nextSnapshotUtc;
    private CombatSnapshot? latest;

    public NativeCombatTracker(
        IGameInteropProvider interop, IObjectTable objectTable, IPartyList partyList, ICondition condition,
        IClientState clientState, IDataManager dataManager, IPlayerState playerState, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.condition = condition;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.log = log;
        rdps = new RdpsEngine(objectTable, playerState, ResolvePartyOwner);
        deaths = new DeathRecorder(dataManager, log);

        limitBreakActions = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
            .Where(a => a.ActionCategory.RowId == 9)
            .Select(a => a.RowId)
            .ToHashSet();

        try
        {
            effectHook = interop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
                ActionEffectHandler.Addresses.Receive.Value, OnActionEffect);
            actorControlHook = interop.HookFromAddress<PacketDispatcher.Delegates.HandleActorControlPacket>(
                PacketDispatcher.Addresses.HandleActorControlPacket.Value, OnActorControl);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to create the built-in tracker hooks");
            HookError = ex.Message;
        }
    }

    public string Name => "Built-in";
    public bool IsEnabled => effectHook?.IsEnabled == true;
    public bool IsReady => IsEnabled;
    public string? HookError { get; }
    public CombatSnapshot? Latest => latest;

    /// <summary>What killed you last (needs the built-in tracker to be running).</summary>
    public DeathRecorder Deaths => deaths;

    public string StatusText => HookError is not null
        ? $"unavailable ({HookError})"
        : IsEnabled ? "listening" : "disabled";

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            effectHook?.Enable();
            actorControlHook?.Enable();
        }
        else
        {
            effectHook?.Disable();
            actorControlHook?.Disable();
            latest = null;
            active = false;
        }
    }

    public void Dispose()
    {
        effectHook?.Dispose();
        actorControlHook?.Dispose();
    }

    /// <summary>Called from the framework update: refreshes the party list, closes encounters and publishes snapshots.</summary>
    public void Tick()
    {
        if (!IsEnabled)
            return;

        RefreshParty();
        deaths.Observe(objectTable.LocalPlayer, NameOf);

        if (!active)
            return;

        var now = DateTime.UtcNow;
        var inCombat = condition[ConditionFlag.InCombat];
        if (!inCombat && (now - lastHitUtc).TotalSeconds > 1.5)
        {
            active = false;
            latest = BuildSnapshot(isActive: false, lastHitUtc);
            return;
        }

        if (now >= nextSnapshotUtc)
        {
            nextSnapshotUtc = now.AddSeconds(1);
            latest = BuildSnapshot(isActive: true, now);
        }
    }

    private void OnActionEffect(
        uint casterEntityId, Character* casterPtr, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        effectHook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);

        try
        {
            RecordHitsOnYou(casterEntityId, header, effects, targetEntityIds);

            var owner = ResolvePartyOwner(casterEntityId);
            if (owner == 0)
                return;

            // ActionType 1 = regular action; LB damage is reported separately, like ACT does.
            if (header->ActionType == 1 && limitBreakActions.Contains(header->ActionId))
                owner = LimitBreakId;

            for (var t = 0; t < header->NumTargets; t++)
            {
                var targetId = targetEntityIds[t].ObjectId;
                if (targetId == 0 || targetId == InvalidId || party.ContainsKey(targetId))
                    continue;

                foreach (ref readonly var effect in effects[t].Effects)
                {
                    // 14 = status applied to the target: remember the buffs a DoT snapshots.
                    if (effect.Type == 14 && owner != LimitBreakId && party.TryGetValue(owner, out var applier))
                    {
                        rdps.OnDotApplied(owner, applier.Job, targetId, effect.Value);
                        continue;
                    }

                    // 3 = damage, 5 = blocked damage, 6 = parried damage
                    if (effect.Type is not (3 or 5 or 6))
                        continue;

                    long amount = effect.Value;
                    if ((effect.Param4 & 0x40) != 0)
                        amount += (long)effect.Param3 << 16;

                    var crit = (effect.Param0 & 0x20) != 0;
                    var directHit = (effect.Param0 & 0x40) != 0;
                    AddDamage(owner, targetId, amount, crit, directHit, header->ActionId, dotStatusId: null);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error while processing an action effect");
        }
    }

    private void OnActorControl(
        uint entityId, uint category, uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8, GameObjectId targetId, bool isRecorded)
    {
        actorControlHook!.Original(entityId, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetId, isRecorded);

        try
        {
            RecordDamageOrDeathOfYou(entityId, category, arg1, arg2, arg3, arg4);

            switch (category)
            {
                // DoT tick: entity = target, arg1 = status id, arg2 = type, arg3 = amount, arg4 = source (often unknown).
                case ActorControlDotHot when arg2 == DotHotTypeDamage && arg3 > 0:
                    var source = arg4 is 0 or InvalidId ? FindDotSource(entityId, arg1) : arg4;
                    var owner = source == 0 ? 0 : ResolvePartyOwner(source);
                    if (owner != 0 && !party.ContainsKey(entityId))
                        AddDamage(owner, entityId, arg3, false, false, 0, dotStatusId: arg1);
                    break;

                case ActorControlDeath when active && actors.TryGetValue(entityId, out var dead):
                    dead.Deaths++;
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error while processing an actor control packet");
        }
    }

    /// <summary>Remembers the hits you take, to tell what killed you.</summary>
    private void RecordHitsOnYou(
        uint casterEntityId, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds)
    {
        var you = objectTable.LocalPlayer?.EntityId ?? 0;
        if (you == 0)
            return;

        for (var t = 0; t < header->NumTargets; t++)
        {
            if (targetEntityIds[t].ObjectId != you)
                continue;

            long amount = 0;
            foreach (ref readonly var effect in effects[t].Effects)
            {
                if (effect.Type is not (3 or 5 or 6))
                    continue;
                amount += effect.Value;
                if ((effect.Param4 & 0x40) != 0)
                    amount += (long)effect.Param3 << 16;
            }

            if (amount > 0)
                deaths.OnHitTaken(header->ActionType, header->ActionId, NameOf(casterEntityId), amount);
        }
    }

    private void RecordDamageOrDeathOfYou(uint entityId, uint category, uint arg1, uint arg2, uint arg3, uint arg4)
    {
        var you = objectTable.LocalPlayer?.EntityId ?? 0;
        if (you == 0 || entityId != you)
            return;

        if (category == ActorControlDeath)
            deaths.OnDeath();
        else if (category == ActorControlDotHot && arg2 == DotHotTypeDamage && arg3 > 0)
            deaths.OnDotTaken(arg1, arg4 is 0 or InvalidId ? string.Empty : NameOf(arg4), arg3);
    }

    private string NameOf(uint entityId) => objectTable.SearchByEntityId(entityId)?.Name.TextValue ?? string.Empty;

    private void AddDamage(uint ownerId, uint targetId, long amount, bool crit, bool directHit, uint actionId, uint? dotStatusId)
    {
        var now = DateTime.UtcNow;
        if (!active)
            StartEncounter(now);

        lastHitUtc = now;

        if (!actors.TryGetValue(ownerId, out var stats))
        {
            stats = ownerId == LimitBreakId
                ? new ActorStats(ownerId, "Limit Break", string.Empty, false)
                : party.TryGetValue(ownerId, out var member)
                    ? new ActorStats(ownerId, member.Name, member.Job, member.IsLocalPlayer)
                    : new ActorStats(ownerId, "Unknown", string.Empty, false);
            actors[ownerId] = stats;
        }

        stats.Damage += amount;
        stats.Hits++;
        if (crit && directHit)
            stats.CritDirectHits++;
        if (crit)
            stats.Crits++;
        if (directHit)
            stats.DirectHits++;

        if (!targets.TryGetValue(targetId, out var target))
        {
            target = new TargetStats(objectTable.SearchByEntityId(targetId)?.Name.TextValue ?? string.Empty);
            targets[targetId] = target;
        }

        target.Damage += amount;

        if (ownerId != LimitBreakId)
        {
            if (dotStatusId is { } statusId)
                rdps.OnDotTick(ownerId, stats.Job, targetId, statusId, amount);
            else
                rdps.OnHit(ownerId, stats.Job, targetId, actionId, amount, crit, directHit);
        }
    }

    private void StartEncounter(DateTime now)
    {
        actors.Clear();
        targets.Clear();
        rdps.Reset();
        active = true;
        firstHitUtc = now;
        nextSnapshotUtc = now;
    }

    /// <summary>Maps a caster (player or pet) to the party member it belongs to, or 0 if it is not ours.</summary>
    private uint ResolvePartyOwner(uint casterId)
    {
        if (party.ContainsKey(casterId))
            return casterId;

        var obj = objectTable.SearchByEntityId(casterId);
        if (obj is not null && obj.OwnerId != 0 && obj.OwnerId != InvalidId && party.ContainsKey(obj.OwnerId))
            return obj.OwnerId;

        return 0;
    }

    /// <summary>When the server does not tell who owns a DoT, look for the status on the target.</summary>
    private uint FindDotSource(uint targetId, uint statusId)
    {
        if (objectTable.SearchByEntityId(targetId) is not IBattleChara target)
            return 0;

        uint found = 0;
        foreach (var status in target.StatusList)
        {
            if (status.StatusId != statusId || !party.ContainsKey(ResolvePartyOwner(status.SourceId)))
                continue;

            // Same DoT applied by two party members: attribution is ambiguous, skip it.
            if (found != 0 && found != status.SourceId)
                return 0;
            found = status.SourceId;
        }

        return found;
    }

    private void RefreshParty()
    {
        party.Clear();

        var local = objectTable.LocalPlayer;
        if (local is not null)
        {
            party[local.EntityId] = new PartyMemberInfo(
                local.Name.TextValue, JobAbbreviation(local.ClassJob.RowId), true);
        }

        for (var i = 0; i < partyList.Length; i++)
        {
            var member = partyList[i];
            if (member is null || member.EntityId == 0 || party.ContainsKey(member.EntityId))
                continue;

            party[member.EntityId] = new PartyMemberInfo(
                member.Name.TextValue, JobAbbreviation(member.ClassJob.RowId), false);
        }
    }

    private CombatSnapshot BuildSnapshot(bool isActive, DateTime endUtc)
    {
        var duration = endUtc - firstHitUtc;
        var seconds = Math.Max(1, duration.TotalSeconds);
        var totalDamage = actors.Values.Sum(a => (double)a.Damage);

        var combatants = actors.Values
            .Select(a => new CombatantInfo(
                Name: a.Name,
                Job: a.Job.ToUpperInvariant(),
                EncDps: a.Damage / seconds,
                RDps: a.Id == LimitBreakId ? a.Damage / seconds : (a.Damage + rdps.NetGain(a.Id)) / seconds,
                EncHps: 0,
                Damage: a.Damage,
                DamagePercent: totalDamage > 0 ? a.Damage * 100 / totalDamage : 0,
                CritPercent: Percent(a.Crits, a.Hits),
                DirectHitPercent: Percent(a.DirectHits, a.Hits),
                CritDirectHitPercent: Percent(a.CritDirectHits, a.Hits),
                Deaths: a.Deaths,
                IsYou: a.IsLocalPlayer,
                RdpsBreakdown: a.Id == LimitBreakId ? null : rdps.Detail(a.Id, seconds)))
            .ToList();

        // Like ACT, name the encounter after the enemy that took the most damage.
        var title = targets.Values.OrderByDescending(t => t.Damage).FirstOrDefault()?.Name ?? string.Empty;
        var zone = dataManager.GetExcelSheet<TerritoryType>().TryGetRow(clientState.TerritoryType, out var row)
            ? row.PlaceName.Value.Name.ExtractText()
            : string.Empty;

        var encounter = new EncounterInfo(title, zone, duration, totalDamage / seconds, totalDamage);
        return new CombatSnapshot(encounter, combatants, isActive, DateTime.UtcNow) { StartUtc = firstHitUtc };
    }

    /// <summary>English abbreviation ("DRK"), matching what ACT sends, whatever the client language.</summary>
    private string JobAbbreviation(uint classJobId) =>
        dataManager.GetExcelSheet<ClassJob>(ClientLanguage.English).TryGetRow(classJobId, out var job)
            ? job.Abbreviation.ExtractText()
            : string.Empty;

    private static double Percent(int part, int total) => total > 0 ? part * 100.0 / total : 0;

    private sealed record PartyMemberInfo(string Name, string Job, bool IsLocalPlayer);

    private sealed class ActorStats(uint id, string name, string job, bool isLocalPlayer)
    {
        public uint Id { get; } = id;
        public string Name { get; } = name;
        public string Job { get; } = job;
        public bool IsLocalPlayer { get; } = isLocalPlayer;
        public long Damage { get; set; }
        public int Hits { get; set; }
        public int Crits { get; set; }
        public int DirectHits { get; set; }
        public int CritDirectHits { get; set; }
        public int Deaths { get; set; }
    }

    private sealed class TargetStats(string name)
    {
        public string Name { get; } = name;
        public long Damage { get; set; }
    }
}
