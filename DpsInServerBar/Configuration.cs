using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Configuration;
using Newtonsoft.Json;
using DpsInServerBar.Combat;
using DpsInServerBar.Tracking;
using DpsInServerBar.UI;

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
    /// <summary>Removed (saved as numbers: kept so the other values do not move).</summary>
    FinalMarker,
    JobIcon,
    JobName,
    Rank,
    RoleGap,
    Value,
    Pace,
    BossHp,
    /// <summary>Removed.</summary>
    KillTime,
    EnrageTimer,
    Phase,
    PhaseDps,
    Uptime,
    Percentile,

    /// <summary>Time since the pull.</summary>
    Duration,
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

    /// <summary>On a wipe, the ability that killed you.</summary>
    KilledBy = 128,

    /// <summary>Mechanics you failed (penalty debuffs), and the party's count.</summary>
    Mistakes = 256,
    All = Job | Dps | Rdps | Percentile | Uptime | PartyDps | Deaths | KilledBy | Mistakes,
}

/// <summary>Optional columns of the compact meter.</summary>
[Flags]
public enum CompactColumn
{
    None = 0,
    Share = 1,
    Crit = 2,
    DirectHit = 4,
    Deaths = 8,
}

[Serializable]
public sealed class BarElementSetting
{
    public BarElement Element { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>A group saved under a name for the progression view: its pulls are those sharing half of their players with it.</summary>
[Serializable]
public sealed class SavedGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> Players { get; set; } = [];
}

/// <summary>A named server bar setup (value, format and elements), switchable with /dpsbar preset N.</summary>
[Serializable]
public sealed class BarPreset
{
    public string Name { get; set; } = string.Empty;
    public DisplayMetric Metric { get; set; }
    public NumberStyle NumberStyle { get; set; } = NumberStyle.Grouped;
    public string Separator { get; set; } = " ";

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<BarElementSetting> Layout { get; set; } = [];

    /// <summary>Copies the current display settings into this preset.</summary>
    public void CaptureFrom(Configuration c)
    {
        Metric = c.Metric;
        NumberStyle = c.NumberStyle;
        Separator = c.BarSeparator;
        Layout = CopyLayout(c.BarLayout);
    }

    /// <summary>Applies this preset to the current display settings.</summary>
    public void ApplyTo(Configuration c)
    {
        c.Metric = Metric;
        c.NumberStyle = NumberStyle;
        c.BarSeparator = Separator;
        c.BarLayout = CopyLayout(Layout);
        c.Migrate(); // elements added since the preset was saved show up, hidden
    }

    /// <summary>True when the current display settings are the ones saved in this preset.</summary>
    public bool Matches(Configuration c) =>
        Metric == c.Metric
        && NumberStyle == c.NumberStyle
        && Separator == c.BarSeparator
        && Layout.Select(e => (e.Element, e.Enabled)).SequenceEqual(c.BarLayout.Select(e => (e.Element, e.Enabled)));

    private static List<BarElementSetting> CopyLayout(IEnumerable<BarElementSetting> layout) =>
        layout.Select(e => new BarElementSetting { Element = e.Element, Enabled = e.Enabled }).ToList();
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 8;

    /// <summary>Order and visibility of the elements of the server bar text.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<BarElementSetting> BarLayout { get; set; } = DefaultLayout();

    // Data source
    public CombatSourceKind Source { get; set; } = CombatSourceKind.Native;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 10501;

    // Display
    public DisplayMetric Metric { get; set; } = DisplayMetric.Personal;
    public NumberStyle NumberStyle { get; set; } = NumberStyle.Grouped;
    /// <summary>Text placed between the elements of the server bar (a space by default, e.g. " | ").</summary>
    public string BarSeparator { get; set; } = " ";

    /// <summary>Saved server bar setups, numbered from 1 for /dpsbar preset N.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<BarPreset> BarPresets { get; set; } = [];

    /// <summary>The preset last applied, -1 for none. Changes go into it only when saved explicitly.</summary>
    public int ActiveBarPreset { get; set; } = -1;

    [JsonIgnore]
    public BarPreset? CurrentBarPreset =>
        ActiveBarPreset >= 0 && ActiveBarPreset < BarPresets.Count ? BarPresets[ActiveBarPreset] : null;

