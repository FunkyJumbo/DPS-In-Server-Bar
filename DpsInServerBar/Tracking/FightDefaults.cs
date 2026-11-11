using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Tracking;

/// <summary>Built-in DPS check of a fight: the boss HP and the time from pull to enrage.</summary>
/// <param name="Boss">English name of the boss (FFLogs), to tell apart the two fights of a zone (door boss and final boss).</param>
/// <param name="BossBaseId">BNpcBase id of the boss, the surest way to tell them apart.</param>
/// <param name="EnrageSeconds">0 when unknown.</param>
public sealed record FightDefault(uint TerritoryId, string Boss, uint BossBaseId, double BossHp, double EnrageSeconds);

/// <summary>
/// Boss HP and enrage times of the Stormblood to Dawntrail extremes and savage raids, read from FFLogs: the HP from
/// recent logs (current game values), the enrage from the wipes that ended together at the end of the fight in the
/// first weeks after release. Used when the player has not set a check of their own for the fight.
/// </summary>
public static class FightDefaults
{
    public static readonly IReadOnlyList<FightDefault> All =
    [
        // Territory, boss (FFLogs), BNpcBase, HP, enrage (s).
        new(677, "Susano", 7068, 3203623, 0),
        new(720, "Lakshmi", 7310, 2942308, 0),
        new(730, "Shinryu", 8026, 6178624, 0),
        new(758, "Byakko", 8435, 3709234, 0),
        new(779, "Tsukuyomi", 8721, 4551174, 0),
        new(811, "Suzaku", 9316, 5722275, 0),
        new(825, "Seiryu", 9708, 5477083, 683),
        new(695, "Alte Roite", 6768, 4118112, 591),
        new(696, "Catastrophe", 7305, 4738147, 635),
        new(697, "Halicarnassus", 7396, 4872947, 702),
        new(698, "Exdeath", 7455, 5325265, 304),
        new(698, "Neo Exdeath", 7459, 6023895, 0),
        new(752, "Phantom Train", 8513, 4267243, 632),
        new(753, "Demon Chadarnook", 8459, 5943060, 636),
        new(754, "Guardian", 8401, 6060170, 745),
        new(755, "Kefka", 8528, 9926597, 420),
        new(755, "God Kefka", 8529, 6655574, 657),
        new(802, "Chaos", 9273, 6071366, 601),
        new(803, "Midgardsormr", 9294, 5912607, 650),
        new(804, "Omega", 9329, 6609670, 685),
        new(805, "Omega-M and Omega-F", 9341, 2634418, 478),
        new(805, "The Final Omega", 9343, 7732853, 0),
        new(858, "Titania", 10211, 8116506, 683),
        new(848, "Innocence", 10499, 9239174, 694),
        new(935, "The Emerald Weapon I", 12353, 6868765, 347),
        new(935, "The Emerald Weapon II", 12628, 7061710, 382),
        new(906, "Ramuh", 11528, 13843394, 0),
        new(907, "Ifrit and Garuda", 11539, 6520866, 627),
        new(908, "The Idol of Darkness", 11570, 12259279, 716),
        new(909, "Shiva", 11627, 17124316, 845),
        new(946, "Cloud of Darkness", 12379, 13288036, 0),
        new(947, "Shadowkeeper", 12393, 17103400, 684),
        new(948, "Fatebreaker", 12407, 16210388, 696),
        new(949, "Eden's Promise", 12441, 13949684, 518),
        new(949, "Oracle of Darkness", 12450, 13631666, 541),
        new(993, "Zodiark", 12877, 19252824, 724),
        new(996, "Hydaelyn", 13549, 17785784, 692),
        new(1072, "Barbariccia", 14798, 29293744, 680),
        new(1141, "Golbez", 16215, 37255204, 667),
        new(1003, "Erichthonios", 13602, 23796496, 603),
        new(1005, "Hippokampos", 13723, 26300152, 632),
        new(1007, "Phoinix", 13631, 24508564, 663),
        new(1009, "Hesperos", 13821, 41780192, 443),
        new(1009, "Hesperos II", 13824, 24237992, 0),
        new(1082, "Proto-Carbuncle", 14826, 29844576, 0),
        new(1086, "Agdistis", 14893, 39591396, 680),
        new(1088, "Hephaistos", 15049, 55874848, 478),
        new(1088, "Hephaistos II", 15077, 32885224, 0),
        new(1148, "Kokytos", 16087, 36660084, 0),
        new(1150, "Pandaemonium", 16157, 43656896, 639),
        new(1152, "Themis", 16114, 49884204, 681),
        new(1154, "Athena", 16171, 32790420, 476),
        new(1154, "Pallas Athena", 16181, 40192744, 0),
        new(1196, "Valigarmanda", 16664, 61841486, 690),
        new(1201, "Zoraal Ja", 17077, 66146024, 668),
        new(1271, "Zelenia", 18374, 85834672, 659),
        new(1308, "Doomtrain", 18999, 98895160, 697),
        new(1257, "Dancing Green", 18361, 105549682, 613),
        new(1259, "Sugar Riot", 18335, 93010785, 690),
        new(1261, "Brute Abombinator", 18307, 118583808, 677),
        new(1263, "Howling Blade", 18215, 67582753, 0),
        new(1321, "Vamp Fatale", 19167, 110594239, 611),
        new(1323, "Red Hot and Deep Blue", 19287, 59491502, 606),
        new(1325, "The Tyrant", 19180, 154287371, 668),
        new(1327, "Lindwurm", 19195, 104593118, 451),
        new(1327, "Lindwurm II", 19202, 131936161, 560),
        new(885, "Hades", 11292, 3458073, 0),
        new(912, "The Ruby Weapon I", 11490, 6388109, 462),
        new(912, "The Ruby Weapon II", 11494, 3648884, 295),
        new(913, "Varis Yae Galvus", 11589, 10733219, 767),
        new(923, "Warrior of Light", 11725, 11983521, 749),
        new(951, "The Diamond Weapon", 13023, 13792169, 725),
        new(853, "Eden Prime", 9365, 8287681, 0),
        new(854, "Voidwalker", 10511, 12300259, 633),
        new(855, "Leviathan", 10604, 12736947, 0),
        new(856, "Titan", 10639, 15394639, 817),
        new(998, "Endsinger", 14527, 26549964, 694),
        new(1096, "Rubicante", 15756, 30021728, 681),
        new(1169, "Zeromus", 16557, 40478540, 680),
        new(1243, "Queen Eternal", 18039, 65341983, 0),
        new(1296, "Necron", 18699, 92718920, 0),
        new(1306, "Guardian Arkveld", 18661, 99490188, 707),
        new(1362, "Enuo", 19905, 107935443, 683),
        new(1226, "Black Cat", 17193, 75867953, 607),
        new(1228, "Honey B. Lovely", 16941, 83966736, 615),
        new(1230, "Brute Bomber", 17094, 96522580, 0),
        new(1232, "Wicked Thunder", 17322, 114525943, 0),
    ];

