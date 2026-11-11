using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using DpsInServerBar.Combat;
using DpsInServerBar.Display;
using DpsInServerBar.Native;
using DpsInServerBar.Ultimates;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Tracking;

/// <summary>
/// Watches the snapshots of the active source, cuts them into pulls, records each pull's timeline
/// and stores finished pulls in the history. Game thread only.
/// </summary>
public sealed class PullTracker : IDisposable
{
    private static readonly Vector4 Good = new(0.45f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Muted = new(0.7f, 0.7f, 0.7f, 1f);

    // ContentType sheet row for dungeons.
    private const uint DungeonContentType = 2;

    private readonly Plugin plugin;
    private readonly IDutyState dutyState;
    private readonly ICondition condition;
    private readonly IChatGui chat;

    private CombatSnapshot? lastSeen;
    private PullInProgress? current;
    private PullOutcome pendingOutcome;
    private PullRecord? lastFinished;
    private DateTime lastFinishedUtc;

    // The chat summary waits for the duty outcome (kill / wipe), which can arrive a bit after the encounter ends.
    private PullRecord? pendingSummary;

    public PullTracker(Plugin plugin, IDutyState dutyState, ICondition condition, IChatGui chat)
    {
        this.plugin = plugin;
        this.dutyState = dutyState;
        this.condition = condition;
        this.chat = chat;

        dutyState.DutyCompleted += OnDutyCompleted;
        dutyState.DutyWiped += OnDutyWiped;
    }

    /// <summary>The pull being fought right now, if any.</summary>
    public PullInProgress? Current => current;

    public void Dispose()
    {
        dutyState.DutyCompleted -= OnDutyCompleted;
        dutyState.DutyWiped -= OnDutyWiped;
    }

    public void Update(CombatSnapshot? snapshot, string sourceName)
    {
        FlushSummary(force: false);

        if (snapshot is null || ReferenceEquals(snapshot, lastSeen))
            return;
        lastSeen = snapshot;

        var seconds = snapshot.Encounter.Duration.TotalSeconds;

        if (snapshot.IsActive)
        {
            // A shorter duration than what we already have means the source started a new encounter.
            if (current is not null && seconds + 1 < current.LastSeconds)
                Finish(current.LastSnapshot);

            current ??= new PullInProgress(snapshot, sourceName, plugin.CurrentTerritory);
            current.Add(snapshot, plugin.Configuration.IncludeLimitBreakInParty);
            return;
        }

        if (current is not null)
        {
            current.Add(snapshot, plugin.Configuration.IncludeLimitBreakInParty);
            Finish(snapshot);
        }
    }

    /// <summary>
    /// Your current pace against your best pull of the same encounter, at the same point in time
    /// (+0.05 = 5% more damage done so far). Null when there is nothing to compare to.
    /// </summary>
    public double? PaceVsBest()
    {
        if (current is null || current.LastSeconds < 10)
            return null;

        var best = plugin.History.BestFor(current.Title);
        var t = (int)current.LastSeconds;
        if (best is null || best.YourDamageTimeline.Length <= t || best.YourDamageTimeline[t] <= 0)
            return null;

        return current.LastYourDamage / best.YourDamageTimeline[t] - 1;
    }

    private void Finish(CombatSnapshot final)
    {
        var pull = current!;
        current = null;
        var outcome = pendingOutcome;
        pendingOutcome = PullOutcome.Unknown;

        var config = plugin.Configuration;
        var duration = final.Encounter.Duration.TotalSeconds;
        if (duration < config.MinPullSeconds)
            return;
        if (config.RecordOnlyInDuties && !condition[ConditionFlag.BoundByDuty])
            return;

        var uptime = plugin.Uptime.Snapshot();
        var you = final.You;
        var partyMembers = final.Combatants.Where(c => c.HasJob).ToList();
        var record = new PullRecord
        {
            StartUtc = pull.StartUtc,
            Title = pull.Title,
            Zone = final.Encounter.Zone,
            TerritoryId = pull.TerritoryId,
            Source = pull.SourceName,
            DurationSeconds = duration,
            Outcome = outcome,
            Job = you?.Job ?? string.Empty,
            YourDps = you?.EncDps ?? 0,
            YourRdps = you is { HasRdps: true } ? you.RDps : null,
            Percentile = plugin.EstimatePercentile(final),
            Uptime = uptime?.Uptime,
            UptimeLostSeconds = uptime?.LostSeconds ?? 0,
            Gcd = uptime?.Gcd,
            Mistakes = plugin.Sources.Native.Mistakes.Current.ToList(),
            PartyDeaths = plugin.Sources.Native.PartyDeaths.Since(pull.StartUtc).ToList(),
            GaugeWaste = plugin.Gauges.Current.ToList(),
            Positionals = plugin.Positionals.Current.ToList(),
            ComboMistakes = plugin.Combos.Current.ToList(),
            DotUptimes = plugin.Dots.Current.ToList(),
            TankSwaps = plugin.Sources.Native.TankSwaps.Current.ToList(),
            Overheal = plugin.Sources.Native.Heals.Current.ToList(),
            Mitigations = plugin.Sources.Native.Mitigations.Current.ToList(),
            PhaseUptimes = uptime?.Phases.ToList() ?? [],
            UptimeAvailableBySecond = uptime?.AvailableBySecond ?? [],
            UptimeActiveBySecond = uptime?.ActiveBySecond ?? [],
            BossHpLeft = plugin.Boss.Current?.HpFraction,
            BossName = plugin.Boss.Current?.Name ?? string.Empty,
            LastBossCast = plugin.Boss.LastCast,
            PartyDps = final.PartyDps(config.IncludeLimitBreakInParty),
            Rank = final.YourRank(),
            RdpsRank = you is { HasRdps: true } ? final.YourRank(byRdps: true) : 0,
            PartySize = partyMembers.Count,
            Deaths = you?.Deaths ?? 0,
            KilledBy = plugin.Sources.Native.Deaths.DeathBetween(pull.StartUtc, DateTime.UtcNow) is { } death ? Describe(death) : string.Empty,
            Combatants = final.Combatants
                .OrderByDescending(c => c.EncDps)
                .Select(c => new PullCombatant
                {
                    Name = c.Name,
                    Job = c.Job,
                    Dps = c.EncDps,
                    Rdps = c.HasRdps ? c.RDps : null,
                    DamagePercent = c.DamagePercent,
                    CritPercent = c.CritPercent,
                    DirectHitPercent = c.DirectHitPercent,
                    Deaths = c.Deaths,
                    IsYou = c.IsYou,
                })
                .ToList(),
            YourDamageTimeline = Timeline.Resample(pull.YourPoints, duration),
            PartyDamageTimeline = Timeline.Resample(pull.PartyPoints, duration),
        };

        plugin.History.Add(record, config.MaxHistory);
        lastFinished = record;
        lastFinishedUtc = DateTime.UtcNow;

        // In a supported ultimate the phase summaries replace the pull summary.
        var ultimateSummaries = config.UltimateMode && config.ChatSummaryPhases && UltimateTracker.FightFor(record.TerritoryId) is not null;
        if (config.ChatSummary && !ultimateSummaries && (config.ChatSummaryInDungeons || !IsDungeon(record.TerritoryId)))
        {
            FlushSummary(force: true); // a previous one still waiting
            pendingSummary = record;
            FlushSummary(force: false);
        }
    }

    private void OnDutyCompleted(IDutyStateEventArgs args) => SetOutcome(PullOutcome.Kill);

    private void OnDutyWiped(IDutyStateEventArgs args) => SetOutcome(PullOutcome.Wipe);

    private void SetOutcome(PullOutcome outcome)
    {
        // The duty event can arrive slightly before or after the source closes the encounter.
        if (current is not null)
        {
            pendingOutcome = outcome;
        }
        else if (lastFinished is not null && (DateTime.UtcNow - lastFinishedUtc).TotalSeconds < 30)
        {
            lastFinished.Outcome = outcome;
            plugin.History.Changed();
            FlushSummary(force: false);
        }
    }

    private static bool IsDungeon(uint territory) =>
        Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var row)
        && row.ContentFinderCondition.ValueNullable?.ContentType.RowId == DungeonContentType;

