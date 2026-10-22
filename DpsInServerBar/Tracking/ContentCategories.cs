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
    All = Dungeon | Trial | Extreme | Raid | Savage | Ultimate | Other,
}

/// <summary>
/// Classifies a territory from the game data: its duty's ContentType (dungeons, trials, raids, ultimates...),
/// and its English name for the difficulty, which is not a content type of its own ("(Extreme)", "(Savage)").
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

    public static readonly (ContentCategory Category, string Label)[] Labels =
    [
        (ContentCategory.Dungeon, "Dungeons"),
        (ContentCategory.Trial, "Trials"),
        (ContentCategory.Extreme, "Extreme"),
        (ContentCategory.Raid, "Raids"),
        (ContentCategory.Savage, "Savage"),
        (ContentCategory.Ultimate, "Ultimate"),
        (ContentCategory.Other, "Other"),
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

        var name = duty.Name.ExtractText();
        return duty.ContentType.RowId switch
        {
            Dungeons or VariantAndCriterionDungeons => ContentCategory.Dungeon,
            Trials when name.Contains("(Extreme)", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("(Unreal)", StringComparison.OrdinalIgnoreCase) => ContentCategory.Extreme,
            Trials => ContentCategory.Trial,
            Raids when name.Contains("(Savage)", StringComparison.OrdinalIgnoreCase) => ContentCategory.Savage,
            ChaoticAllianceRaid => ContentCategory.Savage,
            Raids => ContentCategory.Raid,
            UltimateRaids => ContentCategory.Ultimate,
            _ => ContentCategory.Other,
        };
    }
}
