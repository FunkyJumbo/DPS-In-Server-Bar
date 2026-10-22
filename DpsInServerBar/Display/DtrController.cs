using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using DpsInServerBar.Combat;
using DpsInServerBar.Fflogs;
using DpsInServerBar.Tracking;
using DpsInServerBar.Ultimates;

namespace DpsInServerBar.Display;

/// <summary>Owns the server info bar entry and renders the current state into it. Game thread only.</summary>
public sealed class DtrController : IDisposable
{
    private const string EntryName = "DPS In Server Bar";

    private readonly Plugin plugin;
    private readonly IDtrBarEntry entry;

    // Cache of what was last written, so the bar is only touched when something actually changes.
    private string lastKey = string.Empty;

    public DtrController(Plugin plugin, IDtrBar dtrBar)
    {
        this.plugin = plugin;
        entry = dtrBar.Get(EntryName);
        entry.OnClick = OnClick;
        entry.Shown = true;
    }

    public void Dispose() => entry.Remove();

    public void Update()
    {
        var source = plugin.Sources.Current;
        var snapshot = plugin.Sources.Latest;

        var state = BuildState(source, snapshot);
        var key = $"{state.Key}|{source.Name}|{source.StatusText}";
        if (key == lastKey)
            return;

        lastKey = key;
        entry.Shown = state.Visible;
        entry.Text = state.Text;
        entry.Tooltip = BuildTooltip(source, snapshot);
    }

    /// <summary>Forces the next <see cref="Update"/> to redraw (e.g. after a config change).</summary>
    public void Invalidate() => lastKey = string.Empty;

    private void OnClick(DtrInteractionEvent e)
    {
        switch (e.ClickType)
        {
            case MouseClickType.Left:
                plugin.ToggleMetric();
                break;
            case MouseClickType.Right:
                plugin.ToggleMainUi();
                break;
        }
    }

    private readonly record struct RenderState(string Key, bool Visible, SeString Text);

    /// <summary>A piece of the bar: some text or an icon, with an optional color of its own.</summary>
    /// <param name="Glue">Followed by a plain space instead of the separator (the icon and the end marker belong to what follows).</param>
    private readonly record struct Segment(string Text, BitmapFontIcon Icon = BitmapFontIcon.None, Vector4? Color = null, bool Glue = false);

    private RenderState BuildState(ICombatSource source, CombatSnapshot? snapshot)
    {
        var config = plugin.Configuration;
        var label = MetricLabel(config.Metric);

        if (!source.IsReady && snapshot is null)
        {
            var text = $"{source.Name} × {label}";
            return new RenderState("disc|" + label, !config.HideWhenDisconnected,
                                   Render([new Segment(text)], config.UseColors ? config.FinalColor : null));
        }

        if (snapshot is null)
            return new RenderState("idle|" + label, true, Render([new Segment($"- {label}")], null));

        var ended = !snapshot.IsActive;
        if (ended && config.HideAfterSeconds > 0
                  && (DateTime.UtcNow - snapshot.ReceivedAtUtc).TotalSeconds >= config.HideAfterSeconds)
        {
            return new RenderState("hidden", false, new SeString());
        }

        var partyDps = snapshot.PartyDps(config.IncludeLimitBreakInParty);
        double? value = config.Metric switch
        {
            DisplayMetric.Party => partyDps,
            DisplayMetric.Raid => snapshot.You is { HasRdps: true } r ? r.RDps : null,
            _ => snapshot.You?.EncDps ?? 0,
        };

        var threshold = config.FindThreshold(plugin.CurrentTerritory, snapshot.Encounter.Title);
        var boss = plugin.Boss.Current;

        var segments = new List<Segment>();
        foreach (var element in config.BarLayout.Where(e => e.Enabled).Select(e => e.Element))
        {
            var segment = BuildSegment(element, config, snapshot, value, label, ended, partyDps, threshold, boss);
            if (segment is { } s)
                segments.Add(s);
        }

        // The main color: threshold check in pDPS mode, grey once the fight is over.
        Vector4? color = null;
        if (config.UseColors)
        {
            if (config.Metric == DisplayMetric.Party && threshold is not null && value > 0)
                color = value < threshold.EffectiveMinPartyDps ? config.BelowThresholdColor : config.AboveThresholdColor;
            else if (ended)
                color = config.FinalColor;
        }

        var key = string.Join("|", segments.Select(s => $"{s.Text}{s.Icon}{s.Color}")) + $"|{color}";
        return new RenderState(key, true, Render(segments, color, config.BarSeparator));
    }

