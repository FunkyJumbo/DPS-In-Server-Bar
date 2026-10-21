using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DpsInServerBar.Tracking;

/// <summary>Uptime of one ultimate phase.</summary>
public sealed record PhaseUptime(int Number, double AvailableSeconds, double ActiveSeconds)
{
    public double Uptime => AvailableSeconds > 0 ? ActiveSeconds / AvailableSeconds : 1;
}

/// <summary>GCD uptime of the current (or last) fight.</summary>
public sealed record UptimeState(
    double AvailableSeconds,
    double ActiveSeconds,
    IReadOnlyList<PhaseUptime> Phases,
    float[] AvailableBySecond,
    float[] ActiveBySecond)
{
    public double Uptime => AvailableSeconds > 0 ? ActiveSeconds / AvailableSeconds : 1;
    public double LostSeconds => AvailableSeconds - ActiveSeconds;
}

/// <summary>
/// Measures your GCD uptime: the share of the time an enemy could be attacked during which your GCD was rolling
/// or you were casting. Time when no enemy is targetable (jumps, transitions, cutscenes) is excluded; time spent
/// dead counts as lost. Also kept per second (for the graph) and per ultimate phase. Game thread only.
/// </summary>
public sealed unsafe class UptimeTracker
{
    // Recast group of the global cooldown.
    private const int GcdRecastGroup = 57;

    // Enemy targetability is read a few times per second; GCD state every frame.
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(100);

    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly Func<int?> currentPhase;

    private readonly List<float> availableBySecond = [];
    private readonly List<float> activeBySecond = [];
    private readonly Dictionary<int, (double Available, double Active)> byPhase = new();

    private bool wasInCombat;
    private DateTime combatStartUtc;
    private DateTime lastFrameUtc;
    private DateTime nextScanUtc;
    private bool enemyTargetable;
    private double available;
    private double active;
    private int revision;
    private int publishedRevision = -1;

    public UptimeTracker(IObjectTable objectTable, ICondition condition, Func<int?> currentPhase)
    {
        this.objectTable = objectTable;
        this.condition = condition;
        this.currentPhase = currentPhase;
    }

    /// <summary>Uptime of the fight in progress, or of the last one. Null before the first fight.</summary>
    public UptimeState? Current { get; private set; }

    /// <summary>Called every frame.</summary>
    public void Update()
    {
        var now = DateTime.UtcNow;
        var inCombat = condition[ConditionFlag.InCombat];

        if (inCombat && !wasInCombat)
            Reset(now);
        wasInCombat = inCombat;

        if (!inCombat)
            return;

        var dt = (now - lastFrameUtc).TotalSeconds;
        lastFrameUtc = now;
        if (dt <= 0 || dt > 1)
            return; // first frame, or a hitch (loading screen): don't count it

        if (now >= nextScanUtc)
        {
            nextScanUtc = now + ScanInterval;
            enemyTargetable = AnyEnemyTargetable();
        }

        if (!enemyTargetable)
        {
            Record(now, dt, available: false, active: false);
            return;
        }

        Record(now, dt, available: true, active: IsGcdActive());
    }

    /// <summary>Snapshot for display; rebuilt only when something changed.</summary>
    public UptimeState? Snapshot()
    {
        if (publishedRevision == revision)
            return Current;

        publishedRevision = revision;
        Current = new UptimeState(
            available, active,
            byPhase.OrderBy(p => p.Key).Select(p => new PhaseUptime(p.Key, p.Value.Available, p.Value.Active)).ToList(),
            availableBySecond.ToArray(), activeBySecond.ToArray());
        return Current;
    }

    private void Reset(DateTime now)
    {
        combatStartUtc = now;
        lastFrameUtc = now;
        nextScanUtc = now;
        available = active = 0;
        availableBySecond.Clear();
        activeBySecond.Clear();
        byPhase.Clear();
        revision++;
    }

    private void Record(DateTime now, double dt, bool available, bool active)
    {
        var second = (int)(now - combatStartUtc).TotalSeconds;
        while (availableBySecond.Count <= second)
        {
            availableBySecond.Add(0);
            activeBySecond.Add(0);
        }

        if (available)
        {
            this.available += dt;
            availableBySecond[second] += (float)dt;
            if (active)
            {
                this.active += dt;
                activeBySecond[second] += (float)dt;
            }

            if (currentPhase() is { } phase)
            {
                var (a, b) = byPhase.GetValueOrDefault(phase);
                byPhase[phase] = (a + dt, b + (active ? dt : 0));
            }
        }

        revision++;
    }

    private bool AnyEnemyTargetable()
    {
        foreach (var obj in objectTable)
        {
            if (obj is IBattleNpc npc
                && npc.BattleNpcKind == BattleNpcSubKind.Combatant
                && (npc.StatusFlags & StatusFlags.Hostile) != 0
                && (npc.StatusFlags & StatusFlags.InCombat) != 0
                && npc.IsTargetable
                && !npc.IsDead)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsGcdActive()
    {
        var player = objectTable.LocalPlayer;
        if (player is null || player.IsDead)
            return false; // time spent dead is lost uptime

        if (player.IsCasting)
            return true;

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return false;

        var gcd = actionManager->GetRecastGroupDetail(GcdRecastGroup);
        return gcd != null && gcd->IsActive && gcd->Elapsed < gcd->Total;
    }
}