    private static Dictionary<string, string>? localNames;

    /// <summary>The fights of a zone (two when it has a door boss and a final boss).</summary>
    public static IEnumerable<FightDefault> In(uint territory) => All.Where(d => d.TerritoryId == territory);

    /// <summary>
    /// The check for this zone and encounter: between two fights of a zone, the boss being fought decides, else the boss
    /// name (in the client's language) in the encounter title, else the final boss.
    /// </summary>
    public static FightDefault? Find(uint territory, string encounterTitle, uint bossBaseId = 0)
    {
        var fights = In(territory).ToList();
        if (fights.Count <= 1)
            return fights.FirstOrDefault();
        if (bossBaseId != 0 && fights.FirstOrDefault(f => f.BossBaseId == bossBaseId) is { } fought)
            return fought;

        // Longest names first: "Neo Exdeath" before "Exdeath".
        return fights.OrderByDescending(f => LocalName(f.Boss).Length)
                     .FirstOrDefault(f => encounterTitle.Contains(LocalName(f.Boss), StringComparison.OrdinalIgnoreCase)
                                          || encounterTitle.Contains(f.Boss, StringComparison.OrdinalIgnoreCase))
               ?? fights[^1];
    }

    /// <summary>As a check (not saved): what the plugin uses when the player has none for this fight.</summary>
    public static PartyDpsThreshold? Threshold(uint territory, string encounterTitle, uint bossBaseId = 0) =>
        Find(territory, encounterTitle, bossBaseId) is { } d
            ? new PartyDpsThreshold { TerritoryId = territory, Label = d.Boss, BossHp = d.BossHp, EnrageSeconds = d.EnrageSeconds, IsBuiltIn = true }
            : null;

    /// <summary>A boss name in the client's language (the English one when not found).</summary>
    public static string LocalName(string english)
    {
        if (localNames is null)
        {
            localNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var wanted = All.Select(d => d.Boss).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var local = Plugin.DataManager.GetExcelSheet<BNpcName>();
            foreach (var row in Plugin.DataManager.GetExcelSheet<BNpcName>(ClientLanguage.English))
            {
                var name = row.Singular.ExtractText();
                if (wanted.Contains(name) && !localNames.ContainsKey(name) && local.TryGetRow(row.RowId, out var l)
                    && l.Singular.ExtractText() is { Length: > 0 } localName)
                {
                    localNames[name] = localName;
                }
            }
        }

        return localNames.GetValueOrDefault(english, english);
    }
}