    private Segment? BuildSegment(
        BarElement element, Configuration config, CombatSnapshot snapshot, double? value, string label, bool ended,
        double partyDps, PartyDpsThreshold? threshold, BossInfo? boss)
    {
        var you = snapshot.You;
        switch (element)
        {
            case BarElement.FinalMarker:
                return ended ? new Segment("●", Glue: true) : null;

            case BarElement.JobIcon:
                return you is { HasJob: true } && JobIcons.For(you.Job) is var icon && icon != BitmapFontIcon.None
                           ? new Segment(string.Empty, icon, Glue: true)
                           : null;

            case BarElement.JobName:
                return you is { HasJob: true } ? new Segment(you.Job) : null;

            case BarElement.Rank:
                // The rank only makes sense for your own (r)DPS, and when there is someone to compare to.
                if (config.Metric == DisplayMetric.Party || snapshot.Combatants.Count(c => c.HasJob) < 2)
                    return null;
                var rank = snapshot.YourRank(byRdps: config.Metric == DisplayMetric.Raid);
                return rank > 0 ? new Segment($"#{rank}") : null;

            case BarElement.RoleGap:
                if (config.Metric == DisplayMetric.Party
                    || snapshot.YourRoleStanding(byRdps: config.Metric == DisplayMetric.Raid) is not { Count: > 1, Gap: { } gap } standing)
                {
                    return null;
                }

                Vector4? gapColor = config.UseColors ? (gap >= 0 ? config.AboveThresholdColor : config.BelowThresholdColor) : null;
                return new Segment($"#{standing.Rank} ({gap:+0%;-0%})", Color: gapColor);

            case BarElement.Value:
                // rDPS needs the built-in tracker: show a dash rather than a wrong number when it has nothing.
                return new Segment($"{(value is { } v ? NumberFormat.Format(v, config.NumberStyle) : "-")} {label}");

            case BarElement.Pace:
                return !ended && plugin.Pulls.PaceVsBest() is { } pace ? new Segment($"{pace:+0%;-0%}") : null;

            case BarElement.Uptime:
            {
                if (plugin.Uptime.Snapshot() is not { AvailableSeconds: >= 5 } up)
                    return null;

                // In an ultimate, the uptime of the phase being fought.
                var uptime = plugin.Ultimates.Current is { Started: true } upPhase
                             && up.Phases.FirstOrDefault(p => p.Number == upPhase.Number) is { } phaseUptime
                                 ? phaseUptime.Uptime
                                 : up.Uptime;
                return new Segment($"GCD {uptime:0.0%}");
            }

            case BarElement.Phase:
                return plugin.Ultimates.Current is { } phaseView ? new Segment($"P{phaseView.Number}") : null;

            case BarElement.BossHp:
                // In an ultimate, only the bosses of the current phase count.
                if (ActivePhase() is { } hpPhase)
                    return new Segment($"HP {hpPhase.HpFraction:0%}");
                return boss is not null ? new Segment($"HP {boss.HpFraction:0%}") : null;

            case BarElement.KillTime:
            {
                TimeSpan? eta;
                double? enrageSeconds;
                if (ActivePhase() is { } etaPhase)
                {
                    // Phase time, from the damage actually dealt to the phase's bosses.
                    (eta, enrageSeconds) = (etaPhase.ProjectedKill, etaPhase.EnrageSeconds);
                }
                else
                {
                    (eta, enrageSeconds) = (ended ? null : BossTracker.ProjectedKill(boss, snapshot.Encounter.Duration, partyDps),
                                            threshold is { EnrageSeconds: > 0 } ? threshold.EnrageSeconds : null);
                }

                if (eta is not { } etaValue)
                    return null;

                // Red when the projected kill comes after the enrage.
                Vector4? etaColor = null;
                if (config.UseColors && enrageSeconds is { } limit)
                    etaColor = etaValue.TotalSeconds > limit ? config.BelowThresholdColor : config.AboveThresholdColor;
                return new Segment($"ETA {NumberFormat.Duration(etaValue)}", Color: etaColor);
            }

            case BarElement.EnrageTimer:
            {
                double? left = null;
                if (ActivePhase() is { } enragePhase)
                    left = enragePhase.EnrageIn?.TotalSeconds;
                else if (!ended && threshold is { EnrageSeconds: > 0 })
                    left = threshold.EnrageSeconds - snapshot.Encounter.Duration.TotalSeconds;

                if (left is not { } seconds)
                    return null;

                // Counts down from the enrage time entered by hand; red for the last 30 seconds.
                Vector4? enrageColor = config.UseColors && seconds <= 30 ? config.BelowThresholdColor : null;
                return new Segment(
                    seconds > 0 ? $"Enrage {NumberFormat.Duration(TimeSpan.FromSeconds(Math.Ceiling(seconds)))}" : "Enrage!",
                    Color: enrageColor);
            }

            case BarElement.Percentile:
                return plugin.EstimatePercentile(snapshot) is { } pct
                           ? new Segment($"~{pct:0}", Color: config.UseColors ? FflogsNames.Color(pct) : null)
                           : null;

            default:
                return null;
        }
    }

