using System.Collections.Generic;
using Dalamud.Game.Text.SeStringHandling;

namespace DpsInServerBar.Display;

/// <summary>Maps English job abbreviations (as sent by ACT, e.g. "DRK") to the game's inline job icons.</summary>
public static class JobIcons
{
    private static readonly Dictionary<string, BitmapFontIcon> Icons = new()
    {
        ["GLA"] = BitmapFontIcon.Gladiator,
        ["PGL"] = BitmapFontIcon.Pugilist,
        ["MRD"] = BitmapFontIcon.Marauder,
        ["LNC"] = BitmapFontIcon.Lancer,
        ["ARC"] = BitmapFontIcon.Archer,
        ["CNJ"] = BitmapFontIcon.Conjurer,
        ["THM"] = BitmapFontIcon.Thaumaturge,
        ["ACN"] = BitmapFontIcon.Arcanist,
        ["ROG"] = BitmapFontIcon.Rogue,
        ["PLD"] = BitmapFontIcon.Paladin,
        ["MNK"] = BitmapFontIcon.Monk,
        ["WAR"] = BitmapFontIcon.Warrior,
        ["DRG"] = BitmapFontIcon.Dragoon,
        ["BRD"] = BitmapFontIcon.Bard,
        ["WHM"] = BitmapFontIcon.WhiteMage,
        ["BLM"] = BitmapFontIcon.BlackMage,
        ["SMN"] = BitmapFontIcon.Summoner,
        ["SCH"] = BitmapFontIcon.Scholar,
        ["NIN"] = BitmapFontIcon.Ninja,
        ["MCH"] = BitmapFontIcon.Machinist,
        ["DRK"] = BitmapFontIcon.DarkKnight,
        ["AST"] = BitmapFontIcon.Astrologian,
        ["SAM"] = BitmapFontIcon.Samurai,
        ["RDM"] = BitmapFontIcon.RedMage,
        ["BLU"] = BitmapFontIcon.BlueMage,
        ["GNB"] = BitmapFontIcon.Gunbreaker,
        ["DNC"] = BitmapFontIcon.Dancer,
        ["RPR"] = BitmapFontIcon.Reaper,
        ["SGE"] = BitmapFontIcon.Sage,
        ["VPR"] = BitmapFontIcon.Viper,
        ["PCT"] = BitmapFontIcon.Pictomancer,
        ["BST"] = BitmapFontIcon.Beastmaster,
    };

    public static BitmapFontIcon For(string job) =>
        Icons.TryGetValue(job.ToUpperInvariant(), out var icon) ? icon : BitmapFontIcon.None;
}
