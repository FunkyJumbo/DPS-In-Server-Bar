using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using DpsInServerBar.Combat;
using DpsInServerBar.Native;

namespace DpsInServerBar.Tracking;

public enum PullOutcome
{
    Unknown,
    Kill,
    Wipe,
}

/// <summary>A finished pull, as stored in the history file.</summary>
public sealed class PullRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime StartUtc { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Zone { get; set; } = string.Empty;
    public uint TerritoryId { get; set; }
    public string Source { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public PullOutcome Outcome { get; set; }

    public string Job { get; set; } = string.Empty;
    public double YourDps { get; set; }

    /// <summary>Null when rDPS was not available for this pull.</summary>
    public double? YourRdps { get; set; }

    /// <summary>Estimated FFLogs percentile of YourRdps, null when unknown.</summary>
    public double? Percentile { get; set; }
    /// <summary>GCD uptime (0-1), null for pulls recorded before it existed.</summary>
    public double? Uptime { get; set; }

    public double UptimeLostSeconds { get; set; }

    /// <summary>Where the lost GCD time went (clipping, late, no GCD, dead); null for older pulls.</summary>
    public GcdAnalysis? Gcd { get; set; }

    /// <summary>Mechanics failed by the party (penalty debuffs), oldest first.</summary>
    public List<MechanicMistake> Mistakes { get; set; } = [];

    /// <summary>Deaths of the whole party and their cause, oldest first.</summary>
    public List<PartyDeath> PartyDeaths { get; set; } = [];

    /// <summary>Job gauge lost to overcapping, per gauge that overcapped.</summary>
    public List<GaugeWaste> GaugeWaste { get; set; } = [];

    /// <summary>Your positional actions and whether each positional landed, oldest first.</summary>
    public List<PositionalUse> Positionals { get; set; } = [];

    /// <summary>Your combo errors, oldest first.</summary>
    public List<ComboMistake> ComboMistakes { get; set; } = [];

    /// <summary>Uptime of each of your DoTs on the boss.</summary>
    public List<DotUptime> DotUptimes { get; set; } = [];

    /// <summary>The boss moving from one tank to another, oldest first.</summary>
    public List<TankSwap> TankSwaps { get; set; } = [];

    /// <summary>Healing and overheal of each party member who healed.</summary>
    public List<HealerOverheal> Overheal { get; set; } = [];

    /// <summary>Mitigations and defensive buffs used by the party.</summary>
    public List<MitigationUse> Mitigations { get; set; } = [];

    /// <summary>Uptime of each ultimate phase reached in this pull.</summary>
    public List<PhaseUptime> PhaseUptimes { get; set; } = [];

    /// <summary>Seconds (0-1) an enemy was targetable, then seconds your GCD was rolling, for each second of the pull.</summary>
    public float[] UptimeAvailableBySecond { get; set; } = [];

    public float[] UptimeActiveBySecond { get; set; } = [];

    /// <summary>Boss HP left (0-1) when the pull ended, null for older pulls or when no boss was seen.</summary>
    public double? BossHpLeft { get; set; }

    /// <summary>Left out of the progression view (another group, a test pull...), by the player.</summary>
    public bool ExcludedFromProgression { get; set; }

    public string BossName { get; set; } = string.Empty;

    /// <summary>The last cast an enemy started before the pull ended: how far into the fight it went.</summary>
    public string LastBossCast { get; set; } = string.Empty;

    public double PartyDps { get; set; }
    public int Rank { get; set; }

    /// <summary>Your rank by rDPS (0 when unknown).</summary>
    public int RdpsRank { get; set; }
    public int PartySize { get; set; }
    public int Deaths { get; set; }

    /// <summary>What killed you last in this pull, e.g. "Hydrothermal Missile (Living Liquid, 98,432)".</summary>
    public string KilledBy { get; set; } = string.Empty;

    public List<PullCombatant> Combatants { get; set; } = [];

    /// <summary>Cumulative damage at each whole second of the pull (index = second).</summary>
    public double[] YourDamageTimeline { get; set; } = [];

    /// <inheritdoc cref="YourDamageTimeline"/>
    public double[] PartyDamageTimeline { get; set; } = [];
}

public sealed class PullCombatant
{
    public string Name { get; set; } = string.Empty;
    public string Job { get; set; } = string.Empty;
    public double Dps { get; set; }
    public double? Rdps { get; set; }

    /// <summary>Live diagnostic only, not saved in the history.</summary>
    [JsonIgnore]
    public RdpsDetail? Breakdown { get; set; }
    public double DamagePercent { get; set; }
    public double CritPercent { get; set; }
    public double DirectHitPercent { get; set; }
    public int Deaths { get; set; }
    public bool IsYou { get; set; }
}
