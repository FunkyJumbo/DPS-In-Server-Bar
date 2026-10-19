using System.Collections.Generic;

namespace DpsInServerBar.Rdps;

public enum BuffTarget
{
    /// <summary>The status sits on the attacker (party buffs, cards, dance partner).</summary>
    Attacker,

    /// <summary>The status sits on the enemy (Chain Stratagem, Dokumori).</summary>
    Enemy,
}

/// <summary>A raid buff whose value is known, keyed by status id.</summary>
public sealed record RaidBuff(string Name, BuffTarget On, double DamagePercent = 0, double CritRate = 0, double DirectHitRate = 0)
{
    /// <summary>
    /// Value for this hit. Cards depend on the attacker's role; Radiant Finale on the number of Codas
    /// (exposed as the status param).
    /// </summary>
    public (double Damage, double Crit, double DirectHit) Resolve(string attackerJob, ushort param)
    {
        var damage = DamagePercent;

        if (Name == "Radiant Finale" && param is >= 1 and <= 3)
            damage = 2 * param;
        else if (Name == "The Balance" && !RaidBuffs.IsMeleeOrTank(attackerJob))
            damage /= 2;
        else if (Name == "The Spear" && RaidBuffs.IsMeleeOrTank(attackerJob))
            damage /= 2;

        return (damage, CritRate, DirectHitRate);
    }
}

/// <summary>
/// Dawntrail (7.x) raid buffs. Status ids come from the game's Status sheet; values are not in the game data
/// and must be kept up to date when a patch changes them.
/// </summary>
public static class RaidBuffs
{
    public static readonly IReadOnlyDictionary<uint, RaidBuff> ByStatusId = new Dictionary<uint, RaidBuff>
    {
        // % damage
        [1878] = new("Divination", BuffTarget.Attacker, DamagePercent: 6),
        [1185] = new("Brotherhood", BuffTarget.Attacker, DamagePercent: 5),
        [1297] = new("Embolden", BuffTarget.Attacker, DamagePercent: 5),
        [2703] = new("Searing Light", BuffTarget.Attacker, DamagePercent: 5),
        [2599] = new("Arcane Circle", BuffTarget.Attacker, DamagePercent: 3),
        [1822] = new("Technical Finish", BuffTarget.Attacker, DamagePercent: 5),
        [1821] = new("Standard Finish", BuffTarget.Attacker, DamagePercent: 5),
        [2105] = new("Standard Finish", BuffTarget.Attacker, DamagePercent: 5),
        [2964] = new("Radiant Finale", BuffTarget.Attacker, DamagePercent: 6),
        [3685] = new("Starry Muse", BuffTarget.Attacker, DamagePercent: 5),
        [3887] = new("The Balance", BuffTarget.Attacker, DamagePercent: 6),
        [3889] = new("The Spear", BuffTarget.Attacker, DamagePercent: 6),
        [3849] = new("Dokumori", BuffTarget.Enemy, DamagePercent: 5),

        // crit / direct hit rate
        [786] = new("Battle Litany", BuffTarget.Attacker, CritRate: 0.10),
        [141] = new("Battle Voice", BuffTarget.Attacker, DirectHitRate: 0.20),
        [1825] = new("Devilment", BuffTarget.Attacker, CritRate: 0.20, DirectHitRate: 0.20),
        [1221] = new("Chain Stratagem", BuffTarget.Enemy, CritRate: 0.10),
    };

    /// <summary>Self statuses making the next hit a guaranteed crit and/or direct hit.</summary>
    public static readonly IReadOnlyDictionary<uint, (bool Crit, bool DirectHit)> AutoHitStatuses =
        new Dictionary<uint, (bool, bool)>
        {
            [1177] = (true, true), // Inner Release (Fell Cleave / Decimate)
            [851] = (true, true),  // Reassembled
            [116] = (true, false), // Life Surge
        };

    /// <summary>Actions that always crit and direct hit.</summary>
    public static readonly HashSet<uint> AutoCritDirectHitActions =
    [
        16465, // Inner Chaos
        16463, // Chaotic Cyclone
        25753, // Primal Rend
        36925, // Primal Ruination
    ];

    private static readonly HashSet<string> MeleeOrTank =
    [
        "PLD", "WAR", "DRK", "GNB", "GLA", "MRD",
        "MNK", "DRG", "NIN", "SAM", "RPR", "VPR", "PGL", "LNC", "ROG",
    ];

    public static bool IsMeleeOrTank(string job) => MeleeOrTank.Contains(job.ToUpperInvariant());
}