    /// <summary>Prints the pending summary once the outcome is known, or after a few seconds without one.</summary>
    private void FlushSummary(bool force)
    {
        if (pendingSummary is not { } pull)
            return;
        if (!force && pull.Outcome == PullOutcome.Unknown && (DateTime.UtcNow - lastFinishedUtc).TotalSeconds < 10)
            return;

        pendingSummary = null;
        // Your death can be seen, or its cause refined (a body still falling), after the encounter was closed.
        if (plugin.Sources.Native.Deaths.DeathBetween(pull.StartUtc, DateTime.UtcNow) is { } death && Describe(death) != pull.KilledBy)
        {
            pull.KilledBy = Describe(death);
            plugin.History.Changed();
        }

        // Same for the party: a death closing the fight, or a cause refined since.
        var deaths = plugin.Sources.Native.PartyDeaths.Since(pull.StartUtc);
        if (deaths.Count >= pull.PartyDeaths.Count && DeathKey(deaths) != DeathKey(pull.PartyDeaths))
        {
            pull.PartyDeaths = deaths.ToList();
            plugin.History.Changed();
        }

        PrintSummary(pull);
    }

    /// <summary>Prints made-up summaries with the values ticked in the settings, to check what they look like.</summary>
    public void PrintPreview()
    {
        var config = plugin.Configuration;
        var job = plugin.Sources.Latest?.You is { HasJob: true } you ? you.Job : "DRK";
        PrintSummary(new PullRecord
        {
            StartUtc = DateTime.UtcNow,
            Title = "Preview Boss",
            DurationSeconds = 272,
            Outcome = PullOutcome.Kill,
            Job = job,
            YourDps = 21_234,
            YourRdps = 20_511,
            Percentile = 87,
            Uptime = 0.987,
            PartyDps = 182_345,
            Rank = 2,
            RdpsRank = 3,
            PartySize = 8,
            Deaths = 1,
            KilledBy = "Auto-attack (Preview Boss, 45,210)",
            Mistakes =
            [
                new MechanicMistake(64, "You", job, true, "Vulnerability Up", "Preview Cleave", "Preview Boss"),
                new MechanicMistake(131, "You", job, true, "Vulnerability Up", "Preview Cleave", "Preview Boss"),
                new MechanicMistake(131, "Party member", "WHM", false, "Damage Down", "Preview Tower", "Preview Boss"),
            ],
        }, preview: true);

        // The ultimate phase summaries too, when they are on: a cleared phase and a wipe.
        if (config.UltimateMode && config.ChatSummaryPhases && UltimateDefinitions.All.FirstOrDefault() is { Phases.Count: > 0 } fight)
        {
            var phases = fight.Phases.Where(p => !p.IsTransition).ToList();
            PrintPhaseSummary(new PhaseResult(fight, phases[0], 1, TimeSpan.FromSeconds(118), 8_000_000, 0, 125, Cleared: true), preview: true);
            if (phases.Count > 1)
                PrintPhaseSummary(new PhaseResult(fight, phases[1], 3, TimeSpan.FromSeconds(95), 20_000_000, 3_100_000, 180, Cleared: false), preview: true);
        }
    }

