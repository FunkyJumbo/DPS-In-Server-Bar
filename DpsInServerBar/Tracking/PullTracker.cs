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
    private (PullRecord Pull, PullRecord? PreviousBest)? pendingSummary;

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
            PhaseUptimes = uptime?.Phases.ToList() ?? [],
            UptimeAvailableBySecond = uptime?.AvailableBySecond ?? [],
            UptimeActiveBySecond = uptime?.ActiveBySecond ?? [],
            PartyDps = final.PartyDps(config.IncludeLimitBreakInParty),
            Rank = final.YourRank(),
            PartySize = partyMembers.Count,
            Deaths = you?.Deaths ?? 0,
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

        var previousBest = plugin.History.BestFor(record.Title);

        plugin.History.Add(record, config.MaxHistory);
        lastFinished = record;
        lastFinishedUtc = DateTime.UtcNow;

        if (config.ChatSummary && (config.ChatSummaryInDungeons || !IsDungeon(record.TerritoryId)))
        {
            FlushSummary(force: true); // a previous one still waiting
            pendingSummary = (record, previousBest);
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
        if (pendingSummary is not { } p)
            return;
        if (!force && p.Pull.Outcome == PullOutcome.Unknown && (DateTime.UtcNow - lastFinishedUtc).TotalSeconds < 10)
            return;

        pendingSummary = null;
        PrintSummary(p.Pull, p.PreviousBest);
    }

    private void PrintSummary(PullRecord pull, PullRecord? previousBest)
    {
        var config = plugin.Configuration;
        var sb = new Lumina.Text.SeStringBuilder();

        sb.PushColorRgba(Muted).Append("[DPS] ").PopColor();
        sb.Append(string.IsNullOrEmpty(pull.Title) ? "Pull" : pull.Title)
          .Append($" ({NumberFormat.Duration(TimeSpan.FromSeconds(pull.DurationSeconds))}) ");
        switch (pull.Outcome)
        {
            case PullOutcome.Kill:
                sb.PushColorRgba(Good).Append("Kill").PopColor();
                break;
            case PullOutcome.Wipe:
                sb.PushColorRgba(Bad).Append("Wipe").PopColor();
                break;
        }

        sb.Append("  ");

        if (pull.Job.Length > 0)
            sb.Append(pull.Job).Append(' ');
        sb.Append($"{NumberFormat.Format(pull.YourDps, config.NumberStyle)} DPS");
        if (pull.PartySize > 1 && pull.Rank > 0)
            sb.Append($" #{pull.Rank}/{pull.PartySize}");
        if (pull.YourRdps is { } rdps)
            sb.Append($"  rDPS {NumberFormat.Format(rdps, config.NumberStyle)}");
        if (pull.Percentile is { } pct)
            sb.Append(" ").PushColorRgba(Fflogs.FflogsNames.Color(pct)).Append($"(~{pct:0})").PopColor();
        if (pull.Uptime is { } gcd)
            sb.Append($"  GCD {gcd:0.0%}");

        // Only kills are compared: a wipe or a prog pull ending early says nothing about your DPS.
        if (previousBest is not null && pull.Outcome == PullOutcome.Kill)
        {
            var delta = pull.YourDps / previousBest.YourDps - 1;
            sb.Append("  ");
            if (delta >= 0)
                sb.PushColorRgba(Good).Append("new best!").PopColor();
            else
                sb.PushColorRgba(Muted).Append($"best {NumberFormat.Format(previousBest.YourDps, config.NumberStyle)} ({delta:+0.0%;-0.0%})").PopColor();
        }

        sb.Append($"  |  pDPS {NumberFormat.Format(pull.PartyDps, config.NumberStyle)}");
        var threshold = config.FindThreshold(pull.TerritoryId, pull.Title);
        if (threshold is not null)
        {
            var ok = pull.PartyDps >= threshold.EffectiveMinPartyDps;
            sb.Append(' ').PushColorRgba(ok ? Good : Bad)
              .Append(ok ? "(check OK)" : $"(check {NumberFormat.Format(threshold.EffectiveMinPartyDps, config.NumberStyle)})")
              .PopColor();
        }

        if (pull.Deaths > 0)
            sb.PushColorRgba(Bad).Append($"  deaths: {pull.Deaths}").PopColor();

        chat.Print(sb.ToReadOnlySeString().ToDalamudString());
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
