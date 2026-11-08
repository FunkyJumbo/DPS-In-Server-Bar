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
    float[] ActiveBySecond,
    GcdAnalysis Gcd)
{
    public double Uptime => AvailableSeconds > 0 ? ActiveSeconds / AvailableSeconds : 1;
    public double LostSeconds => AvailableSeconds - ActiveSeconds;
}

/// <summary>
/// Measures your GCD uptime: the share of the time an enemy could be attacked during which your GCD was rolling
/// or you were casting. Time when no enemy is targetable (jumps, transitions, cutscenes) is excluded; time spent
/// dead counts as lost. Also kept per second (for the graph) and per ultimate phase. Game thread only.
/// <para>
/// The lost time is also split by cause (<see cref="GcdAnalysis"/>): each stretch where the GCD sat ready is
/// clipping while an animation lock held you, late otherwise, or "no GCD" once it lasted a whole GCD or more.
/// </para>
/// </summary>
public sealed unsafe class UptimeTracker
{
    // Recast group of the global cooldown.
    private const int GcdRecastGroup = 57;

    // The short lock after a cast ends is the caster's own, not an oGCD weave: counted as late, not clipping.
    private const double CastLockSeconds = 0.3;

    // Gaps shorter than this are latency between GCDs: counted, not listed.
    private const double ListedGapSeconds = 0.3;
    private const int MaxListedGaps = 300;

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

    // GCD analysis.
    private readonly List<GcdGap> gaps = [];
    private int gcdCount;
    private double recastSum;
    private double clipSeconds;
    private int clipCount;
    private double lateSeconds;
    private double noGcdSeconds;
    private int noGcdCount;
    private double deadSeconds;
    private bool gcdWasActive;
    private float lastElapsed;
    private bool wasCasting;
    private DateTime castEndedUtc;
    private bool inGap;
    private double gapStart;
    private double gapLocked;
    private double gapFree;

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
        else if (!inCombat && wasInCombat)
            CloseGap();
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

        var actionManager = ActionManager.Instance();
        var player = objectTable.LocalPlayer;
        var alive = player is not null && !player.IsDead;
        var casting = alive && player!.IsCasting;
        if (wasCasting && !casting)
            castEndedUtc = now;
        wasCasting = casting;

        var gcdRolling = CountGcds(actionManager);

        if (!enemyTargetable)
        {
            CloseGap();
            Record(now, dt, available: false, active: false);
            return;
        }

        var busy = alive && (casting || gcdRolling);
        if (!alive)
        {
            CloseGap();
            deadSeconds += dt; // time spent dead is lost uptime
        }
        else if (busy)
        {
            CloseGap();
        }
        else
        {
            if (!inGap)
            {
                inGap = true;
                gapStart = (now - combatStartUtc).TotalSeconds;
                gapLocked = gapFree = 0;
            }

            var locked = actionManager != null && actionManager->AnimationLock > 0
                         && (now - castEndedUtc).TotalSeconds > CastLockSeconds;
            if (locked)
                gapLocked += dt;
            else
                gapFree += dt;
        }

        Record(now, dt, available: true, active: busy);
    }

    /// <summary>Snapshot for display; rebuilt only when something changed.</summary>
    public UptimeState? Snapshot()
    {
        if (publishedRevision == revision)
            return Current;

        publishedRevision = revision;
        var gcd = new GcdAnalysis(
            gcdCount, gcdCount > 0 ? recastSum / gcdCount : 0,
            clipSeconds, clipCount, lateSeconds, noGcdSeconds, noGcdCount, deadSeconds, gaps.ToList());
        Current = new UptimeState(
            available, active,
            byPhase.OrderBy(p => p.Key).Select(p => new PhaseUptime(p.Key, p.Value.Available, p.Value.Active)).ToList(),
            availableBySecond.ToArray(), activeBySecond.ToArray(), gcd);
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

        gaps.Clear();
        gcdCount = 0;
        recastSum = clipSeconds = lateSeconds = noGcdSeconds = deadSeconds = 0;
        clipCount = noGcdCount = 0;
        gcdWasActive = false;
        lastElapsed = 0;
        inGap = false;
        revision++;
    }

    /// <summary>Counts each new GCD (its recast restarting); returns whether the GCD is rolling.</summary>
    private bool CountGcds(ActionManager* actionManager)
    {
        if (actionManager == null)
            return false;

        var gcd = actionManager->GetRecastGroupDetail(GcdRecastGroup);
        if (gcd == null)
            return false;

        var isActive = gcd->IsActive;
        if (isActive && (!gcdWasActive || gcd->Elapsed < lastElapsed - 0.01f))
        {
            gcdCount++;
            recastSum += gcd->Total;
        }

        gcdWasActive = isActive;
        lastElapsed = gcd->Elapsed;
        return isActive && gcd->Elapsed < gcd->Total;
    }

    /// <summary>Ends the current stretch with the GCD ready and puts its time under its cause.</summary>
    private void CloseGap()
    {
        if (!inGap)
            return;
        inGap = false;

        var seconds = gapLocked + gapFree;
        if (seconds <= 0)
            return;

        // A whole GCD or more without one is not a weave or a late press any more.
        var averageRecast = gcdCount > 0 ? recastSum / gcdCount : 2.5;
        GcdGapKind kind;
        if (seconds >= Math.Max(2.0, averageRecast))
        {
            kind = GcdGapKind.NoGcd;
            noGcdSeconds += seconds;
            noGcdCount++;
        }
        else
        {
            kind = gapLocked >= gapFree ? GcdGapKind.Clipping : GcdGapKind.Late;
            clipSeconds += gapLocked;
            lateSeconds += gapFree;
            if (gapLocked >= 0.1)
                clipCount++;
        }

        if (seconds >= ListedGapSeconds && gaps.Count < MaxListedGaps)
            gaps.Add(new GcdGap(gapStart, seconds, kind));
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

        // Striking dummies are neither hostile nor flagged in combat: the battle NPC you are hitting counts too.
        return objectTable.LocalPlayer?.TargetObject is IBattleNpc target
               && target.IsTargetable && !target.IsDead && target.MaxHp > 0;
    }
}