    private void PrintSummary(PullRecord pull, bool preview = false)
    {
        var config = plugin.Configuration;
        var shown = config.ChatSummaryElements;
        var sb = new Lumina.Text.SeStringBuilder();

        sb.PushColorRgba(Muted).Append("[Recap] ").PopColor();
        sb.Append(string.IsNullOrEmpty(pull.Title) ? "Pull" : pull.Title)
          .Append($" ({NumberFormat.Duration(TimeSpan.FromSeconds(pull.DurationSeconds))})");
        switch (pull.Outcome)
        {
            case PullOutcome.Kill:
                sb.Append(" ").PushColorRgba(Good).Append("Kill").PopColor();
                break;
            case PullOutcome.Wipe:
                sb.Append(" ").PushColorRgba(Bad).Append("Wipe").PopColor();
                break;
        }

        // Each selected value is a part, separated by " | ".
        var parts = new List<System.Action>();

        if (shown.HasFlag(SummaryElement.Job) && pull.Job.Length > 0)
            parts.Add(() => sb.Append(pull.Job));

        if (shown.HasFlag(SummaryElement.Dps))
        {
            parts.Add(() =>
            {
                sb.Append($"DPS {NumberFormat.Format(pull.YourDps, config.NumberStyle)}");
                AppendRank(sb, pull.Rank, pull.PartySize);
                AppendBest(sb, pull, pull.YourDps, p => p.YourDps, isRate: false, preview ? 1.021 : null);
            });
        }

        if (shown.HasFlag(SummaryElement.Rdps) && pull.YourRdps is { } rdps)
        {
            parts.Add(() =>
            {
                sb.Append($"rDPS {NumberFormat.Format(rdps, config.NumberStyle)}");
                AppendRank(sb, pull.RdpsRank, pull.PartySize);
                AppendBest(sb, pull, rdps, p => p.YourRdps, isRate: false, preview ? 0.988 : null);
            });
        }

        if (shown.HasFlag(SummaryElement.Percentile) && pull.Percentile is { } pct)
            parts.Add(() => sb.PushColorRgba(Fflogs.FflogsNames.Color(pct)).Append($"FFLogs ~{pct:0}").PopColor());

        if (shown.HasFlag(SummaryElement.Uptime) && pull.Uptime is { } gcd)
        {
            parts.Add(() =>
            {
                sb.Append($"GCD {gcd:0.0%}");
                AppendBest(sb, pull, gcd, p => p.Uptime, isRate: true, preview ? -0.004 : null);
            });
        }

        if (shown.HasFlag(SummaryElement.PartyDps))
        {
            parts.Add(() =>
            {
                sb.Append($"pDPS {NumberFormat.Format(pull.PartyDps, config.NumberStyle)}");
                var threshold = config.FindThreshold(pull.TerritoryId, pull.Title);
                if (preview)
                    sb.Append(" ").PushColorRgba(Good).Append("(check OK)").PopColor();
                else if (threshold is not null)
                {
                    var ok = pull.PartyDps >= threshold.EffectiveMinPartyDps;
                    sb.Append(" ").PushColorRgba(ok ? Good : Bad)
                      .Append(ok ? "(check OK)" : $"(check {NumberFormat.Format(threshold.EffectiveMinPartyDps, config.NumberStyle)})")
                      .PopColor();
                }

                AppendBest(sb, pull, pull.PartyDps, p => p.PartyDps, isRate: false, preview ? 1.008 : null);
            });
        }

        if (shown.HasFlag(SummaryElement.Deaths) && pull.Deaths > 0)
            parts.Add(() => sb.PushColorRgba(Bad).Append($"Deaths {pull.Deaths}").PopColor());

        if (shown.HasFlag(SummaryElement.KilledBy) && pull.KilledBy.Length > 0)
            parts.Add(() => sb.Append("Killed by ").PushColorRgba(Bad).Append(pull.KilledBy).PopColor());

        // Your failed mechanics by debuff, then the party's total: "Mechanics: Vulnerability Up x2 (party 5)".
        if (shown.HasFlag(SummaryElement.Mistakes) && pull.Mistakes.Count > 0)
        {
            var yours = pull.Mistakes.Where(m => m.IsYou).GroupBy(m => m.Status)
                            .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key).ToList();
            parts.Add(() =>
            {
                sb.Append("Mechanics: ");
                if (yours.Count > 0)
                    sb.PushColorRgba(Bad).Append(string.Join(", ", yours)).PopColor();
                else
                    sb.PushColorRgba(Good).Append("none").PopColor();
                if (pull.PartySize > 1)
                    sb.PushColorRgba(Muted).Append($" (party {pull.Mistakes.Count})").PopColor();
            });
        }