    /// <summary>Show the DPS graphs in the main window.</summary>
    public bool ShowGraph { get; set; } = true;

    /// <summary>Kinds of content shown in the History tab (several can be selected).</summary>
    public ContentCategory HistoryCategories { get; set; } = ContentCategory.All;

    /// <summary>Order the player tables of the main window by rDPS instead of DPS.</summary>
    public bool SortByRdps { get; set; } = false;

    /// <summary>Live tab: only the party's DPS meter (value, share, deaths).</summary>
    public bool LiveCompact { get; set; } = false;

    /// <summary>Opener practice: countdown before the pull when pressing Start, in seconds.</summary>
    public int OpenerCountdown { get; set; } = 5;

    /// <summary>Opener practice: Start launches the in-game /countdown (out of combat), so the pull is the game's.</summary>
    public bool OpenerGameCountdown { get; set; } = true;

    /// <summary>Opener practice: the compact pop-up is shown instead of the full window.</summary>
    public bool OpenerCompact { get; set; }

    /// <summary>Opener practice: opacity of the windows' background only, 0 (see-through) to 1.</summary>
    public float OpenerOpacity { get; set; } = 1f;

    /// <summary>Opener practice: size of the compact pop-up (icons, text and buttons), 0.5 to 1.5.</summary>
    public float OpenerCompactScale { get; set; } = 1f;

    /// <summary>Opener practice: close the window once the opener is done, or this long after its first action.</summary>
    public bool OpenerAutoClose { get; set; }

    /// <summary>Opener practice: seconds after the first action before the window closes by itself.</summary>
    public int OpenerAutoCloseSeconds { get; set; } = 40;

    /// <summary>Opener practice: include the pre-pull actions.</summary>
    public bool OpenerPrepull { get; set; } = true;

    /// <summary>Opener practice: include the tincture.</summary>
    public bool OpenerPotion { get; set; } = true;

    /// <summary>Opener practice: opener chosen for each job (job abbreviation to opener id).</summary>
    public Dictionary<string, string> OpenerSelected { get; set; } = new();

    /// <summary>Columns of the compact meter shown after the player and the DPS / rDPS.</summary>
    public CompactColumn CompactColumns { get; set; } = CompactColumn.Share | CompactColumn.Deaths;

    /// <summary>Opacity of the compact meter's background, 0 (see-through) to 1.</summary>
    public float CompactOpacity { get; set; } = 1f;

    /// <summary>Shows the compact meter's header row (Player, DPS / rDPS, columns).</summary>
    public bool CompactShowHeader { get; set; } = true;

    /// <summary>Replaced by CompactOpacity: only read once.</summary>
    public bool? CompactTransparent { get; set; }
    public bool IncludeLimitBreakInParty { get; set; } = true;


    /// <summary>Hide the entry this many seconds after the encounter ends (0 = always visible).</summary>
    public int HideAfterSeconds { get; set; } = 0;

    /// <summary>Hide the entry while no source can provide data.</summary>
    public bool HideWhenDisconnected { get; set; } = false;

    // Appearance of the plugin's windows
    public ThemePreset ThemePreset { get; set; } = ThemePreset.Midnight;

    /// <summary>Colors of the custom theme (ThemePreset.Custom).</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public ThemePalette CustomTheme { get; set; } = ThemePalette.For(ThemePreset.Midnight);

    /// <summary>Groups saved under a name (History > Progression).</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<SavedGroup> SavedGroups { get; set; } = [];

    /// <summary>Group shown in History > Progression ("saved:<id>", an automatic group's key, or empty for all pulls).</summary>
    public string ProgressionGroup { get; set; } = string.Empty;

    /// <summary>Custom themes saved under a name (Appearance settings).</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<SavedTheme> SavedThemes { get; set; } = [];

    /// <summary>The saved theme in use, -1 for a preset or the unsaved custom theme.</summary>
    public int SavedThemeIndex { get; set; } = -1;

