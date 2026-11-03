using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Tracking;

public enum FightCategory
{
    Ultimate,
    Savage,
    Extreme,
}

/// <summary>A high-end fight from the game data, listed in the DPS check tab.</summary>
public sealed record FightEntry(FightCategory Category, uint TerritoryId, string Name, uint ContentId);

/// <summary>
/// Ultimates, savage raids and extreme trials, read from the duty finder data, most recent first.
/// The kind is recognized on the English names, the names shown are in the client's language.
/// </summary>
public sealed class FightCatalog
{
    // ContentType rows.
    private const uint TrialsType = 4;
    private const uint RaidsType = 5;
    private const uint UltimateRaidsType = 28;

    private readonly IDataManager dataManager;
    private IReadOnlyList<FightEntry>? fights;

    public FightCatalog(IDataManager dataManager) => this.dataManager = dataManager;

    /// <summary>All fights, most recent first (later duty finder entries are newer content).</summary>
    public IReadOnlyList<FightEntry> All => fights ??= Load();

    public IEnumerable<FightEntry> Of(FightCategory category) => All.Where(f => f.Category == category);

    public bool Contains(uint territoryId) => All.Any(f => f.TerritoryId == territoryId);

    private List<FightEntry> Load()
    {
        var english = dataManager.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English);
        var local = dataManager.GetExcelSheet<ContentFinderCondition>();
        var result = new List<FightEntry>();
        var seen = new HashSet<uint>();

        foreach (var row in english.OrderByDescending(r => r.RowId))
        {
            var territory = row.TerritoryType.RowId;
            if (territory == 0 || !seen.Add(territory))
                continue;

            var name = row.Name.ExtractText();
            FightCategory? category = row.ContentType.RowId switch
            {
                UltimateRaidsType => FightCategory.Ultimate,
                RaidsType when name.EndsWith("(Savage)", StringComparison.OrdinalIgnoreCase) => FightCategory.Savage,
                // Some extremes are named "The Minstrel's Ballad: ..." instead of "... (Extreme)".
                TrialsType when name.EndsWith("(Extreme)", StringComparison.OrdinalIgnoreCase)
                                || name.StartsWith("the Minstrel's Ballad", StringComparison.OrdinalIgnoreCase) => FightCategory.Extreme,
                _ => null,
            };
            if (category is not { } kind)
                continue;

            var shown = local.TryGetRow(row.RowId, out var localRow) && localRow.Name.ExtractText() is { Length: > 0 } localName
                            ? localName
                            : name;
            result.Add(new FightEntry(kind, territory, Capitalize(shown), row.RowId));
        }

        return result;
    }

    // Duty names start in lower case ("the Weapon's Refrain (Ultimate)").
    private static string Capitalize(string name) =>
        name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;
}
