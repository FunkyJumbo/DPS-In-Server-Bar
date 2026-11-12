using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Tracking;

/// <summary>A time you dealt less damage because of a debuff, and the damage it cost.</summary>
/// <param name="Reduction">Share of the damage taken away (0.25 = -25%).</param>
/// <param name="Guessed">The game did not send the strength: <see cref="Reduction"/> is the usual one for this content.</param>
/// <param name="LostDamage">Damage you would have dealt more without it.</param>
public sealed record DamageLoss(
    double Start, double End, uint StatusId, string Status, bool Revive, double Reduction, bool Guessed, double LostDamage);

/// <summary>
/// The damage you lost in a pull to Damage Down (failed mechanics) and to Weakness / Brink of Death (after a raise):
/// your damage dealt while the debuff was on, scaled back up by its strength.
/// </summary>
public static class DamageDownLoss
{
    private const uint Weakness = 43;
    private const uint BrinkOfDeath = 44;

    // Usual strength of Damage Down when the game does not send it: harsher in ultimates.
    private const double UltimateDamageDown = 0.9;
    private const double DamageDown = 0.25;

    private static Dictionary<uint, bool>? damageDown; // status id -> stacking

    /// <summary>Your losses in the pull, oldest first; empty when none (or the debuffs were not recorded).</summary>
    public static List<DamageLoss> Of(PullRecord pull)
    {
        var timeline = pull.YourDamageTimeline;
        if (pull.Debuffs.Count == 0 || timeline.Length < 2)
            return [];

        var ultimate = Progression.IsUltimate(pull.TerritoryId);
        var spans = new List<(DebuffSpan Span, double Reduction, bool Guessed, bool Revive)>();
        foreach (var d in pull.Debuffs.Where(d => d.IsYou))
        {
            if (d.StatusId == Weakness)
                spans.Add((d, 0.25, false, true));
            else if (d.StatusId == BrinkOfDeath)
                spans.Add((d, 0.5, false, true));
            else if (DamageDownIds().TryGetValue(d.StatusId, out var stacking))
            {
                var sent = !stacking && d.Param is > 0 and < 100;
                spans.Add((d, sent ? d.Param / 100.0 : ultimate ? UltimateDamageDown : DamageDown, !sent, false));
            }
        }

        if (spans.Count == 0)
            return [];

        // Second by second: the damage of that second scaled back up by every debuff on (they multiply).
        var end = timeline.Length - 1;
        var lost = new double[spans.Count];
        for (var s = 0; s < end; s++)
        {
            var dealt = timeline[s + 1] - timeline[s];
            if (dealt <= 0)
                continue;

            var on = new List<int>();
            var kept = 1.0;
            for (var i = 0; i < spans.Count; i++)
            {
                var (span, reduction, _, _) = spans[i];
                if (span.AppliedSeconds <= s + 0.5 && (span.EndSeconds ?? end) > s + 0.5)
                {
                    on.Add(i);
                    kept *= 1 - reduction;
                }
            }

            if (on.Count == 0 || kept <= 0)
                continue;

            // Shared between the debuffs on, in proportion to their strength.
            var missing = dealt / kept - dealt;
            var weights = on.Sum(i => spans[i].Reduction);
            foreach (var i in on)
                lost[i] += missing * spans[i].Reduction / weights;
        }

        return spans.Select((x, i) => new DamageLoss(x.Span.AppliedSeconds, Math.Min(x.Span.EndSeconds ?? end, end), x.Span.StatusId,
                                                     x.Span.Status, x.Revive, x.Reduction, x.Guessed, lost[i]))
                    .OrderBy(l => l.Start)
                    .ToList();
    }

    private static Dictionary<uint, bool> DamageDownIds()
    {
        if (damageDown is null)
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Status>(ClientLanguage.English);
            damageDown = sheet.Where(s => s.Name.ExtractText() == "Damage Down")
                              .ToDictionary(s => s.RowId, s => s.MaxStacks > 1);
        }

        return damageDown;
    }
}
