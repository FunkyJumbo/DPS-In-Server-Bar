using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;

namespace DpsInServerBar.Native;

/// <summary>A party member using a mitigation or a defensive buff in a pull.</summary>
public sealed record MitigationUse(double AtSeconds, string Player, string Job, bool IsYou, uint ActionId, string Action);

/// <summary>
/// Records when each party member uses a mitigation (on the party or on the boss) or a defensive buff. The actions
/// are found by their English name in the game data, so every level of an action is caught. Game thread only.
/// </summary>
public sealed class MitigationLog
{
    private const int MaxUses = 1000;

    // English names: role actions, then per job (tanks, healers, melee, ranged, casters). oGCDs only: GCD shields
    // (Succor, Eukrasian Prognosis...) would bury the rest.
    private static readonly string[] Names =
    [
        "Rampart", "Reprisal", "Addle", "Feint", "Bloodbath",
        // Tanks
        "Sentinel", "Guardian", "Hallowed Ground", "Sheltron", "Holy Sheltron", "Intervention", "Cover", "Divine Veil", "Passage of Arms", "Bulwark",
        "Vengeance", "Damnation", "Holmgang", "Raw Intuition", "Bloodwhetting", "Nascent Flash", "Thrill of Battle", "Shake It Off",
        "Shadow Wall", "Shadowed Vigil", "Living Dead", "The Blackest Night", "Dark Mind", "Dark Missionary", "Oblation",
        "Nebula", "Great Nebula", "Superbolide", "Heart of Stone", "Heart of Corundum", "Camouflage", "Heart of Light", "Aurora",
        // Healers
        "Temperance", "Divine Benison", "Aquaveil", "Liturgy of the Bell", "Plenary Indulgence", "Asylum", "Divine Caress",
        "Sacred Soil", "Expedient", "Fey Illumination", "Protraction", "Deployment Tactics", "Summon Seraph", "Seraphism", "Excogitation",
        "Collective Unconscious", "Neutral Sect", "Exaltation", "Celestial Intersection", "Macrocosmos", "Sun Sign",
        "Kerachole", "Holos", "Panhaima", "Haima", "Taurochole", "Physis II",
        // Melee
        "Riddle of Earth", "Mantra", "Shade Shift", "Third Eye", "Tengentsu", "Arcane Crest",
        // Physical ranged
        "Troubadour", "Nature's Minne", "Tactician", "Dismantle", "Shield Samba", "Improvisation",
        // Casters
        "Manaward", "Radiant Aegis", "Magick Barrier", "Tempera Coat", "Tempera Grassa",
    ];

    private static Dictionary<uint, string>? actions; // id -> name in the client's language

    private readonly List<MitigationUse> uses = [];
    private bool wasInCombat;
    private DateTime startUtc;
    private int revision;
    private int publishedRevision = -1;
    private IReadOnlyList<MitigationUse> published = [];

    /// <summary>Mitigations used in the fight in progress, or in the last one, oldest first.</summary>
    public IReadOnlyList<MitigationUse> Current
    {
        get
        {
            if (publishedRevision != revision)
            {
                publishedRevision = revision;
                published = uses.ToList();
            }

            return published;
        }
    }

    public void Tick(bool inCombat)
    {
        if (inCombat && !wasInCombat)
        {
            uses.Clear();
            startUtc = DateTime.UtcNow;
            revision++;
        }

        wasInCombat = inCombat;
    }

    /// <summary>A party member (not a pet) used an action (from the action effect hook).</summary>
    public void OnAction(string player, string job, bool isYou, uint actionId)
    {
        if (!wasInCombat || uses.Count >= MaxUses || !Actions.TryGetValue(actionId, out var name))
            return;

        uses.Add(new MitigationUse((DateTime.UtcNow - startUtc).TotalSeconds, player, job.ToUpperInvariant(), isYou, actionId, name));
        revision++;
    }

    private static Dictionary<uint, string> Actions
    {
        get
        {
            if (actions is not null)
                return actions;

            var wanted = Names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var local = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            actions = new Dictionary<uint, string>();
            foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>(ClientLanguage.English))
            {
                if (row.IsPvP || !(row.IsPlayerAction || row.IsRoleAction) || !wanted.Contains(row.Name.ExtractText()))
                    continue;
                actions[row.RowId] = local.TryGetRow(row.RowId, out var l) ? l.Name.ExtractText() : row.Name.ExtractText();
            }

            return actions;
        }
    }
}
