using System.Collections.Generic;
using System.Linq;

namespace DpsInServerBar.Ultimates;

/// <summary>
/// One phase of an ultimate. A boss phase starts when one (or all) of its tracked bosses become targetable and ends when
/// they are all dead, or as soon as the next boss phase starts, even though the fight itself goes on. Only the tracked
/// bosses count for the DPS check: adds spawned during the phase are ignored.
/// A transition (no tracked boss) starts when the previous phase ends and lasts until the next boss phase starts.
/// </summary>
/// <param name="Key">Stable id, used to store the enrage time in the configuration (e.g. "TEA:P1").</param>
/// <param name="Label">Short name shown in the bar and the summaries ("P1", "Limit Cut").</param>
/// <param name="Name">Longer description, shown in the tooltip and the settings.</param>
/// <param name="TrackedBosses">English names (BNpcName) of the bosses whose HP make the DPS check; empty for a transition.</param>
/// <param name="StartWhenAllTargetable">
/// Start only once every tracked boss is targetable (several bosses arriving together), instead of the first one.
/// </param>
public sealed record UltimatePhase(
    string Key, string Label, string Name, IReadOnlyList<string> TrackedBosses, bool StartWhenAllTargetable = false)
{
    /// <summary>A transition between two boss phases: no DPS check, no enrage.</summary>
    public bool IsTransition => TrackedBosses.Count == 0;
}

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
            new("TEA:P1", "P1", "Living Liquid", ["Living Liquid"]),

            // From Living Liquid's death until Brute Justice and Cruise Chaser are both targetable.
            new("TEA:LC", "Limit Cut", "Limit Cut (transition)", []),

            // Brute Justice and Cruise Chaser: starts when both are targetable, both count for the check.
            new("TEA:P2", "P2", "Brute Justice & Cruise Chaser", ["Brute Justice", "Cruise Chaser"], StartWhenAllTargetable: true),
        ]),
    ];

    /// <summary>Label of the n-th phase (1-based) of the ultimate played in this territory.</summary>
    public static string Label(uint territory, int number) =>
        All.FirstOrDefault(f => f.TerritoryId == territory)?.Phases.ElementAtOrDefault(number - 1)?.Label ?? $"P{number}";
}
