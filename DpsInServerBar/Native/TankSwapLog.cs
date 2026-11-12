using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using DpsInServerBar.Combat;

namespace DpsInServerBar.Native;

/// <summary>The boss moving from one tank to another, and why.</summary>
public sealed record TankSwap(double AtSeconds, string From, string To, string Reason);

/// <summary>
/// Follows who the boss is attacking. When it moves from a party tank to another and stays there, records a swap
/// with its reason: the previous tank died, the new one used Provoke, the previous one used Shirk, or neither
/// (enmity from damage). A boss turning to a non-tank (a mechanic) is ignored. Game thread only.
/// </summary>
public sealed class TankSwapLog
{
    private const uint Provoke = 7533;
    private const uint Shirk = 7537;
    private const int MaxSwaps = 200;

    // The new target must hold this long to be a swap (bosses turn briefly during some casts).
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(0.6);

    // Provoke or Shirk this close before the swap caused it.
    private static readonly TimeSpan CauseWindow = TimeSpan.FromSeconds(3);

    private readonly IObjectTable objectTable;
    private readonly Dictionary<uint, DateTime> lastProvoke = new();
    private readonly Dictionary<uint, DateTime> lastShirk = new();
    private readonly List<TankSwap> swaps = [];

    private bool wasInCombat;
    private DateTime startUtc;
    private uint holder;
    private uint candidate;
    private DateTime candidateSince;
    private int revision;
    private int publishedRevision = -1;
    private IReadOnlyList<TankSwap> published = [];

    public TankSwapLog(IObjectTable objectTable)
    {
        this.objectTable = objectTable;
    }

    /// <summary>Entity id of the boss of the fight (0 when none).</summary>
    public Func<uint>? BossId { get; set; }

    /// <summary>Tank swaps of the fight in progress, or of the last one, oldest first.</summary>
    public IReadOnlyList<TankSwap> Current
    {
        get
        {
            if (publishedRevision != revision)
            {
                publishedRevision = revision;
                published = swaps.ToList();
            }

            return published;
        }
    }

    /// <summary>A party member used an action (from the action effect hook).</summary>
    public void OnAction(uint casterId, uint actionId)
    {
        if (actionId == Provoke)
            lastProvoke[casterId] = DateTime.UtcNow;
        else if (actionId == Shirk)
            lastShirk[casterId] = DateTime.UtcNow;
    }

    /// <param name="party">Party members by entity id: name and English job abbreviation.</param>
    public void Tick(IEnumerable<(uint Id, string Name, string Job)> party, bool inCombat)
    {
        var now = DateTime.UtcNow;
        if (inCombat && !wasInCombat)
        {
            swaps.Clear();
            lastProvoke.Clear();
            lastShirk.Clear();
            holder = candidate = 0;
            startUtc = now;
            revision++;
        }

        wasInCombat = inCombat;
        if (!inCombat || BossId?.Invoke() is not { } bossId || bossId == 0
            || objectTable.SearchByEntityId(bossId) is not IBattleNpc boss || boss.IsDead)
        {
            return;
        }

        var tanks = party.Where(p => JobRoles.Of(p.Job) == JobRole.Tank).ToDictionary(p => p.Id, p => p.Name);
        var target = (uint)boss.TargetObjectId;
        if (!tanks.ContainsKey(target))
            return; // no target, or a mechanic aimed at someone else

        if (target == holder)
        {
            candidate = 0;
            return;
        }

        if (target != candidate)
        {
            candidate = target;
            candidateSince = now;
            return;
        }

        if (now - candidateSince < Settle)
            return;

        if (holder != 0 && swaps.Count < MaxSwaps)
        {
            var from = tanks.GetValueOrDefault(holder) ?? NameOf(holder);
            swaps.Add(new TankSwap((candidateSince - startUtc).TotalSeconds, from, tanks[target], Reason(holder, target)));
            revision++;
        }

        holder = target;
        candidate = 0;
    }

    private string Reason(uint from, uint to)
    {
        if (objectTable.SearchByEntityId(from) is IBattleChara { IsDead: true })
            return Loc.T("Death");

        var since = candidateSince - CauseWindow;
        var provoked = lastProvoke.TryGetValue(to, out var p) && p >= since;
        var shirked = lastShirk.TryGetValue(from, out var s) && s >= since;
        return (provoked, shirked) switch
        {
            (true, true) => Loc.T("Provoke + Shirk"),
            (true, false) => Loc.T("Provoke"),
            (false, true) => Loc.T("Shirk"),
            _ => Loc.T("Enmity (damage)"),
        };
    }

    private string NameOf(uint entityId) => objectTable.SearchByEntityId(entityId)?.Name.TextValue ?? "?";
}
