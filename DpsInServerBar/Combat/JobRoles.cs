using System.Collections.Generic;

namespace DpsInServerBar.Combat;

public enum JobRole
{
    Tank,
    Healer,
    Dps,
}

public static class JobRoles
{
    private static readonly HashSet<string> Tanks = ["PLD", "WAR", "DRK", "GNB", "GLA", "MRD"];
    private static readonly HashSet<string> Healers = ["WHM", "SCH", "AST", "SGE", "CNJ"];

    /// <summary>Role of an English job abbreviation (as sent by ACT, e.g. "DRK").</summary>
    public static JobRole Of(string job)
    {
        var upper = job.ToUpperInvariant();
        return Tanks.Contains(upper) ? JobRole.Tank : Healers.Contains(upper) ? JobRole.Healer : JobRole.Dps;
    }

    public static string Label(JobRole role) => role switch
    {
        JobRole.Tank => "tanks",
        JobRole.Healer => "healers",
        _ => "DPS",
    };
}
