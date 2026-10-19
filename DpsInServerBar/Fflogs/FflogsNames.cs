using System.Collections.Generic;
using System.Numerics;

namespace DpsInServerBar.Fflogs;

public static class FflogsNames
{
    /// <summary>English job abbreviation (as sent by ACT) to FFLogs class/spec name.</summary>
    private static readonly Dictionary<string, string> Specs = new()
    {
        ["PLD"] = "Paladin", ["WAR"] = "Warrior", ["DRK"] = "DarkKnight", ["GNB"] = "Gunbreaker",
        ["WHM"] = "WhiteMage", ["SCH"] = "Scholar", ["AST"] = "Astrologian", ["SGE"] = "Sage",
        ["MNK"] = "Monk", ["DRG"] = "Dragoon", ["NIN"] = "Ninja", ["SAM"] = "Samurai", ["RPR"] = "Reaper",
        ["VPR"] = "Viper", ["BRD"] = "Bard", ["MCH"] = "Machinist", ["DNC"] = "Dancer",
        ["BLM"] = "BlackMage", ["SMN"] = "Summoner", ["RDM"] = "RedMage", ["PCT"] = "Pictomancer",
        ["BLU"] = "BlueMage",
    };

    public static string? Spec(string job) => Specs.GetValueOrDefault(job.ToUpperInvariant());

    /// <summary>FFLogs parse colors: grey, green, blue, purple, orange, pink, gold.</summary>
    public static Vector4 Color(double percentile) => percentile switch
    {
        >= 100 => Rgb(0xE5, 0xCC, 0x80),
        >= 99 => Rgb(0xE2, 0x68, 0xA8),
        >= 95 => Rgb(0xFF, 0x80, 0x00),
        >= 75 => Rgb(0xA3, 0x35, 0xEE),
        >= 50 => Rgb(0x00, 0x70, 0xFF),
        >= 25 => Rgb(0x1E, 0xFF, 0x00),
        _ => Rgb(0x99, 0x99, 0x99),
    };

    private static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);
}
