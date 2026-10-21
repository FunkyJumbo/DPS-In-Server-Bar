using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Combat;
using DpsInServerBar.Display;
using DpsInServerBar.Tracking;

namespace DpsInServerBar.Windows;

/// <summary>Live encounter breakdown and pull history, opened with right click on the bar entry or /dpsbar.</summary>
public sealed class MainWindow : Window
{
    private static readonly Vector4 YouColor = new(1f, 0.85f, 0.4f, 1f);
    private static readonly Vector4 GoodColor = new(0.45f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 BadColor = new(1f, 0.4f, 0.4f, 1f);

    private readonly Plugin plugin;
    private readonly DpsGraph graph = new();

    private bool selectHistoryTab;
    private string historyFilter = string.Empty;
    private Guid? selectedId;
    private Guid? compareId;

    public MainWindow(Plugin plugin) : base("DPS In Server Bar###DpsInServerBarMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 360) };
        Size = new Vector2(680, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void ShowHistory()
    {
        IsOpen = true;
        selectHistoryTab = true;
    }

    public override void Draw()
    {
        DrawSourceStatus();
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 70);
        if (ImGui.Button("Settings"))
            plugin.ToggleConfigUi();

        using var tabs = ImRaii.TabBar("##maintabs");
        if (!tabs)
            return;

        using (var tab = ImRaii.TabItem("Live"))
        {
            if (tab)
                DrawLive();
        }

        var flags = selectHistoryTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        selectHistoryTab = false;
        using (var tab = ImRaii.TabItem("History", flags))
        {
            if (tab)
                DrawHistory();
        }
    }

    private void DrawSourceStatus()
    {
        var source = plugin.Sources.Current;
        ImGui.TextColored(source.IsReady ? GoodColor : BadColor, $"{source.Name}: {source.StatusText}");
        if (plugin.Configuration.Source == CombatSourceKind.Auto)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(auto)");
        }
    }

    // ---------------------------------------------------------------- Live

    private void DrawLive()
    {
        var config = plugin.Configuration;
        var snapshot = plugin.Sources.Latest;
        if (snapshot is null)
        {
            ImGui.TextDisabled("No combat data yet. Start a fight.");
            return;
        }

        var enc = snapshot.Encounter;
        ImGui.TextUnformatted(string.IsNullOrEmpty(enc.Title) ? "Encounter" : enc.Title);
        ImGui.SameLine();
        ImGui.TextDisabled($"{NumberFormat.Duration(enc.Duration)}{(snapshot.IsActive ? "" : "  (ended)")}");

        var partyDps = snapshot.PartyDps(config.IncludeLimitBreakInParty);
        ImGui.TextUnformatted($"Party DPS: {NumberFormat.Format(partyDps, NumberStyle.Grouped)}");
        var threshold = config.FindThreshold(plugin.CurrentTerritory, enc.Title);
        if (threshold is not null)
        {
            ImGui.SameLine();
            ImGui.TextColored(partyDps >= threshold.EffectiveMinPartyDps ? GoodColor : BadColor,
                              $"(check {NumberFormat.Format(threshold.EffectiveMinPartyDps, NumberStyle.Grouped)})");
        }

        if (plugin.Pulls.PaceVsBest() is { } pace)
        {
            ImGui.SameLine();
            ImGui.TextColored(pace >= 0 ? GoodColor : BadColor, $"Pace vs best: {pace:+0.0%;-0.0%}");
        }

        if (plugin.Boss.Current is { } boss)
        {
            ImGui.TextUnformatted($"{boss.Name}: {boss.HpFraction:0.0%}");
            ImGui.SameLine();
            ImGui.TextDisabled($"({boss.CurrentHp:N0} / {boss.MaxHp:N0})");

            if (snapshot.IsActive && BossTracker.ProjectedKill(boss, enc.Duration, partyDps) is { } eta)
            {
                ImGui.SameLine();
                var text = $"Projected kill {NumberFormat.Duration(eta)}";
                if (threshold is { EnrageSeconds: > 0 })
                {
                    text += $" / enrage {NumberFormat.Duration(TimeSpan.FromSeconds(threshold.EnrageSeconds))}";
                    ImGui.TextColored(eta.TotalSeconds <= threshold.EnrageSeconds ? GoodColor : BadColor, text);
                }
                else
                {
                    ImGui.TextUnformatted(text);
                }
            }
        }

        ImGui.Spacing();
        var tableHeight = Math.Min(ImGui.GetTextLineHeightWithSpacing() * (snapshot.Combatants.Count + 1.5f),
                                   ImGui.GetContentRegionAvail().Y * (config.ShowGraph ? 0.45f : 1f));
        DrawCombatantTable(
            snapshot.Combatants.OrderByDescending(c => c.EncDps).Select(c => new PullCombatant
            {
                Name = c.Name, Job = c.Job, Dps = c.EncDps, Rdps = c.HasRdps ? c.RDps : null, Breakdown = c.RdpsBreakdown, DamagePercent = c.DamagePercent,
                CritPercent = c.CritPercent, DirectHitPercent = c.DirectHitPercent, Deaths = c.Deaths, IsYou = c.IsYou,
            }),
            tableHeight);

        if (config.ShowGraph)
        {
            ImGui.Spacing();
            DrawLiveGraph(threshold);
        }
    }

    private void DrawLiveGraph(PartyDpsThreshold? threshold)
    {
        var window = plugin.Configuration.GraphWindowSeconds;
        graph.DrawOptions(window);

        var pull = plugin.Pulls.Current;
        if (pull is null)
        {
            ImGui.TextDisabled("The graph shows the pull in progress; finished pulls are in the History tab.");
            return;
        }

        var points = graph.ShowParty ? pull.PartyPoints : pull.YourPoints;
        var cumulative = Timeline.Resample(points, pull.LastSeconds);

        var series = new List<DpsGraph.Series>();
        graph.AddCurves(series, graph.ShowParty ? "Party" : "You", cumulative, window);

        var best = plugin.History.BestFor(pull.Title);
        if (best is not null && !graph.ShowParty)
            series.Add(new DpsGraph.Series("Best pull average", Timeline.Average(best.YourDamageTimeline)));

        DpsGraph.Draw("##livegraph", series, ImGui.GetContentRegionAvail().Y,
                      graph.ShowParty ? threshold?.EffectiveMinPartyDps : null);
    }

    // ---------------------------------------------------------------- History

    private void DrawHistory()
    {
        var pulls = plugin.History.Pulls;
        if (pulls.Count == 0)
        {
            ImGui.TextDisabled("No pull recorded yet.");
            ImGui.TextDisabled(plugin.Configuration.RecordOnlyInDuties
                                   ? "Pulls are recorded inside duties only (see Settings > Pulls)."
                                   : "Pulls shorter than the minimum duration are ignored.");
            return;
        }

        DrawHistoryFilter(pulls);

        var visible = pulls
            .Where(p => historyFilter.Length == 0 || p.Title.Equals(historyFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        using (var child = ImRaii.Child("##pulllist", new Vector2(-1, ImGui.GetContentRegionAvail().Y * 0.4f), true))
        {
            if (child)
                DrawPullTable(visible);
        }

        var selected = pulls.FirstOrDefault(p => p.Id == selectedId);
        if (selected is null)
        {
            ImGui.TextDisabled("Select a pull to see its details. Tick \"vs\" on another one to compare.");
            return;
        }

        var compare = pulls.FirstOrDefault(p => p.Id == compareId && p.Id != selectedId);
        DrawPullDetails(selected, compare);
    }

    private void DrawHistoryFilter(IReadOnlyList<PullRecord> pulls)
    {
        var titles = pulls.Select(p => p.Title).Where(t => t.Length > 0)
                          .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();

        ImGui.SetNextItemWidth(260);
        using (var combo = ImRaii.Combo("Encounter", historyFilter.Length == 0 ? "All" : historyFilter))
        {
            if (combo)
            {
                if (ImGui.Selectable("All", historyFilter.Length == 0))
                    historyFilter = string.Empty;
                foreach (var title in titles)
                {
                    if (ImGui.Selectable(title, title == historyFilter))
                        historyFilter = title;
                }
            }
        }

        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 90);
        using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
        {
            if (ImGui.Button("Clear all"))
            {
                plugin.History.Clear();
                selectedId = compareId = null;
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl to delete the whole history.");
    }

    private void DrawPullTable(List<PullRecord> pulls)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH
                                      | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        using var table = ImRaii.Table("##pulls", 11, flags);
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Encounter", ImGuiTableColumnFlags.WidthStretch, 2.4f);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("DPS", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("rDPS", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("pDPS", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn("Rank", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("vs", ImGuiTableColumnFlags.WidthFixed, 24);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24);
        ImGui.TableHeadersRow();

        Guid? toDelete = null;
        foreach (var p in pulls)
        {
            using var id = ImRaii.PushId(p.Id.ToString());
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (ImGui.Selectable(p.StartUtc.ToLocalTime().ToString("dd/MM HH:mm"), p.Id == selectedId,
                                 ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap))
            {
                selectedId = p.Id;
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.Title.Length > 0 ? p.Title : p.Zone);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(NumberFormat.Duration(TimeSpan.FromSeconds(p.DurationSeconds)));
            ImGui.TableNextColumn();
            switch (p.Outcome)
            {
                case PullOutcome.Kill:
                    ImGui.TextColored(GoodColor, "Kill");
                    break;
                case PullOutcome.Wipe:
                    ImGui.TextColored(BadColor, "Wipe");
                    break;
                default:
                    ImGui.TextDisabled("-");
                    break;
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.Job);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(NumberFormat.Format(p.YourDps, NumberStyle.Grouped));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.YourRdps is { } rd ? NumberFormat.Format(rd, NumberStyle.Grouped) : "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(NumberFormat.Format(p.PartyDps, NumberStyle.Grouped));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.PartySize > 1 && p.Rank > 0 ? $"{p.Rank}/{p.PartySize}" : "-");

            ImGui.TableNextColumn();
            var isCompare = compareId == p.Id;
            if (ImGui.Checkbox("##cmp", ref isCompare))
                compareId = isCompare ? p.Id : null;

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("X"))
                toDelete = p.Id;
        }

        if (toDelete is { } del)
        {
            plugin.History.Remove(del);
            if (selectedId == del)
                selectedId = null;
            if (compareId == del)
                compareId = null;
        }
    }

    /// <summary>A pull that wiped to the enrage gives its exact timing: one click stores it in the party check.</summary>
    private void DrawSetEnrageButton(PullRecord pull)
    {
        var config = plugin.Configuration;
        var duration = Math.Round(pull.DurationSeconds);
        var current = config.FindThreshold(pull.TerritoryId, pull.Title)?.EnrageSeconds ?? 0;
        var durationText = NumberFormat.Duration(TimeSpan.FromSeconds(duration));

        ImGui.SameLine();
        using (ImRaii.Disabled(current == duration))
        {
            if (ImGui.SmallButton($"Set as enrage time ({durationText})"))
            {
                config.SetEnrage(pull.TerritoryId, pull.Title, duration);
                plugin.OnConfigChanged();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "Use this pull's duration as the enrage time of this fight (Party checks), e.g. after a wipe to the enrage.\n"
                + (current > 0
                       ? $"Current enrage time: {NumberFormat.Duration(TimeSpan.FromSeconds(current))}."
                       : "No enrage time yet: a party check is created for this encounter."));
        }
    }

    private void DrawPullDetails(PullRecord pull, PullRecord? compare)
    {
        var config = plugin.Configuration;

        ImGui.TextUnformatted($"{(pull.Title.Length > 0 ? pull.Title : pull.Zone)} - {pull.StartUtc.ToLocalTime():g}");
        ImGui.SameLine();
        ImGui.TextDisabled($"({pull.Source})");

        DrawSetEnrageButton(pull);

        if (compare is not null)
        {
            var dpsDelta = compare.YourDps > 0 ? pull.YourDps / compare.YourDps - 1 : 0;
            var partyDelta = compare.PartyDps > 0 ? pull.PartyDps / compare.PartyDps - 1 : 0;
            ImGui.TextUnformatted($"vs {compare.StartUtc.ToLocalTime():g}:");
            ImGui.SameLine();
            ImGui.TextColored(dpsDelta >= 0 ? GoodColor : BadColor, $"DPS {dpsDelta:+0.0%;-0.0%}");
            ImGui.SameLine();
            ImGui.TextColored(partyDelta >= 0 ? GoodColor : BadColor, $"pDPS {partyDelta:+0.0%;-0.0%}");
            ImGui.SameLine();
            ImGui.TextDisabled($"time {NumberFormat.Duration(TimeSpan.FromSeconds(pull.DurationSeconds))} vs "
                               + NumberFormat.Duration(TimeSpan.FromSeconds(compare.DurationSeconds)));
        }

        var tableHeight = Math.Min(ImGui.GetTextLineHeightWithSpacing() * (pull.Combatants.Count + 1.5f),
                                   ImGui.GetContentRegionAvail().Y * (config.ShowGraph ? 0.4f : 1f));
        DrawCombatantTable(pull.Combatants, tableHeight);

        if (!config.ShowGraph)
            return;

        var window = config.GraphWindowSeconds;
        graph.DrawOptions(window);

        var series = new List<DpsGraph.Series>();
        graph.AddCurves(series, "This pull", graph.ShowParty ? pull.PartyDamageTimeline : pull.YourDamageTimeline, window);
        if (compare is not null)
        {
            graph.AddCurves(series, "Compared", graph.ShowParty ? compare.PartyDamageTimeline : compare.YourDamageTimeline,
                            window);
        }

        var threshold = graph.ShowParty ? config.FindThreshold(pull.TerritoryId, pull.Title)?.EffectiveMinPartyDps : null;
        DpsGraph.Draw("##historygraph", series, ImGui.GetContentRegionAvail().Y, threshold);
    }

    // ---------------------------------------------------------------- Shared

    private void DrawCombatantTable(IEnumerable<PullCombatant> combatants, float height)
    {
        var rows = combatants.ToList();
        var showRdps = rows.Any(c => c.Rdps is not null);

        // Sorting only matters when there is an rDPS column to sort by.
        var config = plugin.Configuration;
        if (showRdps)
        {
            ImGui.TextDisabled("Sort by");
            ImGui.SameLine();
            if (ImGui.RadioButton("DPS##sort", !config.SortByRdps))
            {
                config.SortByRdps = false;
                config.Save();
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("rDPS##sort", config.SortByRdps))
            {
                config.SortByRdps = true;
                config.Save();
            }

            height = Math.Max(ImGui.GetTextLineHeightWithSpacing() * 2, height - ImGui.GetFrameHeightWithSpacing());
        }

        rows = showRdps && config.SortByRdps
                   ? rows.OrderByDescending(c => c.Rdps ?? c.Dps).ToList()
                   : rows.OrderByDescending(c => c.Dps).ToList();

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH
                                      | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

        using var table = ImRaii.Table("##combatants", showRdps ? 8 : 7, flags, new Vector2(-1, height));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("DPS", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        if (showRdps)
            ImGui.TableSetupColumn("rDPS", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("%", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Crit", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("DH", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Deaths", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        foreach (var c in rows)
        {
            ImGui.TableNextRow();

            void Cell(string text)
            {
                ImGui.TableNextColumn();
                if (c.IsYou)
                    ImGui.TextColored(YouColor, text);
                else
                    ImGui.TextUnformatted(text);
            }

            Cell(c.Name);
            Cell(c.Job);
            Cell(NumberFormat.Format(c.Dps, NumberStyle.Grouped));
            if (showRdps)
                Cell(c.Rdps is { } r ? NumberFormat.Format(r, NumberStyle.Grouped) : "-");

            if (showRdps && c.Breakdown is { } b && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"Given to others' buffs: -{NumberFormat.Format(b.GivenDps, NumberStyle.Grouped)} DPS\n"
                    + $"Received from own buffs: +{NumberFormat.Format(b.ReceivedDps, NumberStyle.Grouped)} DPS\n"
                    + $"Crit rate used: {b.CritRate:0.0%}   DH rate used: {b.DirectHitRate:0.0%}\n"
                    + $"({b.Samples} hits observed; exact stats are used for you at level 100)");
            }

            Cell($"{c.DamagePercent:0}%");
            Cell($"{c.CritPercent:0}%");
            Cell($"{c.DirectHitPercent:0}%");
            Cell(c.Deaths.ToString());
        }
    }
}
