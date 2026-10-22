using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Ultimates;

/// <summary>State of the current ultimate phase, for display.</summary>
public sealed record PhaseView(
    UltimateFight Fight,
    UltimatePhase? Phase,
    int Number,
    bool Started,
    TimeSpan Elapsed,
    double CurrentHp,
    double MaxHp,
    double? EnrageSeconds,
    bool InCombat)
{
    /// <summary>False past the last phase defined for this ultimate.</summary>
    public bool Supported => Phase is not null;

    public double HpFraction => MaxHp > 0 ? CurrentHp / MaxHp : 0;

    /// <summary>Damage actually dealt to the tracked bosses per second since the phase started.</summary>
    public double BossDps => Elapsed.TotalSeconds > 1 ? (MaxHp - CurrentHp) / Elapsed.TotalSeconds : 0;

    /// <summary>DPS on the tracked bosses needed to kill them before the enrage.</summary>
    public double? RequiredDps => EnrageSeconds is > 0 && MaxHp > 0 ? MaxHp / EnrageSeconds : null;

    /// <summary>Phase time at which the bosses die at the current boss DPS.</summary>
    public TimeSpan? ProjectedKill => BossDps > 0 ? Elapsed + TimeSpan.FromSeconds(CurrentHp / BossDps) : null;

    public TimeSpan? EnrageIn => EnrageSeconds is > 0 ? TimeSpan.FromSeconds(EnrageSeconds.Value) - Elapsed : null;
}

/// <summary>
/// Follows the phases of the supported ultimates: each phase has its own timer, boss HP and enrage,
/// independent from the overall fight timer. Game thread only.
/// </summary>
public sealed class UltimateTracker
{
    // A boss can disappear briefly (jumps, cutscenes): only end the phase if it stays gone that long.
    private static readonly TimeSpan GoneDelay = TimeSpan.FromSeconds(3);

    private readonly Configuration config;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    // Per phase, the name ids of each tracked boss (one set per boss name).
    private readonly Dictionary<string, List<HashSet<uint>>> nameIdsByPhase = new();

    private bool wasInCombat;
    private int phaseIndex;
    private DateTime? phaseStartUtc;
    private DateTime lastAliveUtc;
    // Tracked bosses seen targetable during the phase: invisible copies of a boss (same name, never targetable)
    // are common in ultimates and must neither count for the HP nor keep the phase alive.
    private readonly Dictionary<ulong, uint> maxHpByObject = new();

    public UltimateTracker(Configuration config, IObjectTable objectTable, ICondition condition, IClientState clientState,
                           IDataManager dataManager)
    {
        this.config = config;
        this.objectTable = objectTable;
        this.condition = condition;
        this.clientState = clientState;

        // Boss names -> every BNpcName row with that English name (a boss can exist in several fights).
        var names = dataManager.GetExcelSheet<BNpcName>(ClientLanguage.English)
                               .Select(r => (r.RowId, Name: r.Singular.ExtractText()))
                               .Where(r => r.Name.Length > 0)
                               .ToList();
        foreach (var phase in UltimateDefinitions.All.SelectMany(f => f.Phases))
        {
            nameIdsByPhase[phase.Key] = phase.TrackedBosses
                .Select(boss => names.Where(n => n.Name.Equals(boss, StringComparison.OrdinalIgnoreCase))
                                     .Select(n => n.RowId)
                                     .ToHashSet())
                .ToList();
        }
    }

    /// <summary>The current phase, or null outside a supported ultimate (or when the mode is off).</summary>
    public PhaseView? Current { get; private set; }

    /// <summary>Phase time when the last pull ended before clearing the phase (e.g. a wipe to the enrage).</summary>
    public Dictionary<string, TimeSpan> LastWipes { get; } = new();

    /// <summary>Phase time of the last clear of each phase.</summary>
    public Dictionary<string, TimeSpan> LastClears { get; } = new();

    public static UltimateFight? FightFor(uint territory) =>
        UltimateDefinitions.All.FirstOrDefault(f => f.TerritoryId == territory);

