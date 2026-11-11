using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace DpsInServerBar.Tracking;

/// <summary>A time your DoT was off the boss while it could be attacked (only the attackable time counts).</summary>
public sealed record DotDrop(double AtSeconds, double Seconds);

/// <summary>Uptime of one of your DoTs on the boss in a pull.</summary>
/// <param name="Name">Name of the DoT (its highest version seen).</param>
/// <param name="Icon">Icon of that status.</param>
/// <param name="ActiveSeconds">Time the DoT was on the boss while it could be attacked.</param>
/// <param name="AvailableSeconds">Time the boss could be attacked.</param>
/// <param name="Drops">Times the DoT was off the boss for a second or more (the start of the pull included).</param>
public sealed record DotUptime(string Name, uint Icon, double ActiveSeconds, double AvailableSeconds, DotDrop[]? Drops = null)
{
    public double Uptime => AvailableSeconds > 0 ? Math.Min(1, ActiveSeconds / AvailableSeconds) : 0;
}

/// <summary>
/// Measures how long your DoTs stay on the boss, over the time the boss can be attacked (jumps, transitions and
/// cutscenes left out, as for the GCD uptime). Game thread only.
/// </summary>
public sealed class DotTracker
{
    // Per job (ClassJob row id): each DoT as the statuses of all its versions (levels), lowest first.
    private static readonly Dictionary<uint, uint[][]> Dots = new()
    {
        [24] = [[143, 144, 1871]],                      // WHM: Aero, Aero II, Dia
        [28] = [[179, 189, 1895]],                      // SCH: Bio, Bio II, Biolysis
        [33] = [[838, 843, 1881]],                      // AST: Combust I-III
        [40] = [[2614, 2615, 2616]],                    // SGE: Eukrasian Dosis I-III
        [23] = [[124, 1200], [129, 1201]],              // BRD: Caustic Bite, Stormbite
        [25] = [[161, 162, 163, 1210, 3871, 3872]],     // BLM: Thunder I-IV, High Thunder I-II
        [34] = [[1228]],                                // SAM: Higanbana
        [22] = [[118, 2719]],                           // DRG: Chaos Thrust, Chaotic Spring
        [39] = [[2586]],                                // RPR: Death's Design
    };

    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly Func<bool> enemyTargetable;
    private readonly Func<uint> bossId;

    private uint[][] dots = [];
    private double available;
    private double[] active = [];
    private uint[] seen = [];

    // Per DoT: the drop in progress (start, attackable time off so far), and the drops done.
    private double[] offSince = [];
    private double[] offSeconds = [];
    private List<DotDrop>[] drops = [];
    private DateTime combatStartUtc;
    private const double MinDropSeconds = 1;
    private bool wasInCombat;
    private DateTime lastFrameUtc;
    private int revision;
    private int publishedRevision = -1;
    private IReadOnlyList<DotUptime> published = [];

    public DotTracker(IObjectTable objectTable, ICondition condition, IDataManager dataManager, Func<bool> enemyTargetable, Func<uint> bossId)
    {
        this.objectTable = objectTable;
        this.condition = condition;
        this.dataManager = dataManager;
        this.enemyTargetable = enemyTargetable;
        this.bossId = bossId;
    }

    /// <summary>Whether a job (ClassJob row id) has a DoT to keep up.</summary>
    public static bool Tracks(uint classJobId) => Dots.ContainsKey(classJobId);

    /// <summary>Uptime of each of your DoTs in the fight in progress, or in the last one; empty for jobs without.</summary>
    public IReadOnlyList<DotUptime> Current
    {
        get
        {
            if (publishedRevision != revision)
            {
                publishedRevision = revision;
                published = dots.Select((versions, i) => Describe(seen[i] != 0 ? seen[i] : versions[^1], active[i], Drops(i))).ToList();
            }

            return published;
        }
    }

    /// <summary>Called every frame, after the boss and the GCD uptime are updated.</summary>
    public void Update()
    {
        var now = DateTime.UtcNow;
        var inCombat = condition[ConditionFlag.InCombat];
        var player = objectTable.LocalPlayer;
        if (inCombat && !wasInCombat)
        {
            dots = player is not null && Dots.TryGetValue(player.ClassJob.RowId, out var list) ? list : [];
            active = new double[dots.Length];
            seen = new uint[dots.Length];
            offSince = Enumerable.Repeat(0.0, dots.Length).ToArray();
            offSeconds = new double[dots.Length];
            drops = dots.Select(_ => new List<DotDrop>()).ToArray();
            combatStartUtc = now;
            available = 0;
            lastFrameUtc = now;
            revision++;
        }

        wasInCombat = inCombat;
        var dt = (now - lastFrameUtc).TotalSeconds;
        lastFrameUtc = now;
        if (!inCombat || dots.Length == 0 || player is null || dt <= 0 || dt > 1 || !enemyTargetable())
            return;

        if (objectTable.SearchByEntityId(bossId()) is not IBattleNpc boss || boss.IsDead || !boss.IsTargetable)
            return;

        available += dt;
        var at = (now - combatStartUtc).TotalSeconds;
        for (var i = 0; i < dots.Length; i++)
        {
            var on = false;
            foreach (var status in boss.StatusList)
            {
                if (status.SourceId == player.EntityId && dots[i].Contains(status.StatusId))
                {
                    active[i] += dt;
                    seen[i] = status.StatusId;
                    on = true;
                    break;
                }
            }

            if (!on)
            {
                if (offSeconds[i] == 0)
                    offSince[i] = at;
                offSeconds[i] += dt;
            }
            else if (offSeconds[i] > 0)
            {
                if (offSeconds[i] >= MinDropSeconds)
                    drops[i].Add(new DotDrop(offSince[i], offSeconds[i]));
                offSeconds[i] = 0;
            }
        }

        revision++;
    }

    // The drops of a DoT, with the one in progress.
    private DotDrop[] Drops(int i) =>
        offSeconds[i] >= MinDropSeconds ? [.. drops[i], new DotDrop(offSince[i], offSeconds[i])] : [.. drops[i]];

    private DotUptime Describe(uint statusId, double activeSeconds, DotDrop[] dotDrops)
    {
        var (name, icon) = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>().TryGetRow(statusId, out var row)
            ? (row.Name.ExtractText(), row.Icon)
            : ($"#{statusId}", 0u);
        return new DotUptime(name, icon, activeSeconds, available, dotDrops);
    }
}