    /// <summary>The colors in use: a saved theme, the custom theme or a preset.</summary>
    [JsonIgnore]
    public ThemePalette ActivePalette =>
        SavedThemeIndex >= 0 && SavedThemeIndex < SavedThemes.Count ? SavedThemes[SavedThemeIndex].Palette
        : ThemePreset == ThemePreset.Custom ? CustomTheme
        : ThemePalette.For(ThemePreset);

    /// <summary>Opacity of the windows' background, 0.2 to 1.</summary>
    public float BackgroundOpacity { get; set; } = 0.96f;

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

    /// <summary>Failed mechanics: ignore a vulnerability put on a tank by a tankbuster.</summary>
    public bool MistakesIgnoreTankbusters { get; set; } = true;
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

    /// <summary>Combined max HP of each phase's bosses, as last read from the game (all bosses of the phase must die).</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, double> UltimatePhaseHpDetected { get; set; } = new();

    /// <summary>Phase HP entered by hand, overriding the detected one (0 or absent = use the detected HP).</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, double> UltimatePhaseHp { get; set; } = new();

    /// <summary>HP used for a phase's DPS check: entered by hand, else the larger of the live and detected HP.</summary>
    public double PhaseCheckHp(string phaseKey, double liveHp) =>
        UltimatePhaseHp.GetValueOrDefault(phaseKey) is > 0 and var manual
            ? manual
            : Math.Max(liveHp, UltimatePhaseHpDetected.GetValueOrDefault(phaseKey));

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

        if (CompactTransparent is { } transparent)
        {
            CompactOpacity = transparent ? 0f : 1f;
            CompactTransparent = null;
            changed = true;
        }

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

        if (Version < 5)
        {
            // "Killed by" was added to the chat summary: turn it on for those who had every value on.
            if (ChatSummaryElements == (SummaryElement.All & ~SummaryElement.KilledBy))
                ChatSummaryElements = SummaryElement.All;
            Version = 5;
            changed = true;
        }

        if (Version < 6)
        {
            // "Killed by" is now wanted everywhere: turn it on for everyone.
            ChatSummaryElements |= SummaryElement.KilledBy;
            Version = 6;
            changed = true;
        }

        if (Version < 7)
        {
            // The end-of-fight marker and the projected kill time were removed from the bar.
            BarLayout.RemoveAll(e => RemovedElements.Contains(e.Element));
            Version = 7;
            changed = true;
        }

        if (Version < 8)
        {
            // Failed mechanics were added to the chat summary: on for everyone.
            ChatSummaryElements |= SummaryElement.Mistakes;
            Version = 8;
            changed = true;
        }

        // Elements added in later versions show up, hidden, at the end.
        foreach (var element in Enum.GetValues<BarElement>().Except(RemovedElements))
        {
            if (BarLayout.All(e => e.Element != element))
            {
                BarLayout.Add(new BarElementSetting { Element = element, Enabled = false });
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Bar elements that no longer exist.</summary>
    public static readonly BarElement[] RemovedElements = [BarElement.FinalMarker, BarElement.KillTime];

    private void SetShown(BarElement element, bool shown)
    {
        foreach (var e in BarLayout.Where(e => e.Element == element))
            e.Enabled = shown;
    }

    private static List<BarElementSetting> DefaultLayout() =>
    [
        new() { Element = BarElement.JobIcon },
        new() { Element = BarElement.JobName },
        new() { Element = BarElement.Rank },
        new() { Element = BarElement.RoleGap, Enabled = false },
        new() { Element = BarElement.Value },
        new() { Element = BarElement.Pace, Enabled = false },
        new() { Element = BarElement.BossHp, Enabled = false },
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
    /// <param name="fightName">Name of the fight when it is listed in the DPS check tab: its zone-wide check is created.</param>
    public PartyDpsThreshold SetEnrage(uint territory, string encounterTitle, double seconds, string? fightName = null)
    {
        var threshold = FindThreshold(territory, encounterTitle);
        if (threshold is null)
        {
            threshold = fightName is not null
                            ? new PartyDpsThreshold { TerritoryId = territory, Label = fightName }
                            : new PartyDpsThreshold { TerritoryId = territory, EncounterName = encounterTitle, Label = encounterTitle };
            Thresholds.Add(threshold);
        }

        threshold.EnrageSeconds = Math.Round(seconds);
        return threshold;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
