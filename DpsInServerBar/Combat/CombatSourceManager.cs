using System;
using System.Linq;
using DpsInServerBar.Act;
using DpsInServerBar.Native;

namespace DpsInServerBar.Combat;

/// <summary>Owns both data sources, decides which one feeds the display and merges rDPS into ACT data.</summary>
public sealed class CombatSourceManager : IDisposable
{
    private readonly Configuration config;

    // Merging allocates, so cache the result for the last pair of snapshots.
    private CombatSnapshot? mergedFrom;
    private CombatSnapshot? mergedWith;
    private CombatSnapshot? merged;

    public CombatSourceManager(Configuration config, ActClient act, NativeCombatTracker native)
    {
        this.config = config;
        Act = act;
        Native = native;
    }

    public ActClient Act { get; }
    public NativeCombatTracker Native { get; }

    public ICombatSource Current => config.Source switch
    {
        CombatSourceKind.Act => Act,
        CombatSourceKind.Native => Native,
        _ => Act.IsReady ? Act : Native,
    };

    /// <summary>
    /// Latest snapshot of the current source. ACT does not know about buffs, so when it is the source,
    /// the rDPS computed by the built-in tracker (running alongside) is added to ACT's numbers.
    /// </summary>
    public CombatSnapshot? Latest
    {
        get
        {
            var source = Current;
            var snapshot = source.Latest;
            if (snapshot is null || source != Act || !config.ComputeRdps)
                return snapshot;

            var native = Native.Latest;
            // Same fight if both sources saw it start at about the same time (ACT's first hit vs ours).
            if (native is null || !native.HasRdps
                || Math.Abs((native.StartUtc - snapshot.StartUtc).TotalSeconds) > 15)
            {
                return snapshot;
            }

            if (!ReferenceEquals(snapshot, mergedFrom) || !ReferenceEquals(native, mergedWith))
            {
                mergedFrom = snapshot;
                mergedWith = native;
                merged = MergeRdps(snapshot, native);
            }

            return merged;
        }
    }

    /// <summary>Starts/stops each source according to the configuration.</summary>
    public void Apply(bool reconnectAct)
    {
        var wantAct = config.Source != CombatSourceKind.Native;
        if (!wantAct)
            Act.Stop();
        else if (reconnectAct || !Act.IsRunning)
            Act.Start(new Uri(config.WebSocketUrl));

        // The built-in tracker also runs next to ACT when rDPS is wanted (only it sees buffs) or to tell what killed
        // you (only it sees the hits you take).
        Native.SetEnabled(config.Source != CombatSourceKind.Act || config.ComputeRdps
                          || (config.ChatSummary && config.ChatSummaryElements.HasFlag(SummaryElement.KilledBy)));
    }

    public void Tick() => Native.Tick();

    public void Dispose()
    {
        Act.Dispose();
        Native.Dispose();
    }

    /// <summary>Applies each player's rDPS - DPS difference measured by the built-in tracker to ACT's DPS.</summary>
    private static CombatSnapshot MergeRdps(CombatSnapshot act, CombatSnapshot native)
    {
        var combatants = act.Combatants.Select(c =>
        {
            if (c.IsLimitBreak)
                return c with { RDps = c.EncDps };

            var match = native.Combatants.FirstOrDefault(n =>
                n.HasRdps && ((c.IsYou && n.IsYou) || n.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)));

            return match is null ? c : c with { RDps = c.EncDps + (match.RDps - match.EncDps), RdpsBreakdown = match.RdpsBreakdown };
        }).ToList();

        return act with { Combatants = combatants };
    }
}
