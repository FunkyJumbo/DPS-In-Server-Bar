using System;
using System.Collections.Generic;
using System.Linq;
using DpsInServerBar.Ultimates;

namespace DpsInServerBar.Tracking;

/// <summary>Pulls played with the same group. <see cref="Key"/> stays the same while new pulls are added.</summary>
public sealed record PullGroup(
    string Key, string Label, string Members, List<PullRecord> Pulls, SavedGroup? Saved, List<string> Regulars);

/// <summary>
/// How far a pull got, to follow a progression pull after pull: the boss HP taken (0-100 %), or in an ultimate the
/// phase reached plus the HP taken in it (phase 3 at 40 % left = 2.6). A kill is the top of the scale.
/// </summary>
public static class Progression
{
    // Deaths this close to the end of a wipe are the wipe itself.
    private const double WipeWindowSeconds = 20;

    public static bool IsUltimate(uint territory) => UltimateTracker.FightFor(territory) is not null;

    /// <summary>The fight a pull belongs to: the ultimate's name, else the pull's title (the enemy hit the most).</summary>
    public static string Encounter(PullRecord pull) => UltimateTracker.FightFor(pull.TerritoryId)?.Name ?? pull.Title;

    /// <summary>Names of the phases of the ultimate played in this territory, empty elsewhere.</summary>
    public static List<string> PhaseNames(uint territory) =>
        UltimateTracker.FightFor(territory)?.Phases.Select(p => p.Label).ToList() ?? [];

    // Pulls further apart than this are not the same evening in the instance.
    private static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Hours spent in the instance at the end of each pull (oldest first): fights plus the time between pulls of a same
    /// evening, the breaks between evenings left out.
    /// </summary>
    public static double[] InstanceHours(IReadOnlyList<PullRecord> pulls)
    {
        var hours = new double[pulls.Count];
        var total = TimeSpan.Zero;
        for (var i = 0; i < pulls.Count; i++)
        {
            if (i > 0)
            {
                var gap = pulls[i].StartUtc - pulls[i - 1].StartUtc.AddSeconds(pulls[i - 1].DurationSeconds);
                if (gap > TimeSpan.Zero && gap < SessionGap)
                    total += gap;
            }

            total += TimeSpan.FromSeconds(pulls[i].DurationSeconds);
            hours[i] = total.TotalHours;
        }

        return hours;
    }

    /// <summary>Top of the scale: 100 (%), or the number of phases of the ultimate.</summary>
    public static double Max(uint territory) => UltimateTracker.FightFor(territory)?.Phases.Count ?? 100;

    /// <summary>Phase reached (1-based) in an ultimate, from the phases the pull has uptime for.</summary>
    public static int Phase(PullRecord pull) => pull.PhaseUptimes.Count > 0 ? pull.PhaseUptimes.Max(p => p.Number) : 1;

    /// <summary>The pull's progress, null when unknown (pulls recorded before the boss HP was saved).</summary>
    public static double? Score(PullRecord pull)
    {
        if (pull.Outcome == PullOutcome.Kill)
            return Max(pull.TerritoryId);

        if (IsUltimate(pull.TerritoryId))
            return Phase(pull) - 1 + (pull.BossHpLeft is { } hp ? 1 - hp : 0);

        return pull.BossHpLeft is { } left ? (1 - left) * 100 : null;
    }

    /// <summary>"Kill", "P3 · 42.1 %" or "23.4 % left".</summary>
    public static string Label(PullRecord pull)
    {
        if (pull.Outcome == PullOutcome.Kill)
            return "Kill";

        if (IsUltimate(pull.TerritoryId))
        {
            var phase = UltimateDefinitions.Label(pull.TerritoryId, Phase(pull));
            return pull.BossHpLeft is { } hp ? $"{phase} · {hp:0.0%} left" : phase;
        }

        return pull.BossHpLeft is { } left ? $"{left:0.0%} left" : "?";
    }

