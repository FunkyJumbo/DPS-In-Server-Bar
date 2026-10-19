using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Player;
using Dalamud.Plugin.Services;
using DpsInServerBar.Combat;

namespace DpsInServerBar.Rdps;

/// <summary>
/// Tracks, for the current encounter, how much damage each party member gave away to buffers and received
/// from their own buffs. rDPS damage = own damage - given + received. Game thread only.
/// </summary>
public sealed class RdpsEngine
{
    // Prior used before enough hits are seen to estimate a player's own rates (typical 7.x raid gear).
    private const double PriorCrit = 0.23;
    private const double PriorDirectHit = 0.22;
    private const double PriorWeight = 40;

    // Level 100 substat constants.
    private const int SubStat = 420;
    private const int Divisor = 2780;

    private readonly IObjectTable objectTable;
    private readonly IPlayerState playerState;
    private readonly Func<uint, uint> resolveOwner;

    private readonly Dictionary<uint, double> given = new();
    private readonly Dictionary<uint, double> received = new();
    private readonly Dictionary<uint, (RateEstimator Crit, RateEstimator DirectHit)> rates = new();

    // Buffs present when a DoT was applied: DoTs keep (snapshot) them for their whole duration.
    private readonly Dictionary<(uint Target, uint Status, uint Owner), List<BuffEffect>> dotSnapshots = new();

    public RdpsEngine(IObjectTable objectTable, IPlayerState playerState, Func<uint, uint> resolveOwner)
    {
        this.objectTable = objectTable;
        this.playerState = playerState;
        this.resolveOwner = resolveOwner;
    }

    public void Reset()
    {
        given.Clear();
        received.Clear();
        dotSnapshots.Clear();
        // Rate estimates are kept across pulls: the same players usually do several pulls together.
    }

    /// <summary>Net rDPS damage adjustment for a party member (positive for buffers).</summary>
    public double NetGain(uint ownerId) => received.GetValueOrDefault(ownerId) - given.GetValueOrDefault(ownerId);

    /// <summary>Breakdown shown in the main window, to compare with FFLogs.</summary>
    public RdpsDetail Detail(uint ownerId, double seconds)
    {
        var (crit, dh) = BaseRates(ownerId);
        var samples = rates.TryGetValue(ownerId, out var r) ? r.Crit.Samples : 0;
        return new RdpsDetail(
            given.GetValueOrDefault(ownerId) / seconds, received.GetValueOrDefault(ownerId) / seconds, crit, dh, samples);
    }

    public void OnHit(uint ownerId, string job, uint targetId, uint actionId, double damage, bool crit, bool directHit)
    {
        var buffs = CollectBuffs(ownerId, job, targetId);
        var (autoCrit, autoDh) = AutoHit(ownerId, actionId, crit, directHit);
        var (baseCrit, baseDh) = BaseRates(ownerId);

        Observe(ownerId, buffs, crit && !autoCrit, directHit && !autoDh, autoCrit, autoDh);

        Allocate(ownerId, new HitContext
        {
            Damage = damage,
            Crit = crit,
            DirectHit = directHit,
            AutoCrit = autoCrit,
            AutoDirectHit = autoDh,
            BaseCritRate = baseCrit,
            BaseDirectHitRate = baseDh,
            Buffs = buffs,
        });
    }

    public void OnDotApplied(uint ownerId, string job, uint targetId, uint statusId) =>
        dotSnapshots[(targetId, statusId, ownerId)] = CollectBuffs(ownerId, job, targetId);

    public void OnDotTick(uint ownerId, string job, uint targetId, uint statusId, double damage)
    {
        if (!dotSnapshots.TryGetValue((targetId, statusId, ownerId), out var buffs))
            buffs = CollectBuffs(ownerId, job, targetId);

        var (baseCrit, baseDh) = BaseRates(ownerId);
        Allocate(ownerId, new HitContext
        {
            Damage = damage,
            IsDot = true,
            BaseCritRate = baseCrit,
            BaseDirectHitRate = baseDh,
            Buffs = buffs,
        });
    }

