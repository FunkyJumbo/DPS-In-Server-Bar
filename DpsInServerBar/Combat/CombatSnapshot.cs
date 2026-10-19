using System;
using System.Collections.Generic;
using System.Linq;

namespace DpsInServerBar.Combat;

/// <summary>State of the current encounter, from any source. Immutable, safe to share across threads.</summary>
public sealed record CombatSnapshot(
    EncounterInfo Encounter,
    IReadOnlyList<CombatantInfo> Combatants,
    bool IsActive,
    DateTime ReceivedAtUtc)
{
    // Computed on access rather than stored: a stored value would be copied unchanged by `with { Combatants = ... }`.
    public CombatantInfo? You => Combatants.FirstOrDefault(c => c.IsYou);

    /// <summary>When the encounter started; used to tell whether two sources are describing the same fight.</summary>
    public DateTime StartUtc { get; init; } = ReceivedAtUtc - Encounter.Duration;

    /// <summary>Sum of the party members' DPS (players with a job), optionally including the Limit Break.</summary>
    public double PartyDps(bool includeLimitBreak) =>
        Combatants.Where(c => c.HasJob || (includeLimitBreak && c.IsLimitBreak)).Sum(c => c.EncDps);

    public bool HasRdps => Combatants.Any(c => c.HasJob && c.HasRdps);

    /// <summary>
    /// Your place among players of your role (tanks, healers or DPS), and the gap to the reference player:
    /// the first one, or the second one when you are first (+8% = 8% ahead of #2). Null when you are unknown.
    /// </summary>
    public RoleStanding? YourRoleStanding(bool byRdps)
    {
        if (You is not { HasJob: true } you)
            return null;

        double Value(CombatantInfo c) => byRdps && c.HasRdps ? c.RDps : c.EncDps;

        var role = JobRoles.Of(you.Job);
        var peers = Combatants.Where(c => c.HasJob && JobRoles.Of(c.Job) == role).OrderByDescending(Value).ToList();
        var rank = peers.FindIndex(c => c.IsYou) + 1;
        if (peers.Count < 2)
            return new RoleStanding(role, rank, peers.Count, null);

        var reference = Value(rank == 1 ? peers[1] : peers[0]);
        return new RoleStanding(role, rank, peers.Count, reference > 0 ? Value(you) / reference - 1 : null);
    }

    /// <summary>1-based position of the player in the DPS (or rDPS) ranking among party members, or 0 if unknown.</summary>
    public int YourRank(bool byRdps = false)
    {
        if (You is null)
            return 0;

        var ranked = Combatants.Where(c => c.HasJob)
            .OrderByDescending(c => byRdps && c.HasRdps ? c.RDps : c.EncDps)
            .ToList();
        return ranked.IndexOf(You) + 1;
    }
}

public sealed record EncounterInfo(
    string Title,
    string Zone,
    TimeSpan Duration,
    double EncDps,
    double TotalDamage);

public sealed record CombatantInfo(
    string Name,
    string Job,
    double EncDps,

    /// <summary>Raid-contributed DPS (FFLogs-style), NaN when the source cannot compute it.</summary>
    double RDps,
    double EncHps,
    double Damage,
    double DamagePercent,
    double CritPercent,
    double DirectHitPercent,
    double CritDirectHitPercent,
    int Deaths,
    bool IsYou,
    RdpsDetail? RdpsBreakdown = null)
{
    public bool HasJob => Job.Length > 0;
    public bool IsLimitBreak => Name.Equals("Limit Break", StringComparison.OrdinalIgnoreCase);
    public bool HasRdps => !double.IsNaN(RDps);
}

/// <summary>rDPS breakdown for one player: DPS given to and received from buffs, and the rates used.</summary>
public sealed record RdpsDetail(double GivenDps, double ReceivedDps, double CritRate, double DirectHitRate, int Samples);

/// <summary>Your rank within your role and the relative gap to the reference player (see YourRoleStanding).</summary>
public sealed record RoleStanding(JobRole Role, int Rank, int Count, double? Gap);