        foreach (var part in parts)
        {
            sb.PushColorRgba(Muted).Append(" | ").PopColor();
            part();
        }

        chat.Print(sb.ToReadOnlySeString().ToDalamudString());
    }

    /// <summary>
    /// Chat summary of an ultimate phase: phase time, DPS on the phase's bosses against the DPS needed for the enrage,
    /// enrage margin and GCD uptime of the phase. For a phase not cleared, the boss HP left instead of the margin.
    /// </summary>
    public void PrintPhaseSummary(PhaseResult result, bool preview = false)
    {
        var config = plugin.Configuration;
        var sb = new Lumina.Text.SeStringBuilder();

        sb.PushColorRgba(Muted).Append("[Recap] ").PopColor();
        sb.Append($"{result.Fight.Key} {result.Phase.Label} {result.Phase.Name} ({NumberFormat.Duration(result.Elapsed)})");
        if (result.Cleared)
            sb.Append(" ").PushColorRgba(Good).Append("Cleared").PopColor();
        else
            sb.Append(" ").PushColorRgba(Bad).Append("Wipe").PopColor();

        var parts = new List<System.Action>();

        if (!result.Cleared && result.MaxHp > 0)
            parts.Add(() => sb.Append($"HP left {result.HpLeft / result.MaxHp:0.0%}"));

        if (result.BossDps > 0)
        {
            parts.Add(() =>
            {
                sb.Append($"Boss DPS {NumberFormat.Format(result.BossDps, config.NumberStyle)}");
                if (result.RequiredDps is { } required)
                {
                    sb.Append(" ").PushColorRgba(result.BossDps >= required ? Good : Bad)
                      .Append($"(need {NumberFormat.Format(required, config.NumberStyle)}, {result.BossDps / required - 1:+0.0%;-0.0%})")
                      .PopColor();
                }
            });
        }

        if (result.Cleared && result.EnrageSeconds is { } enrage)
        {
            var margin = enrage - result.Elapsed.TotalSeconds;
            parts.Add(() => sb.Append($"enrage {NumberFormat.Duration(TimeSpan.FromSeconds(enrage))}, ")
                              .PushColorRgba(margin >= 0 ? Good : Bad).Append($"{margin:+0;-0}s margin").PopColor());
        }

        var phaseGcd = preview
                           ? 0.975
                           : plugin.Uptime.Snapshot()?.Phases.FirstOrDefault(p => p.Number == result.Number) is { AvailableSeconds: >= 5 } gcd
                               ? gcd.Uptime
                               : (double?)null;
        if (phaseGcd is { } phaseUptime)
            parts.Add(() => sb.Append($"GCD {phaseUptime:0.0%}"));

        // What killed you during this phase, if you died.
        var death = preview
                        ? result.Cleared ? null : new DeathInfo(DateTime.UtcNow, "Hydrothermal Missile", "Living Liquid", 98_432)
                        : plugin.Sources.Native.Deaths.DeathBetween(DateTime.UtcNow - result.Elapsed - TimeSpan.FromSeconds(1), DateTime.UtcNow);
        if (config.ChatSummaryElements.HasFlag(SummaryElement.KilledBy) && death is not null)
            parts.Add(() => sb.Append("Killed by ").PushColorRgba(Bad).Append(Describe(death)).PopColor());

        foreach (var part in parts)
        {
            sb.PushColorRgba(Muted).Append(" | ").PopColor();
            part();
        }

        chat.Print(sb.ToReadOnlySeString().ToDalamudString());
    }

    /// <summary>"Hydrothermal Missile (Living Liquid, 98,432)".</summary>
    private static string DeathKey(IEnumerable<PartyDeath> deaths) =>
        string.Join("|", deaths.Select(d => $"{d.Player} {d.AtSeconds:0} {d.Cause} {d.Damage}"));

    private static string Describe(DeathInfo death)
    {
        var details = new List<string>();
        if (death.Source.Length > 0)
            details.Add(death.Source);
        if (death.Damage > 0)
            details.Add(death.Damage.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        return details.Count > 0 ? $"{death.Ability} ({string.Join(", ", details)})" : death.Ability;
    }

    private static void AppendRank(Lumina.Text.SeStringBuilder sb, int rank, int partySize)
    {
        if (partySize > 1 && rank > 0)
            sb.Append($" #{rank}/{partySize}");
    }

    /// <summary>
    /// "NEW BEST" when the value beats your best kill of the fight, the gap otherwise. Only kills are compared:
    /// a wipe or a prog pull ending early says nothing. Rates (GCD uptime) are compared in points.
    /// </summary>
    /// <param name="previewGain">Preview only: made-up gain over the best (ratio, or points for a rate) instead of the history.</param>
    private void AppendBest(
        Lumina.Text.SeStringBuilder sb, PullRecord pull, double value, Func<PullRecord, double?> metric, bool isRate,
        double? previewGain = null)
    {
        if (!plugin.Configuration.ChatSummaryCompareBest || pull.Outcome != PullOutcome.Kill)
            return;

        double best;
        if (previewGain is { } gain)
            best = isRate ? value - gain : value / gain;
        else if (plugin.History.BestOf(pull.Title, metric, pull.Id) is { } recorded)
            best = recorded;
        else
            return;

        var gap = isRate ? $"{(value - best) * 100:+0.0;-0.0} pts" : $"{value / best - 1:+0.0%;-0.0%}";
        if (value >= best)
            sb.Append(" ").PushColorRgba(Good).Append($"NEW BEST ({gap})").PopColor();
        else
            sb.Append(" ").PushColorRgba(Muted).Append($"(best {gap})").PopColor();
    }
}