    private void Allocate(uint ownerId, HitContext hit)
    {
        RdpsMath.Allocate(hit, (source, amount) =>
        {
            given[ownerId] = given.GetValueOrDefault(ownerId) + amount;
            received[source] = received.GetValueOrDefault(source) + amount;
        });
    }

    /// <summary>External raid buffs on the attacker and raid debuffs on the enemy, with their party source.</summary>
    private List<BuffEffect> CollectBuffs(uint ownerId, string job, uint targetId)
    {
        var result = new List<BuffEffect>();
        AddStatuses(result, objectTable.SearchByEntityId(ownerId) as IBattleChara, BuffTarget.Attacker, ownerId, job);
        AddStatuses(result, objectTable.SearchByEntityId(targetId) as IBattleChara, BuffTarget.Enemy, ownerId, job);
        return result;
    }

    private void AddStatuses(List<BuffEffect> result, IBattleChara? chara, BuffTarget on, uint ownerId, string job)
    {
        if (chara is null)
            return;

        foreach (var status in chara.StatusList)
        {
            if (!RaidBuffs.ByStatusId.TryGetValue(status.StatusId, out var buff) || buff.On != on)
                continue;

            // Self buffs are not redistributed (FFLogs treats them as part of the player's own damage).
            var source = resolveOwner(status.SourceId);
            if (source == 0 || source == ownerId)
                continue;

            var (damage, crit, dh) = buff.Resolve(job, status.Param);
            result.Add(new BuffEffect(source, 1 + damage / 100, crit, dh));
        }
    }

    private (bool Crit, bool DirectHit) AutoHit(uint ownerId, uint actionId, bool crit, bool directHit)
    {
        if (RaidBuffs.AutoCritDirectHitActions.Contains(actionId))
            return (true, true);

        if (objectTable.SearchByEntityId(ownerId) is not IBattleChara chara)
            return (false, false);

        foreach (var status in chara.StatusList)
        {
            // The status is only consumed by the hit it guarantees, so require the matching outcome.
            if (RaidBuffs.AutoHitStatuses.TryGetValue(status.StatusId, out var auto)
                && (!auto.Crit || crit) && (!auto.DirectHit || directHit))
            {
                return auto;
            }
        }

        return (false, false);
    }

    private (double Crit, double DirectHit) BaseRates(uint ownerId)
    {
        // Our own stats are known exactly (only at level 100, where the constants above apply).
        var local = objectTable.LocalPlayer;
        if (local is not null && local.EntityId == ownerId && playerState.IsLoaded
            && playerState.Level == 100 && !playerState.IsLevelSynced)
        {
            var critStat = playerState.GetAttribute(PlayerAttribute.CriticalHit);
            var dhStat = playerState.GetAttribute(PlayerAttribute.DirectHitRate);
            if (critStat > 0)
            {
                var crit = Math.Floor(200.0 * (critStat - SubStat) / Divisor + 50) / 1000;
                var dh = Math.Floor(550.0 * (dhStat - SubStat) / Divisor) / 1000;
                return (crit, Math.Max(0, dh));
            }
        }

        // Other players' stats are not available client side: estimate them from their hits.
        var r = Rates(ownerId);
        return (r.Crit.Estimate(), r.DirectHit.Estimate());
    }

    private void Observe(uint ownerId, List<BuffEffect> buffs, bool crit, bool directHit, bool autoCrit, bool autoDh)
    {
        var critBonus = 0.0;
        var dhBonus = 0.0;
        foreach (var b in buffs)
        {
            critBonus += b.CritRate;
            dhBonus += b.DirectHitRate;
        }

        // Guaranteed outcomes say nothing about the player's rate.
        var r = Rates(ownerId);
        if (!autoCrit)
            r.Crit.Add(critBonus, crit);
        if (!autoDh)
            r.DirectHit.Add(dhBonus, directHit);
    }

    private (RateEstimator Crit, RateEstimator DirectHit) Rates(uint ownerId)
    {
        if (!rates.TryGetValue(ownerId, out var r))
        {
            r = (new RateEstimator(PriorCrit, PriorWeight), new RateEstimator(PriorDirectHit, PriorWeight));
            rates[ownerId] = r;
        }

        return r;
    }
}
