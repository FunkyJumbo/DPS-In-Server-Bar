using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Configuration;
using Newtonsoft.Json;
using DpsInServerBar.Combat;
using DpsInServerBar.Tracking;

namespace DpsInServerBar;

public enum DisplayMetric
{
    Personal,
    Party,

    /// <summary>Your raid-contributed DPS (FFLogs-style rDPS).</summary>
    Raid,
}

public enum NumberStyle
{
    /// <summary>12345</summary>
    Plain,

    /// <summary>12,345</summary>
    Grouped,

    /// <summary>12.3k</summary>
    Compact,
}

[Serializable]
public sealed class PartyDpsThreshold
{
    /// <summary>0 = any zone.</summary>
    public uint TerritoryId { get; set; }

    /// <summary>Optional: only applies when the encounter title contains this text.</summary>
    public string EncounterName { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>Manual minimum, used when the boss HP or the enrage time is not set.</summary>
    public double MinPartyDps { get; set; }

    /// <summary>Total HP to burn before enrage (0 = not set). Can be read from the game.</summary>
    public double BossHp { get; set; }

    /// <summary>Time from pull to enrage, in seconds (0 = not set). Not available in game data.</summary>
    public double EnrageSeconds { get; set; }

    public bool UsesBossHp => BossHp > 0 && EnrageSeconds > 0;

    /// <summary>The minimum party DPS: HP / enrage time when both are known, the manual value otherwise.</summary>
    public double EffectiveMinPartyDps => UsesBossHp ? BossHp / EnrageSeconds : MinPartyDps;
}

/// <summary>A piece of the server bar text.</summary>
public enum BarElement
{
    FinalMarker,
    JobIcon,
    JobName,
    Rank,
    RoleGap,
    Value,
    Pace,
    BossHp,
    KillTime,
    EnrageTimer,
    Phase,
    PhaseDps,
    Uptime,
    Percentile,
}

/// <summary>Values that can appear in the chat summary after each pull.</summary>
[Flags]
public enum SummaryElement
{
    None = 0,
    Job = 1,
    Dps = 2,
    Rdps = 4,
    Percentile = 8,
    Uptime = 16,
    PartyDps = 32,
    Deaths = 64,
    All = Job | Dps | Rdps | Percentile | Uptime | PartyDps | Deaths,
}

[Serializable]
public sealed class BarElementSetting
{
    public BarElement Element { get; set; }
    public bool Enabled { get; set; } = true;
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    /// <summary>Order and visibility of the elements of the server bar text.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<BarElementSetting> BarLayout { get; set; } = DefaultLayout();

    // Data source
    public CombatSourceKind Source { get; set; } = CombatSourceKind.Auto;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 10501;

    // Display
    public DisplayMetric Metric { get; set; } = DisplayMetric.Personal;
    public NumberStyle NumberStyle { get; set; } = NumberStyle.Grouped;
    /// <summary>Text placed between the elements of the server bar (a space by default, e.g. " | ").</summary>
    public string BarSeparator { get; set; } = " ";

    /// <summary>Show the DPS graphs in the main window.</summary>
    public bool ShowGraph { get; set; } = true;

    /// <summary>Kinds of content shown in the History tab (several can be selected).</summary>
    public ContentCategory HistoryCategories { get; set; } = ContentCategory.All;

    /// <summary>Order the player tables of the main window by rDPS instead of DPS.</summary>
    public bool SortByRdps { get; set; } = false;
    public bool IncludeLimitBreakInParty { get; set; } = true;

    /// <summary>Compute rDPS with the built-in tracker (also runs next to ACT for that purpose).</summary>
    public bool ComputeRdps { get; set; } = true;

    /// <summary>Hide the entry this many seconds after the encounter ends (0 = always visible).</summary>
    public int HideAfterSeconds { get; set; } = 0;

    /// <summary>Hide the entry while no source can provide data.</summary>
    public bool HideWhenDisconnected { get; set; } = false;

    // Colors (RGBA, 0-1)
    public bool UseColors { get; set; } = true;
    public Vector4 FinalColor { get; set; } = new(0.65f, 0.65f, 0.65f, 1f);
    public Vector4 BelowThresholdColor { get; set; } = new(1f, 0.35f, 0.35f, 1f);
    public Vector4 AboveThresholdColor { get; set; } = new(0.45f, 0.9f, 0.45f, 1f);

    // Pulls / history
    public bool ChatSummary { get; set; } = true;
    public bool ChatSummaryInDungeons { get; set; } = true;

    /// <summary>In ultimates, also a summary at the end of each phase.</summary>
    public bool ChatSummaryPhases { get; set; } = true;

    /// <summary>Values shown in the chat summary.</summary>
    public SummaryElement ChatSummaryElements { get; set; } = SummaryElement.All;

    /// <summary>After DPS, rDPS, GCD and pDPS: "NEW BEST" or the gap to your best kill of the fight.</summary>
    public bool ChatSummaryCompareBest { get; set; } = true;
    public bool RecordOnlyInDuties { get; set; } = true;
    public int MinPullSeconds { get; set; } = 20;
    public int MaxHistory { get; set; } = 300;
    public int GraphWindowSeconds { get; set; } = 10;

    // Party DPS checks. Replace (not merge into) the defaults when loading, or they would be duplicated.
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<PartyDpsThreshold> Thresholds { get; set; } =
    [
        new() { TerritoryId = 1321, Label = "AAC Heavyweight M1 (Savage)", MinPartyDps = 182800 },
        new() { TerritoryId = 1323, Label = "AAC Heavyweight M2 (Savage)", MinPartyDps = 192000 },
    ];

