using System.Collections.Generic;

namespace DpsInServerBar.Ultimates;

/// <summary>
/// One phase of an ultimate. It starts when one of its tracked bosses becomes targetable and ends when they are
/// all dead (or gone), even though the fight itself goes on. Only the tracked bosses count for the DPS check:
/// adds spawned during the phase are ignored.
/// </summary>
/// <param name="Key">Stable id, used to store the enrage time in the configuration (e.g. "TEA:P1").</param>
/// <param name="Name">Shown in the tooltip and the settings.</param>
/// <param name="TrackedBosses">English names (BNpcName) of the bosses whose HP make the DPS check.</param>
public sealed record UltimatePhase(string Key, string Name, IReadOnlyList<string> TrackedBosses);

public sealed record UltimateFight(string Key, string Name, uint TerritoryId, IReadOnlyList<UltimatePhase> Phases);

/// <summary>
/// Ultimates and their phases, added one at a time. Territory ids come from the game data;
/// boss names are resolved to name ids through the English BNpcName sheet.
/// </summary>
public static class UltimateDefinitions
{
    public static readonly IReadOnlyList<UltimateFight> All =
    [
        new("TEA", "The Epic of Alexander", 887,
        [
            // Living Liquid summons Liquid Hand (and Liquid Rage): only Living Liquid is part of the check.
            new("TEA:P1", "Living Liquid", ["Living Liquid"]),
        ]),
    ];
}