    private static void AppendPhase(StringBuilder sb, PhaseView phase, Configuration config)
    {
        sb.Append($"\n{phase.Fight.Key} - P{phase.Number}");
        if (!phase.Supported)
        {
            sb.Append(": phase not supported yet");
            return;
        }

        sb.Append($" {phase.Phase!.Name}");
        if (!phase.Started)
        {
            sb.Append(phase.InCombat ? " (waiting for the boss)" : string.Empty);
            return;
        }

        sb.Append($"  {NumberFormat.Duration(phase.Elapsed)}{(phase.InCombat ? "" : " (pull ended)")}");
        sb.Append($"\nBoss HP: {phase.HpFraction:0.0%} ({NumberFormat.Format(phase.CurrentHp, NumberStyle.Compact)}"
                  + $" / {NumberFormat.Format(phase.MaxHp, NumberStyle.Compact)})");
        sb.Append($"\nDPS on boss: {NumberFormat.Format(phase.BossDps, config.NumberStyle)}");
        if (phase.RequiredDps is { } required)
            sb.Append($"  (need {NumberFormat.Format(required, config.NumberStyle)})");

        if (phase.InCombat && phase.ProjectedKill is { } eta)
            sb.Append($"\nProjected kill: {NumberFormat.Duration(eta)}");
        if (phase.EnrageSeconds is { } enrage)
            sb.Append($"  (enrage {NumberFormat.Duration(TimeSpan.FromSeconds(enrage))})");
    }

    /// <summary>The ultimate phase being fought, when its bosses are up.</summary>
    private PhaseView? ActivePhase() =>
        plugin.Ultimates.Current is { Supported: true, Started: true, InCombat: true } phase ? phase : null;

    private static string MetricLabel(DisplayMetric metric) => metric switch
    {
        DisplayMetric.Party => "pDPS",
        DisplayMetric.Raid => "rDPS",
        _ => "DPS",
    };

    private static SeString Render(IReadOnlyList<Segment> segments, Vector4? color, string separator = " ")
    {
        var builder = new Lumina.Text.SeStringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (i > 0)
                builder.Append(segments[i - 1].Glue ? " " : separator);

            if (s.Icon != BitmapFontIcon.None)
            {
                builder.AppendIcon((uint)s.Icon);
                continue;
            }

            if ((s.Color ?? color) is { } c)
                builder.PushColorRgba(c).Append(s.Text).PopColor();
            else
                builder.Append(s.Text);
        }