    /// <summary>
    /// What wiped the party: the most common cause of death in the last seconds of the pull (the earliest one on a tie),
    /// else its first death. Null for a kill or a pull without deaths.
    /// </summary>
    public static string? WipeCause(PullRecord pull)
    {
        if (pull.Outcome == PullOutcome.Kill || pull.PartyDeaths.Count == 0)
            return null;

        var end = Math.Max(pull.DurationSeconds, pull.PartyDeaths.Max(d => d.AtSeconds));
        var final = pull.PartyDeaths.Where(d => d.AtSeconds >= end - WipeWindowSeconds).ToList();
        if (final.Count == 0)
            return pull.PartyDeaths[0].Cause;

        return final.GroupBy(d => d.Cause)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Min(d => d.AtSeconds))
                    .First().Key;
    }

    /// <summary>
    /// Splits pulls by group. A pull goes with the saved group sharing the most of its other players (at least half),
    /// else with the first automatic group sharing at least half of them, else starts a new one; pulls without other
    /// players make the "Solo" group. Saved groups first (even without pulls), then the biggest.
    /// </summary>
    public static List<PullGroup> Groups(IReadOnlyList<PullRecord> pulls, IReadOnlyList<SavedGroup> saved)
    {
        var savedGroups = saved.Select(s => new Builder(s)).ToList();
        var groups = new List<Builder>();
        var solo = new List<PullRecord>();
        foreach (var pull in pulls)
        {
            var others = Others(pull);
            if (others.Count == 0)
            {
                solo.Add(pull);
                continue;
            }

            var group = savedGroups.Select(g => (Group: g, Shared: others.Count(g.Members.Contains)))
                                   .Where(g => g.Shared * 2 >= others.Count)
                                   .OrderByDescending(g => g.Shared)
                                   .Select(g => g.Group)
                                   .FirstOrDefault()
                        ?? groups.FirstOrDefault(g => others.Count(g.Members.Contains) * 2 >= others.Count);
            if (group is null)
            {
                group = new Builder(null);
                groups.Add(group);
            }

            if (group.Saved is null)
                group.Members.UnionWith(others);
            foreach (var name in others)
                group.Seen[name] = group.Seen.GetValueOrDefault(name) + 1;
            group.Pulls.Add(pull);
        }

        var result = savedGroups.Select(g => g.Build())
                                .Concat(groups.Select(g => g.Build()).OrderByDescending(g => g.Pulls.Count))
                                .ToList();
        if (solo.Count > 0)
            result.Add(new PullGroup("solo", "Solo", string.Empty, solo, null, []));

        return result;
    }

    private sealed class Builder(SavedGroup? saved)
    {
        public SavedGroup? Saved { get; } = saved;
        public HashSet<string> Members { get; } = new(saved?.Players ?? [], StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PullRecord> Pulls { get; } = [];

        public PullGroup Build()
        {
            var byPresence = Seen.OrderByDescending(s => s.Value).Select(s => s.Key).ToList();
            // The regulars: players in at least a quarter of the pulls (not a one-night substitute).
            var regulars = Seen.Where(s => s.Value * 4 >= Pulls.Count).OrderByDescending(s => s.Value).Select(s => s.Key).ToList();
            if (Saved is not null)
            {
                return new PullGroup($"saved:{Saved.Id}", Saved.Name, string.Join(", ", Saved.Players), Pulls, Saved,
                                     Saved.Players.ToList());
            }

            var label = string.Join(", ", byPresence.Take(3)) + (byPresence.Count > 3 ? $" +{byPresence.Count - 3}" : string.Empty);
            return new PullGroup(Pulls[0].Id.ToString(), label, string.Join(", ", byPresence), Pulls, null, regulars);
        }
    }

    // The other players of a pull (no pets, no Limit Break).
    private static List<string> Others(PullRecord pull) =>
        pull.Combatants.Where(c => !c.IsYou && c.Job.Length > 0).Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The pulls of the same fight as <paramref name="title"/>, oldest first (a whole ultimate, whatever its phase).</summary>
    public static List<PullRecord> PullsOf(IEnumerable<PullRecord> pulls, string title)
    {
        return pulls.Where(p => Encounter(p).Equals(title, StringComparison.OrdinalIgnoreCase))
                  .OrderBy(p => p.StartUtc)
                  .ToList();
    }
}