/// <summary>Data collected while a pull is ongoing.</summary>
public sealed class PullInProgress
{
    private readonly List<(double T, double Value)> yourPoints = [];
    private readonly List<(double T, double Value)> partyPoints = [];

    public PullInProgress(CombatSnapshot first, string sourceName, uint territoryId)
    {
        StartUtc = DateTime.UtcNow - first.Encounter.Duration;
        SourceName = sourceName;
        TerritoryId = territoryId;
        LastSnapshot = first;
    }

    public DateTime StartUtc { get; }
    public string SourceName { get; }
    public uint TerritoryId { get; }
    public string Title => LastSnapshot.Encounter.Title;
    public CombatSnapshot LastSnapshot { get; private set; }
    public double LastSeconds { get; private set; }
    public double LastYourDamage { get; private set; }
    public IReadOnlyList<(double T, double Value)> YourPoints => yourPoints;
    public IReadOnlyList<(double T, double Value)> PartyPoints => partyPoints;

    /// <summary>Bumped on every new point, so the graph knows when to rebuild its curves.</summary>
    public int Revision { get; private set; }

    public void Add(CombatSnapshot snapshot, bool includeLimitBreak)
    {
        LastSnapshot = snapshot;
        var t = snapshot.Encounter.Duration.TotalSeconds;
        if (t <= 0 || (yourPoints.Count > 0 && t <= yourPoints[^1].T))
            return;

        LastSeconds = t;
        LastYourDamage = snapshot.You?.Damage ?? 0;
        var party = snapshot.Combatants
            .Where(c => c.HasJob || (includeLimitBreak && c.IsLimitBreak))
            .Sum(c => c.Damage);

        yourPoints.Add((t, LastYourDamage));
        partyPoints.Add((t, party));
        Revision++;
    }
}
