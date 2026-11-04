using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Combat;
using DpsInServerBar.Display;
using DpsInServerBar.Tracking;
using DpsInServerBar.UI;
using DpsInServerBar.Ultimates;

namespace DpsInServerBar.Windows;

/// <summary>Live encounter breakdown and pull history, opened with right click on the bar entry or /dpsbar.</summary>
public sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly DpsGraph graph = new();

    private int selectedTab;
    private string historyFilter = string.Empty;
    private Guid? selectedId;
    private Guid? compareId;
    private IDisposable? themeScope;

    public MainWindow(Plugin plugin) : base("DPS In Server Bar###DpsInServerBarMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 380) };
        Size = new Vector2(760, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void ShowHistory()
    {
        IsOpen = true;
        selectedTab = 1;
    }

    public override void PreDraw() => themeScope = Theme.Push();

    public override void PostDraw()
    {
        themeScope?.Dispose();
        themeScope = null;
    }

    public override void Draw()
    {
        Theme.TabStrip("main", ref selectedTab, (FontAwesomeIcon.Bolt, "Live"), (FontAwesomeIcon.History, "History"));
        DrawSourceAndSettings();
        Theme.Rule(2);

        if (selectedTab == 0)
            DrawLive();
        else
            DrawHistory();
    }

    /// <summary>At the right of the tabs: data source state and the settings button (the title bar already names the plugin).</summary>
    private void DrawSourceAndSettings()
    {
        var source = plugin.Sources.Current;
        var status = $"{source.Name}: {source.StatusText}{(plugin.Configuration.Source == CombatSourceKind.Auto ? " (auto)" : "")}";
        var badgeWidth = ImGui.CalcTextSize(status).X + Theme.Px(14);
        var buttonWidth = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - badgeWidth - buttonWidth - spacing);

        // Centered on the height of the tabs.
        var y = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(y + Theme.Px(5));
        Theme.Badge(status, source.IsReady ? Theme.Good : Theme.Bad);
        ImGui.SameLine();
        ImGui.SetCursorPosY(y + Theme.Px(1));
        if (Theme.IconButton("settings", FontAwesomeIcon.Cog, "Settings"))
            plugin.ToggleConfigUi();
    }

    // ---------------------------------------------------------------- Live

    private void DrawLive()
    {
        var config = plugin.Configuration;
        var snapshot = plugin.Sources.Latest;

        if (Theme.Chip("Compact view", false))
            plugin.SetCompact(true);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only the party's DPS meter, in a small pop-up window.");

        if (snapshot is null)
        {
            Theme.Hint("No combat data yet. Start a fight.");
            return;
        }

        var enc = snapshot.Encounter;
        ImGui.TextColored(Theme.GoldBright, string.IsNullOrEmpty(enc.Title) ? "Encounter" : enc.Title);
        ImGui.SameLine();
        Theme.Badge(NumberFormat.Duration(enc.Duration), Theme.Crystal);
        if (!snapshot.IsActive)
        {
            ImGui.SameLine();
            Theme.Badge("ended", Theme.TextMuted);
        }

        var partyDps = snapshot.PartyDps(config.IncludeLimitBreakInParty);
        var threshold = config.FindThreshold(plugin.CurrentTerritory, enc.Title);
        DrawLiveCards(snapshot, partyDps, threshold);

        if (plugin.Ultimates.Current is { } phase)
            DrawPhase(phase);

        Theme.SectionHeader("Party", FontAwesomeIcon.Users);
        DrawCombatantTable(
            snapshot.Combatants.Select(c => new PullCombatant
            {
                Name = c.Name, Job = c.Job, Dps = c.EncDps, Rdps = c.HasRdps ? c.RDps : null, Breakdown = c.RdpsBreakdown, DamagePercent = c.DamagePercent,
                CritPercent = c.CritPercent, DirectHitPercent = c.DirectHitPercent, Deaths = c.Deaths, IsYou = c.IsYou,
            }));

        if (config.ShowGraph)
        {
            Theme.SectionHeader("Graph", FontAwesomeIcon.ChartLine);
            DrawLiveGraph(threshold);
        }
    }

    /// <summary>The key figures of the fight in progress, one card each.</summary>
    private void DrawLiveCards(CombatSnapshot snapshot, double partyDps, PartyDpsThreshold? threshold)
    {
        var cards = new List<System.Action<float>>();
        var you = snapshot.You;

        if (you is not null)
        {
            var rank = snapshot.YourRank();
            var party = snapshot.Combatants.Count(c => c.HasJob);
            cards.Add(w => Theme.StatCard("DPS", NumberFormat.Format(you.EncDps, NumberStyle.Grouped), Theme.Gold, w,
                                          party > 1 && rank > 0 ? $"#{rank} of {party}" : null));
            if (you.HasRdps)
                cards.Add(w => Theme.StatCard("rDPS", NumberFormat.Format(you.RDps, NumberStyle.Grouped), Theme.Crystal, w,
                                              party > 1 ? $"#{snapshot.YourRank(byRdps: true)} of {party}" : null));
        }

        if (threshold is { EffectiveMinPartyDps: > 0 } check)
        {
            var ok = partyDps >= check.EffectiveMinPartyDps;
            var color = ok ? Theme.Good : Theme.Bad;
            cards.Add(w => Theme.StatCard("Party DPS", NumberFormat.Format(partyDps, NumberStyle.Grouped), color, w,
                                          $"need {NumberFormat.Format(check.EffectiveMinPartyDps, NumberStyle.Grouped)} "
                                          + $"({partyDps / check.EffectiveMinPartyDps - 1:+0.0%;-0.0%})", color));
        }
        else
        {
            cards.Add(w => Theme.StatCard("Party DPS", NumberFormat.Format(partyDps, NumberStyle.Grouped), Theme.Gold, w));
        }

        if (plugin.Ultimates.Current is null && plugin.Boss.Current is { } boss)
        {
            string? hint = null;
            Vector4? hintColor = null;
            if (snapshot.IsActive && BossTracker.ProjectedKill(boss, snapshot.Encounter.Duration, partyDps) is { } eta)
            {
                hint = $"kill in {NumberFormat.Duration(eta)}";
                if (threshold is { EnrageSeconds: > 0 })
                {
                    hint += $" / enrage {NumberFormat.Duration(TimeSpan.FromSeconds(threshold.EnrageSeconds))}";
                    hintColor = eta.TotalSeconds <= threshold.EnrageSeconds ? Theme.Good : Theme.Bad;
                }
            }

            cards.Add(w => Theme.StatCard(boss.Name, $"{boss.HpFraction:0.0%}", Theme.Bad, w, hint, hintColor));
        }

        if (plugin.Uptime.Snapshot() is { AvailableSeconds: >= 5 } up)
            cards.Add(w => Theme.StatCard("GCD uptime", $"{up.Uptime:0.0%}", Theme.Crystal, w, $"lost {up.LostSeconds:0.0}s"));

        if (plugin.Pulls.PaceVsBest() is { } pace)
        {
            var color = pace >= 0 ? Theme.Good : Theme.Bad;
            cards.Add(w => Theme.StatCard("Pace vs best", $"{pace:+0.0%;-0.0%}", color, w, "damage so far", valueColor: color));
        }

        DrawCards(cards);
    }

    // Width of a figure card; narrower only when the window cannot fit them all.
    private const float CardWidth = 190;

    /// <summary>Lays the cards out on one line, at a fixed width.</summary>
    private static void DrawCards(List<System.Action<float>> cards)
    {
        var width = Math.Min(Theme.Px(CardWidth), Theme.CardWidth(cards.Count));
        for (var i = 0; i < cards.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine();
            cards[i](width);
        }
    }

    /// <summary>Ultimate phase: phase timer, boss HP, DPS on the boss vs the DPS needed, projected kill.</summary>
    private static void DrawPhase(PhaseView phase)
    {
        Theme.SectionHeader($"{phase.Fight.Key} {phase.Label}", FontAwesomeIcon.LayerGroup);
        if (!phase.Supported)
        {
            Theme.Hint("Phase not supported yet.");
            return;
        }

        if (phase.IsTransition)
        {
            Theme.Hint($"Transition, {NumberFormat.Duration(phase.Elapsed)}.");
            return;
        }

        if (!phase.Started)
        {
            Theme.Hint($"{phase.Phase!.Name}: waiting for the boss.");
            return;
        }

        var cards = new List<System.Action<float>>
        {
            w => Theme.StatCard(phase.Phase!.Name, $"{phase.HpFraction:0.0%}", Theme.Bad, w,
                                $"phase time {NumberFormat.Duration(phase.Elapsed)}"),
        };

        if (phase.RequiredDps is { } required)
        {
            var color = phase.BossDps >= required ? Theme.Good : Theme.Bad;
            cards.Add(w => Theme.StatCard("DPS on boss", NumberFormat.Format(phase.BossDps, NumberStyle.Grouped), color, w,
                                          $"need {NumberFormat.Format(required, NumberStyle.Grouped)} "
                                          + $"({phase.BossDps / required - 1:+0.0%;-0.0%})", color));
        }
        else
        {
            cards.Add(w => Theme.StatCard("DPS on boss", NumberFormat.Format(phase.BossDps, NumberStyle.Grouped), Theme.Gold, w,
                                          "no enrage set"));
        }

        if (phase.ProjectedKill is { } eta)
        {
            Vector4? color = phase.EnrageSeconds is { } enrage ? eta.TotalSeconds <= enrage ? Theme.Good : Theme.Bad : null;
            cards.Add(w => Theme.StatCard("Projected kill", NumberFormat.Duration(eta), color ?? Theme.Crystal, w,
                                          phase.EnrageSeconds is { } e ? $"enrage {NumberFormat.Duration(TimeSpan.FromSeconds(e))}" : null,
                                          color));
        }

        DrawCards(cards);
    }

    private void DrawLiveGraph(PartyDpsThreshold? threshold)
    {
        var window = plugin.Configuration.GraphWindowSeconds;
        graph.DrawOptions(window);

        if (graph.ShowUptime)
        {
            // Uptime of the fight in progress (or the last one).
            if (plugin.Uptime.Snapshot() is { } up)
                DpsGraph.DrawUptime("##liveuptime", [("You", up.AvailableBySecond, up.ActiveBySecond)], ImGui.GetContentRegionAvail().Y);
            else
                Theme.Hint("No fight yet.");
            return;
        }

        var pull = plugin.Pulls.Current;
        if (pull is null)
        {
            Theme.Hint("The graph shows the pull in progress; finished pulls are in the History tab.");
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
            Theme.Hint("No pull recorded yet.");
            Theme.Hint(plugin.Configuration.RecordOnlyInDuties
                           ? "Pulls are recorded inside duties only (see Settings > Pulls history)."
                           : "Pulls shorter than the minimum duration are ignored.");
            return;
        }

        var categories = plugin.Configuration.HistoryCategories;
        var inCategories = pulls.Where(p => (plugin.ContentCategories.Of(p.TerritoryId) & categories) != 0).ToList();

        DrawCategoryFilter(pulls);
        DrawHistoryFilter(inCategories);

        var visible = inCategories
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
            Theme.Hint("Select a pull to see its details. Tick \"vs\" on another one to compare.");
            return;
        }

        var compare = pulls.FirstOrDefault(p => p.Id == compareId && p.Id != selectedId);
        DrawPullDetails(selected, compare);
    }

    /// <summary>One chip per kind of content (several can be on), with the number of pulls in each.</summary>
    private void DrawCategoryFilter(IReadOnlyList<PullRecord> pulls)
    {
        var config = plugin.Configuration;
        var counts = pulls.GroupBy(p => plugin.ContentCategories.Of(p.TerritoryId)).ToDictionary(g => g.Key, g => g.Count());

        var first = true;
        foreach (var (category, label) in ContentCategories.Labels)
        {
            // Wrap to the next line when the chip would not fit.
            var text = $"{label} {counts.GetValueOrDefault(category)}";
            var width = ImGui.CalcTextSize(text).X + Theme.Px(20);
            if (!first)
            {
                ImGui.SameLine(0, Theme.Px(4));
                if (ImGui.GetContentRegionAvail().X < width)
                    ImGui.NewLine();
            }

            first = false;
            var on = (config.HistoryCategories & category) != 0;
            if (Theme.Chip(text, on))
            {
                config.HistoryCategories = on ? config.HistoryCategories & ~category : config.HistoryCategories | category;
                config.Save();
            }
        }

        ImGui.SameLine(0, Theme.Px(4));
        if (Theme.Chip(config.HistoryCategories == ContentCategory.All ? "None" : "All", false))
        {
            config.HistoryCategories = config.HistoryCategories == ContentCategory.All ? ContentCategory.None : ContentCategory.All;
            config.Save();
        }
    }

    private void DrawHistoryFilter(IReadOnlyList<PullRecord> pulls)
    {
        var titles = pulls.Select(p => p.Title).Where(t => t.Length > 0)
                          .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();

        ImGui.SetNextItemWidth(Theme.Px(260));
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

        var label = "Clear all";
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(label).X - Theme.Px(20));
        using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
        {
            if (Theme.GhostButton(label))
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
        // Scrolls sideways in a narrow window rather than squashing the columns.
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollX
                                      | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX;
        var innerWidth = Math.Max(ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize, Theme.Px(760));
        using var table = ImRaii.Table("##pulls", 11, flags, new Vector2(-1, -1), innerWidth);
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(1, 1); // the date and the headers stay visible while scrolling
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Encounter", ImGuiTableColumnFlags.WidthStretch, 2.4f);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("DPS", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("rDPS", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("pDPS", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn("Rank", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("vs", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        ImGui.TableHeadersRow();

        // Rows as tall as the checkbox and the delete button, everything else centered on that height.
        var rowHeight = ImGui.GetFrameHeight();
        var iconSize = ImGui.GetTextLineHeight();
        var framePadding = ImGui.GetStyle().FramePadding.Y;
        using var selectableAlign = ImRaii.PushStyle(ImGuiStyleVar.SelectableTextAlign, new Vector2(0, 0.5f));

        void Text(string text, Vector4? color = null)
        {
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(color ?? Theme.Text, text);
        }

        Guid? toDelete = null;
        foreach (var p in pulls)
        {
            using var id = ImRaii.PushId(p.Id.ToString());
            ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);

            ImGui.TableNextColumn();
            if (ImGui.Selectable(p.StartUtc.ToLocalTime().ToString("dd/MM HH:mm"), p.Id == selectedId,
                                 ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap, new Vector2(0, rowHeight)))
            {
                selectedId = p.Id;
            }

            Text(p.Title.Length > 0 ? p.Title : p.Zone);
            Text(NumberFormat.Duration(TimeSpan.FromSeconds(p.DurationSeconds)), Theme.TextMuted);
            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + framePadding - Theme.Px(1));
            switch (p.Outcome)
            {
                case PullOutcome.Kill:
                    Theme.Badge("Kill", Theme.Good);
                    break;
                case PullOutcome.Wipe:
                    Theme.Badge("Wipe", Theme.Bad);
                    break;
                default:
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Theme.Px(1));
                    ImGui.TextColored(Theme.TextFaint, "-");
                    break;
            }

            // The icon is drawn by hand next to text aligned like the other cells (the row's text baseline also
            // applies to anything placed after an image, which pushed the text down).
            ImGui.TableNextColumn();
            var cell = ImGui.GetCursorScreenPos();
            JobInfo.DrawIconAt(p.Job, cell with { Y = cell.Y + framePadding }, iconSize);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + iconSize + Theme.Px(4));
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(p.Job);
            Text(NumberFormat.Format(p.YourDps, NumberStyle.Grouped));
            Text(p.YourRdps is { } rd ? NumberFormat.Format(rd, NumberStyle.Grouped) : "-");
            Text(NumberFormat.Format(p.PartyDps, NumberStyle.Grouped));
            Text(p.PartySize > 1 && p.Rank > 0 ? $"{p.Rank}/{p.PartySize}" : "-", Theme.TextMuted);

            ImGui.TableNextColumn();
            var isCompare = compareId == p.Id;
            if (ImGui.Checkbox("##cmp", ref isCompare))
                compareId = isCompare ? p.Id : null;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Compare the selected pull with this one.");

            ImGui.TableNextColumn();
            if (Theme.IconButton("del", FontAwesomeIcon.TrashAlt, "Delete this pull", Theme.TextFaint))
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

    /// <summary>A pull that wiped to the enrage gives its exact timing: one click stores it in the DPS check.</summary>
    private void DrawSetEnrageButton(PullRecord pull)
    {
        var config = plugin.Configuration;
        var duration = Math.Round(pull.DurationSeconds);
        var current = config.FindThreshold(pull.TerritoryId, pull.Title)?.EnrageSeconds ?? 0;
        var durationText = NumberFormat.Duration(TimeSpan.FromSeconds(duration));

        using (ImRaii.Disabled(current == duration))
        {
            if (Theme.GhostButton($"Set as enrage time ({durationText})"))
            {
                config.SetEnrage(pull.TerritoryId, pull.Title, duration,
                                  plugin.Fights.All.FirstOrDefault(f => f.TerritoryId == pull.TerritoryId)?.Name);
                plugin.OnConfigChanged();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "Use this pull's duration as the enrage time of this fight (DPS check tab), e.g. after a wipe to the enrage.\n"
                + (current > 0
                       ? $"Current enrage time: {NumberFormat.Duration(TimeSpan.FromSeconds(current))}."
                       : "No enrage time yet: a DPS check is created for this fight."));
        }
    }

    private void DrawPullDetails(PullRecord pull, PullRecord? compare)
    {
        var config = plugin.Configuration;

        ImGui.TextColored(Theme.GoldBright, pull.Title.Length > 0 ? pull.Title : pull.Zone);
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextMuted, $"{pull.StartUtc.ToLocalTime():g}");
        ImGui.SameLine();
        Theme.Badge(pull.Source, Theme.TextMuted);
        ImGui.SameLine();
        DrawSetEnrageButton(pull);

        DrawPullCards(pull, compare);

        Theme.SectionHeader("Party", FontAwesomeIcon.Users);
        DrawCombatantTable(pull.Combatants);

        if (!config.ShowGraph)
            return;

        Theme.SectionHeader("Graph", FontAwesomeIcon.ChartLine);
        var window = config.GraphWindowSeconds;
        graph.DrawOptions(window);

        if (graph.ShowUptime)
        {
            var uptimes = new List<(string, float[], float[])> { ("This pull", pull.UptimeAvailableBySecond, pull.UptimeActiveBySecond) };
            if (compare is { UptimeAvailableBySecond.Length: > 0 })
                uptimes.Add(("Compared", compare.UptimeAvailableBySecond, compare.UptimeActiveBySecond));
            DpsGraph.DrawUptime("##historyuptime", uptimes, ImGui.GetContentRegionAvail().Y);
            return;
        }

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

    /// <summary>The pull's figures, with the gap to the compared pull under each.</summary>
    private void DrawPullCards(PullRecord pull, PullRecord? compare)
    {
        static (string?, Vector4?) Gap(double value, double? other, bool rate = false)
        {
            if (other is not > 0)
                return (null, null);
            var diff = rate ? (value - other.Value) * 100 : value / other.Value - 1;
            var text = rate ? $"{diff:+0.0;-0.0} pts vs compared" : $"{diff:+0.0%;-0.0%} vs compared";
            return (text, diff >= 0 ? Theme.Good : Theme.Bad);
        }

        var cards = new List<System.Action<float>>();

        var (dpsHint, dpsColor) = Gap(pull.YourDps, compare?.YourDps);
        if (dpsHint is null && pull.PartySize > 1 && pull.Rank > 0)
            dpsHint = $"#{pull.Rank} of {pull.PartySize}";
        cards.Add(w => Theme.StatCard("DPS", NumberFormat.Format(pull.YourDps, NumberStyle.Grouped), Theme.Gold, w, dpsHint, dpsColor));

        if (pull.YourRdps is { } rdps)
        {
            var (hint, color) = Gap(rdps, compare?.YourRdps);
            if (hint is null && pull.PartySize > 1 && pull.RdpsRank > 0)
                hint = $"#{pull.RdpsRank} of {pull.PartySize}";
            cards.Add(w => Theme.StatCard("rDPS", NumberFormat.Format(rdps, NumberStyle.Grouped), Theme.Crystal, w, hint, color));
        }

        var (partyHint, partyColor) = Gap(pull.PartyDps, compare?.PartyDps);
        cards.Add(w => Theme.StatCard("Party DPS", NumberFormat.Format(pull.PartyDps, NumberStyle.Grouped), Theme.Gold, w, partyHint, partyColor));

        var durationHint = compare is not null ? $"vs {NumberFormat.Duration(TimeSpan.FromSeconds(compare.DurationSeconds))}" : null;
        cards.Add(w => Theme.StatCard("Duration", NumberFormat.Duration(TimeSpan.FromSeconds(pull.DurationSeconds)),
                                      pull.Outcome switch { PullOutcome.Kill => Theme.Good, PullOutcome.Wipe => Theme.Bad, _ => Theme.TextMuted },
                                      w, durationHint));

        if (pull.Uptime is { } gcd)
        {
            var (hint, color) = Gap(gcd, compare?.Uptime, rate: true);
            hint ??= $"lost {pull.UptimeLostSeconds:0.0}s";
            cards.Add(w => Theme.StatCard("GCD uptime", $"{gcd:0.0%}", Theme.Crystal, w, hint, color));
        }

        DrawCards(cards);

        if (pull.PhaseUptimes.Count > 0)
        {
            Theme.Hint("GCD per phase: " + string.Join("   ", pull.PhaseUptimes.Select(
                                               p => $"{UltimateDefinitions.Label(pull.TerritoryId, p.Number)} {p.Uptime:0.0%}")));
        }
    }

    // ---------------------------------------------------------------- Shared

    /// <summary>
    /// Player table, sized to its rows: job icon, name over a damage bar in the role's color, then the figures.
    /// </summary>
    private void DrawCombatantTable(IEnumerable<PullCombatant> combatants)
    {
        var rows = combatants.ToList();
        var showRdps = rows.Any(c => c.Rdps is not null);

        // Sorting only matters when there is an rDPS column to sort by.
        var config = plugin.Configuration;
        if (showRdps)
        {
            ImGui.TextColored(Theme.TextMuted, "Sort by");
            ImGui.SameLine();
            if (Theme.Chip("DPS", !config.SortByRdps) && config.SortByRdps)
            {
                config.SortByRdps = false;
                config.Save();
            }

            ImGui.SameLine(0, Theme.Px(4));
            if (Theme.Chip("rDPS", config.SortByRdps) && !config.SortByRdps)
            {
                config.SortByRdps = true;
                config.Save();
            }
        }

        var byRdps = showRdps && config.SortByRdps;
        rows = byRdps
                   ? rows.OrderByDescending(c => c.Rdps ?? c.Dps).ToList()
                   : rows.OrderByDescending(c => c.Dps).ToList();
        var top = rows.Count > 0 ? rows.Max(c => byRdps ? c.Rdps ?? c.Dps : c.Dps) : 0;

        // Share of the party's DPS and of its rDPS (the Limit Break counts in the DPS, like ACT's damage %).
        var dpsTotal = rows.Sum(c => c.Dps);
        var rdpsTotal = rows.Sum(c => c.Rdps ?? 0);
        static string Share(double value, double total) => total > 0 ? $"{value / total:0.0%}" : "-";

        // Without scrolling and with a height of 0, the table takes exactly the height of its rows. In a narrow window it
        // keeps a readable width and scrolls sideways instead, which needs an explicit height.
        var iconSize = ImGui.GetTextLineHeight() + Theme.Px(4);
        var minWidth = Theme.Px(showRdps ? 720 : 600);
        var narrow = ImGui.GetContentRegionAvail().X < minWidth;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX;
        var height = 0f;
        if (narrow)
        {
            flags |= ImGuiTableFlags.ScrollX;
            var cell = ImGui.GetStyle().CellPadding.Y * 2;
            height = ImGui.GetTextLineHeight() + cell + rows.Count * (iconSize + Theme.Px(4) + cell)
                     + ImGui.GetStyle().ScrollbarSize + Theme.Px(2);
        }

        using var table = ImRaii.Table("##combatants", showRdps ? 9 : 7, flags, new Vector2(-1, height), narrow ? minWidth : 0);
        if (!table)
            return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, Theme.Px(22));
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 3.4f);
        ImGui.TableSetupColumn("DPS", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn("DPS %", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        if (showRdps)
        {
            ImGui.TableSetupColumn("rDPS", ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableSetupColumn("rDPS %", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        }

        ImGui.TableSetupColumn("Crit", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("DH", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Deaths", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Deaths").X + Theme.Px(8));
        ImGui.TableHeadersRow();

        var rank = 0;
        foreach (var c in rows)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, iconSize + Theme.Px(4));
            var hasJob = c.Job.Length > 0;
            if (hasJob)
                rank++;

            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(Theme.TextFaint, hasJob ? rank.ToString() : "");

            // Damage bar behind the name, as wide as the share of the top player's value.
            ImGui.TableNextColumn();
            var value = byRdps ? c.Rdps ?? c.Dps : c.Dps;
            var start = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            if (top > 0)
            {
                var barColor = JobInfo.RoleColor(c.Job);
                var end = start + new Vector2(width * (float)Math.Clamp(value / top, 0, 1), iconSize);
                ImGui.GetWindowDrawList().AddRectFilled(start, end, Theme.U32(barColor, c.IsYou ? 0.34f : 0.2f), Theme.Px(3));
            }

            JobInfo.DrawIcon(c.Job, iconSize);
            ImGui.SameLine(0, Theme.Px(6));
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(c.IsYou ? Theme.GoldBright : Theme.Text, c.Name);

            void Cell(string text, Vector4? color = null)
            {
                ImGui.TableNextColumn();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2);
                ImGui.TextColored(color ?? (c.IsYou ? Theme.GoldBright : Theme.Text), text);
            }

            Cell(NumberFormat.Format(c.Dps, NumberStyle.Grouped));
            Cell(Share(c.Dps, dpsTotal), Theme.TextMuted);
            if (showRdps)
            {
                Cell(c.Rdps is { } r ? NumberFormat.Format(r, NumberStyle.Grouped) : "-");
                if (c.Breakdown is { } b && ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        $"Given to others' buffs: -{NumberFormat.Format(b.GivenDps, NumberStyle.Grouped)} DPS\n"
                        + $"Received from own buffs: +{NumberFormat.Format(b.ReceivedDps, NumberStyle.Grouped)} DPS\n"
                        + $"Crit rate used: {b.CritRate:0.0%}   DH rate used: {b.DirectHitRate:0.0%}\n"
                        + $"({b.Samples} hits observed; exact stats are used for you at level 100)");
                }

                Cell(c.Rdps is { } own ? Share(own, rdpsTotal) : "-", Theme.TextMuted);
            }

            Cell($"{c.CritPercent:0}%", Theme.TextMuted);
            Cell($"{c.DirectHitPercent:0}%", Theme.TextMuted);
            Cell(c.Deaths.ToString(), c.Deaths > 0 ? Theme.Bad : Theme.TextFaint);
        }
    }
}