    // Ultimates: per-phase tracking (boss HP, timer, enrage) in the supported ultimates.
    public bool UltimateMode { get; set; } = true;

    /// <summary>Enrage time of each ultimate phase, in seconds from the phase start, keyed by phase key (e.g. "TEA:P1").</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, double> UltimateEnrageSeconds { get; set; } = new();

    // FFLogs percentile estimate (API v2 client credentials, entered by the user in the settings)
    public bool ShowPercentile { get; set; } = true;
    public string FflogsClientId { get; set; } = string.Empty;
    public string FflogsClientSecret { get; set; } = string.Empty;

    /// <summary>Territory id to FFLogs encounter id, when the automatic match by boss name fails.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<uint, int> FflogsEncounterOverrides { get; set; } = new();

    // Pre-v3 display toggles, only read once to build BarLayout.
    public bool? ShowJob { get; set; }
    public bool? ShowJobIcon { get; set; }
    public bool? ShowRank { get; set; }
    public bool? ShowFinalMarker { get; set; }
    public bool? ShowPaceVsBest { get; set; }

    public string WebSocketUrl => $"ws://{Host}:{Port}/ws";

    public bool IsShown(BarElement element) => BarLayout.Any(e => e.Element == element && e.Enabled);

    /// <summary>Upgrades older configurations and repairs the layout. Returns true if something changed.</summary>
    public bool Migrate()
    {
        var changed = false;

        if (Version < 3)
        {
            BarLayout = DefaultLayout();
            SetShown(BarElement.JobName, ShowJob ?? true);
            SetShown(BarElement.JobIcon, ShowJobIcon ?? true);
            SetShown(BarElement.Rank, ShowRank ?? true);
            SetShown(BarElement.FinalMarker, ShowFinalMarker ?? true);
            SetShown(BarElement.Pace, ShowPaceVsBest ?? false);
            ShowJob = ShowJobIcon = ShowRank = ShowFinalMarker = ShowPaceVsBest = null;

            // Older versions appended the default checks to the saved ones on every load.
            Thresholds = Thresholds
                .GroupBy(t => (t.TerritoryId, t.EncounterName, t.Label, t.MinPartyDps))
                .Select(g => g.First())
                .ToList();

            Version = 3;
            changed = true;
        }

        if (Version < 4)
        {
            // The default AAC checks pointed at the Abyssos zones (1087/1088) instead of AAC Heavyweight M1/M2 Savage.
            foreach (var t in Thresholds)
            {
                if (t.TerritoryId == 1087 && t.Label == "AAC Heavyweight M1 (Savage)")
                    t.TerritoryId = 1321;
                else if (t.TerritoryId == 1088 && t.Label == "AAC Heavyweight M2 (Savage)")
                    t.TerritoryId = 1323;
            }

            Version = 4;
            changed = true;
        }

        // Elements added in later versions show up, hidden, at the end.
        foreach (var element in Enum.GetValues<BarElement>())
        {
            if (BarLayout.All(e => e.Element != element))
            {
                BarLayout.Add(new BarElementSetting { Element = element, Enabled = false });
                changed = true;
            }
        }

        return changed;
    }

    private void SetShown(BarElement element, bool shown)
    {
        foreach (var e in BarLayout.Where(e => e.Element == element))
            e.Enabled = shown;
    }

    private static List<BarElementSetting> DefaultLayout() =>
    [
        new() { Element = BarElement.FinalMarker },
        new() { Element = BarElement.JobIcon },
        new() { Element = BarElement.JobName },
        new() { Element = BarElement.Rank },
        new() { Element = BarElement.RoleGap, Enabled = false },
        new() { Element = BarElement.Value },
        new() { Element = BarElement.Pace, Enabled = false },
        new() { Element = BarElement.BossHp, Enabled = false },
        new() { Element = BarElement.KillTime, Enabled = false },
        new() { Element = BarElement.EnrageTimer, Enabled = false },
        new() { Element = BarElement.Phase, Enabled = false },
        new() { Element = BarElement.PhaseDps, Enabled = false },
        new() { Element = BarElement.Uptime, Enabled = false },
        new() { Element = BarElement.Percentile, Enabled = false },
    ];

    /// <summary>
    /// The most specific check for this zone and encounter: an entry naming the encounter wins over a zone-wide one.
    /// </summary>
    public PartyDpsThreshold? FindThreshold(uint territory, string encounterTitle)
    {
        var candidates = Thresholds
            .Where(t => t.TerritoryId == 0 || t.TerritoryId == territory)
            .Where(t => t.EncounterName.Length == 0
                        || encounterTitle.Contains(t.EncounterName, StringComparison.OrdinalIgnoreCase))
            .Where(t => t.TerritoryId != 0 || t.EncounterName.Length > 0);

        return candidates
            .OrderByDescending(t => t.EncounterName.Length > 0)
            .ThenByDescending(t => t.TerritoryId != 0)
            .FirstOrDefault();
    }

    /// <summary>
    /// Sets the enrage time of the check matching this fight, creating an encounter check if there is none.
    /// Typically fed with the duration of a pull that wiped to the enrage.
    /// </summary>
    public PartyDpsThreshold SetEnrage(uint territory, string encounterTitle, double seconds)
    {
        var threshold = FindThreshold(territory, encounterTitle);
        if (threshold is null)
        {
            threshold = new PartyDpsThreshold { TerritoryId = territory, EncounterName = encounterTitle, Label = encounterTitle };
            Thresholds.Add(threshold);
        }

        threshold.EnrageSeconds = Math.Round(seconds);
        return threshold;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
