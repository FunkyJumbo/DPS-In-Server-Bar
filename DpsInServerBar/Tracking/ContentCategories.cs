using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Tracking;

/// <summary>Kind of content a pull happened in, used to filter the history. Several can be selected at once.</summary>
[Flags]
public enum ContentCategory
{
    None = 0,
    Dungeon = 1,
    Trial = 2,
    Extreme = 4,
    Raid = 8,
    Savage = 16,
    Ultimate = 32,
    Other = 64,
    Criterion = 128,
    All = Dungeon | Trial | Extreme | Raid | Savage | Ultimate | Other | Criterion,

    /// <summary>The content with a real progression: shown in the Progression tab.</summary>
    Progression = Extreme | Savage | Ultimate | Criterion,
}

/// <summary>
/// Classifies a territory from the game data: its duty's ContentType (dungeons, trials, raids, ultimates...),
/// and its Duty Finder section for the difficulty, which is not a content type of its own (extreme, savage).
/// </summary>
public sealed class ContentCategories
{
    // ContentType sheet rows.
    private const uint Dungeons = 2;
    private const uint Trials = 4;
    private const uint Raids = 5;
    private const uint UltimateRaids = 28;
    private const uint VariantAndCriterionDungeons = 30;
    private const uint ChaoticAllianceRaid = 37;

    public static (ContentCategory Category, string Label)[] Labels =>
    [
        (ContentCategory.Dungeon, Loc.T("Dungeons")),
        (ContentCategory.Trial, Loc.T("Trials")),
        (ContentCategory.Extreme, Loc.T("Extreme")),
        (ContentCategory.Raid, Loc.T("Raids")),
        (ContentCategory.Savage, Loc.T("Savage")),
        (ContentCategory.Criterion, Loc.T("Criterion")),
        (ContentCategory.Ultimate, Loc.T("Ultimate")),
        (ContentCategory.Other, Loc.T("Other")),
    ];

    private readonly IDataManager dataManager;
    private readonly Dictionary<uint, ContentCategory> cache = new();

    public ContentCategories(IDataManager dataManager) => this.dataManager = dataManager;

    public ContentCategory Of(uint territory)
    {
        if (!cache.TryGetValue(territory, out var category))
        {
            category = Classify(territory);
            cache[territory] = category;
        }

        return category;
    }

    private ContentCategory Classify(uint territory)
    {
        if (!dataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English).TryGetRow(territory, out var row)
            || row.ContentFinderCondition.ValueNullable is not { } duty)
        {
            return ContentCategory.Other;
        }

        // The Duty Finder section of the duty tells the difficulty: "High-end Trials (Dawntrail)" holds the extremes
        // and unreals (with "The Minstrel's Ballad: ..." ones), "Savage Raids (...)" the savage raids. Read in English.
        var section = dataManager.GetExcelSheet<ContentUICategory>(ClientLanguage.English)
                                 .TryGetRow(duty.ContentUICategory.RowId, out var ui) ? ui.Name.ExtractText() : string.Empty;
        var name = duty.Name.ExtractText();
        return duty.ContentType.RowId switch
        {
            // Criterion dungeons ("Another ..."), normal and savage, apart from the variant ones (no section for them).
            VariantAndCriterionDungeons when name.StartsWith("Another ", StringComparison.OrdinalIgnoreCase) => ContentCategory.Criterion,
            Dungeons or VariantAndCriterionDungeons => ContentCategory.Dungeon,
            Trials when section.StartsWith("High-end", StringComparison.OrdinalIgnoreCase) => ContentCategory.Extreme,
            Trials => ContentCategory.Trial,
            Raids when section.StartsWith("Savage", StringComparison.OrdinalIgnoreCase) => ContentCategory.Savage,
            ChaoticAllianceRaid => ContentCategory.Savage,
            Raids => ContentCategory.Raid,
            UltimateRaids => ContentCategory.Ultimate,
            _ => ContentCategory.Other,
        };
    }
}