        return builder.ToReadOnlySeString().ToDalamudString();
    }

    private SeString BuildTooltip(ICombatSource source, CombatSnapshot? snapshot)
    {
        var config = plugin.Configuration;
        var sb = new StringBuilder();
        sb.Append(source.Name).Append(": ").Append(source.StatusText);

        if (snapshot is not null)
        {
            var enc = snapshot.Encounter;
            sb.Append('\n').Append(string.IsNullOrEmpty(enc.Title) ? "Encounter" : enc.Title)
              .Append(" - ").Append(NumberFormat.Duration(enc.Duration))
              .Append(snapshot.IsActive ? "" : " (ended)");

            if (snapshot.You is { } you)
            {
                var party = snapshot.Combatants.Count(c => c.HasJob);
                sb.Append($"\nYou: {NumberFormat.Format(you.EncDps, config.NumberStyle)} DPS");
                if (party > 1)
                    sb.Append($"  (#{snapshot.YourRank()}/{party})");
                if (you.HasRdps)
                {
                    sb.Append($"\nrDPS: {NumberFormat.Format(you.RDps, config.NumberStyle)}");
                    if (party > 1)
                        sb.Append($"  (#{snapshot.YourRank(byRdps: true)}/{party})");
                }

                sb.Append($"\nCrit {you.CritPercent:0}%  DH {you.DirectHitPercent:0}%  CDH {you.CritDirectHitPercent:0}%");
                if (you.Deaths > 0)
                    sb.Append($"  Deaths {you.Deaths}");
            }

            var byRdps = config.Metric == DisplayMetric.Raid;
            if (snapshot.YourRoleStanding(byRdps) is { Count: > 1, Gap: { } roleGap } role)
            {
                var vs = role.Rank == 1 ? "#2" : "#1";
                sb.Append($"\nAmong {JobRoles.Label(role.Role)}{(byRdps ? " (rDPS)" : "")}: "
                          + $"#{role.Rank}/{role.Count} ({roleGap:+0.0%;-0.0%} vs {vs})");
            }

            var partyDps = snapshot.PartyDps(config.IncludeLimitBreakInParty);
            sb.Append($"\nParty: {NumberFormat.Format(partyDps, config.NumberStyle)} DPS");
            var check = config.FindThreshold(plugin.CurrentTerritory, enc.Title);
            if (check is not null)
                sb.Append($"  (check {NumberFormat.Format(check.EffectiveMinPartyDps, config.NumberStyle)})");

            if (plugin.Ultimates.Current is { } phase)
            {
                // In an ultimate, the phase replaces the fight-wide boss and enrage lines.
                AppendPhase(sb, phase, config);
            }
            else if (plugin.Boss.Current is { } boss)
            {
                sb.Append($"\n{boss.Name}: {boss.HpFraction:0.0%} ({NumberFormat.Format(boss.CurrentHp, NumberStyle.Compact)}"
                          + $" / {NumberFormat.Format(boss.MaxHp, NumberStyle.Compact)})");
                if (snapshot.IsActive && BossTracker.ProjectedKill(boss, enc.Duration, partyDps) is { } eta)
                {
                    sb.Append($"\nProjected kill: {NumberFormat.Duration(eta)}");
                    if (check is { EnrageSeconds: > 0 })
                        sb.Append($"  (enrage {NumberFormat.Duration(TimeSpan.FromSeconds(check.EnrageSeconds))})");
                }
            }

            if (plugin.Ultimates.Current is null && snapshot.IsActive && check is { EnrageSeconds: > 0 })
            {
                var left = check.EnrageSeconds - enc.Duration.TotalSeconds;
                sb.Append(left > 0
                              ? $"\nEnrage in {NumberFormat.Duration(TimeSpan.FromSeconds(Math.Ceiling(left)))}"
                              : "\nEnrage time reached");
            }

            if (plugin.Uptime.Snapshot() is { AvailableSeconds: >= 5 } gcd)
            {
                sb.Append($"\nGCD uptime: {gcd.Uptime:0.0%} (lost {gcd.LostSeconds:0.0}s)");
                if (gcd.Phases.Count > 0)
                    sb.Append("\n  " + string.Join(" | ", gcd.Phases.Select(p => $"P{p.Number} {p.Uptime:0.0%}")));
            }

            if (plugin.EstimatePercentile(snapshot) is { } percentile)
                sb.Append($"\nFFLogs percentile (estimate): ~{percentile:0}");

            if (plugin.History.BestFor(enc.Title) is { } best)
                sb.Append($"\nYour best here: {NumberFormat.Format(best.YourDps, config.NumberStyle)} DPS");
            if (plugin.Pulls.PaceVsBest() is { } pace && snapshot.IsActive)
                sb.Append($"  (pace {pace:+0.0%;-0.0%})");
        }

        sb.Append("\n\nLeft click: DPS / pDPS / rDPS | Right click: details");
        return new SeString().Append(sb.ToString());
    }
}