    public void Update()
    {
        var fight = config.UltimateMode ? FightFor(clientState.TerritoryType) : null;
        if (fight is null)
        {
            Current = null;
            wasInCombat = false;
            return;
        }

        var now = DateTime.UtcNow;
        var inCombat = condition[ConditionFlag.InCombat];

        if (inCombat && !wasInCombat)
        {
            phaseIndex = 0;
            ResetPhase();
        }
        else if (!inCombat && wasInCombat && phaseStartUtc is { } start && phaseIndex < fight.Phases.Count)
        {
            // The pull ended during this phase.
            LastWipes[fight.Phases[phaseIndex].Key] = now - start;
        }

        wasInCombat = inCombat;

        if (!inCombat)
        {
            // Keep showing where the last pull ended.
            if (Current is not null)
                Current = Current with { InCombat = false };
            return;
        }

        if (phaseIndex >= fight.Phases.Count)
        {
            Current = new PhaseView(fight, null, phaseIndex + 1, false, TimeSpan.Zero, 0, 0, null, true);
            return;
        }

        var phase = fight.Phases[phaseIndex];
        var npcs = objectTable.OfType<IBattleNpc>().ToList();

        if (phaseStartUtc is null && HasStarted(phase, npcs))
        {
            phaseStartUtc = now;
            lastAliveUtc = now;
        }

        // Only the bosses of this phase that have been targetable count (see maxHpByObject).
        var ids = nameIdsByPhase[phase.Key].SelectMany(set => set).ToHashSet();
        foreach (var b in npcs.Where(n => ids.Contains(n.NameId) && n.IsTargetable && n.MaxHp > 0))
            maxHpByObject[b.GameObjectId] = Math.Max(maxHpByObject.GetValueOrDefault(b.GameObjectId), b.MaxHp);

        var bosses = npcs.Where(n => maxHpByObject.ContainsKey(n.GameObjectId)).ToList();
        var alive = bosses.Where(b => !b.IsDead && b.CurrentHp > 0).ToList();

        if (phaseStartUtc is { } phaseStart)
        {
            if (alive.Count > 0)
                lastAliveUtc = now;

            // The phase ends when the next one starts, or when its bosses are dead (or gone for good).
            var next = phaseIndex + 1 < fight.Phases.Count ? fight.Phases[phaseIndex + 1] : null;
            var nextStarted = next is not null && HasStarted(next, npcs);
            var bossesGone = alive.Count == 0 && (bosses.Count > 0 || now - lastAliveUtc > GoneDelay);
            if (nextStarted || bossesGone)
            {
                LastClears[phase.Key] = (nextStarted ? now : lastAliveUtc) - phaseStart;
                phaseIndex++;
                ResetPhase();
                if (nextStarted)
                {
                    // The next phase's bosses are already up: its timer starts now.
                    phaseStartUtc = now;
                    lastAliveUtc = now;
                }

                return;
            }
        }

        var maxHp = maxHpByObject.Values.Sum(v => (double)v);
        var currentHp = alive.Sum(b => (double)b.CurrentHp);
        double? enrage = config.UltimateEnrageSeconds.TryGetValue(phase.Key, out var e) && e > 0 ? e : null;

        Current = new PhaseView(
            fight, phase, phaseIndex + 1, phaseStartUtc is not null,
            phaseStartUtc is { } s ? now - s : TimeSpan.Zero, currentHp, maxHp, enrage, true);
    }

    /// <summary>Whether a phase's bosses are up: the first one, or all of them for a multi-boss phase.</summary>
    private bool HasStarted(UltimatePhase phase, List<IBattleNpc> npcs)
    {
        bool Up(HashSet<uint> ids) => npcs.Any(n => ids.Contains(n.NameId) && n.IsTargetable && !n.IsDead && n.CurrentHp > 0);

        var bosses = nameIdsByPhase[phase.Key];
        return phase.StartWhenAllTargetable ? bosses.All(Up) : bosses.Any(Up);
    }

    private void ResetPhase()
    {
        phaseStartUtc = null;
        maxHpByObject.Clear();
    }
}
