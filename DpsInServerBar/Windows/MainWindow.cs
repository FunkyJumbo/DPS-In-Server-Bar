using System;
using System.Collections.Generic;
using System.Globalization;
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
    private bool showProgression;
    private string groupSearch = string.Empty;
    private string encounterSearch = string.Empty;
    private bool progressionByTime;
    private Guid? lastTickedPull; // anchor of a Shift+click range in the Pulls pop-up
    private string groupName = string.Empty;

    /// <summary>Height the selected pull's details need to be fully visible (graph at its smallest), from the last frame.</summary>
    private float detailsHeight;
    private IDisposable? themeScope;

    public MainWindow(Plugin plugin) : base("Raid Companion###DpsInServerBarMain")
    {
        this.plugin = plugin;
        AllowBackgroundBlur = false;
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

        if (Theme.Chip("Compact view", plugin.CompactOpen))
            plugin.SetCompact(!plugin.CompactOpen);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only the party's DPS meter, in a small pop-up window.");

        ImGui.SameLine();
        if (Theme.Chip("Opener practice", plugin.OpenerOpen))
            plugin.ToggleOpener();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Practice your job's opener: each action used in the right order is checked off.");

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

        var liveDeaths = plugin.Sources.Native.PartyDeaths.Since(snapshot.StartUtc);
        if (liveDeaths.Count > 0)
            DrawDeaths(liveDeaths);

        if (plugin.Sources.Native.Mistakes.Current is { Count: > 0 } mistakes)
            DrawMistakes(mistakes, timeline: false);

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

        if (plugin.Uptime.Snapshot() is { AvailableSeconds: >= 5 } gcdState)
            DrawGcdAnalysis(gcdState.Gcd, showGaps: false);

        if (plugin.Uptime.Snapshot() is { AvailableSeconds: >= 5 } && snapshot.You is { } me && GaugeTracker.Tracks(me.Job))
            DrawGaugeWaste(plugin.Gauges.Current);

        DrawPositionals(plugin.Positionals.Current);
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
                DpsGraph.DrawUptime("##liveuptime", [("You", up.AvailableBySecond, up.ActiveBySecond)], ImGui.GetContentRegionAvail().Y, up.Gcd.Gaps,
                                     plugin.Sources.Latest is { } latest ? plugin.Sources.Native.PartyDeaths.Since(latest.StartUtc) : null);
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
                      graph.ShowParty ? threshold?.EffectiveMinPartyDps : null, plugin.Sources.Native.PartyDeaths.Since(pull.StartUtc));
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

        if (showProgression)
        {
            DrawProgression(inCategories);
            return;
        }

        var visible = inCategories
            .Where(p => historyFilter.Length == 0 || Progression.Encounter(p).Equals(historyFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var selected = pulls.FirstOrDefault(p => p.Id == selectedId);

        // Pull list: room for the headers and at least 3 pulls, even in a small window. Without a selected pull it
        // takes all the room left (the details would be empty), with one it leaves most of the room to the details.
        var style = ImGui.GetStyle();
        var minHeight = ImGui.GetTextLineHeight() + style.CellPadding.Y * 2
                        + 3 * (ImGui.GetFrameHeight() + style.CellPadding.Y * 2)
                        + style.ScrollbarSize + style.WindowPadding.Y * 2 + Theme.Px(2);
        var avail = ImGui.GetContentRegionAvail().Y;
        var listHeight = selected is null
            ? Math.Max(avail - ImGui.GetTextLineHeightWithSpacing() - style.ItemSpacing.Y, minHeight)
            : Math.Max(minHeight, avail - detailsHeight - style.ItemSpacing.Y);
        // With a pull selected, the list stays at its smallest until all the details below fit; past that, all the
        // extra room goes to the list (the details keep their size, the graph its smallest height).

        using (var child = ImRaii.Child("##pulllist", new Vector2(-1, listHeight), true))
        {
            if (child)
                DrawPullTable(visible);
        }

        if (selected is null)
        {
            Theme.Hint("Select a pull to see its details. Tick \"vs\" on another one to compare.");
            return;
        }

        var compare = pulls.FirstOrDefault(p => p.Id == compareId && p.Id != selectedId);
        DrawPullDetails(selected, compare);
    }

    /// <summary>
    /// Progression on the chosen encounter: key figures, how far each pull got (and the best so far), the phases
    /// reached in an ultimate and what wiped the party.
    /// </summary>
    private void DrawProgression(IReadOnlyList<PullRecord> pulls)
    {
        if (historyFilter.Length == 0)
        {
            Theme.Hint("Choose an encounter above to follow its progression pull after pull.");
            return;
        }

        var fight = Progression.PullsOf(pulls, historyFilter);
        if (fight.Count == 0)
        {
            Theme.Hint("No pull of this encounter in the ticked kinds of content.");
            return;
        }

        var groups = Progression.Groups(fight, plugin.Configuration.SavedGroups);
        var group = groups.FirstOrDefault(g => g.Key == plugin.Configuration.ProgressionGroup);
        var chosen = group?.Pulls ?? fight;
        DrawProgressionSelection(groups, group, chosen);

        var prog = chosen.Where(p => !p.ExcludedFromProgression).ToList();
        if (prog.Count == 0)
        {
            Theme.Hint("Every pull is left out: tick some in Pulls.");
            return;
        }

        var territory = prog[^1].TerritoryId;
        var ultimate = Progression.IsUltimate(territory);
        var scores = prog.Select(Progression.Score).ToList();
        var kills = prog.Count(p => p.Outcome == PullOutcome.Kill);
        var best = prog.Where(p => Progression.Score(p) is not null).MaxBy(p => Progression.Score(p)!.Value);

        var cards = new List<System.Action<float>>
        {
            w => Theme.StatCard("Pulls", prog.Count.ToString(), Theme.Gold, w, kills > 0 ? (kills == 1 ? "1 kill" : $"{kills} kills") : "no kill yet",
                                kills > 0 ? Theme.Good : null),
        };
        if (best is not null)
        {
            var number = prog.IndexOf(best) + 1;
            cards.Add(w => Theme.StatCard("Best pull", Progression.Label(best), Theme.Good, w,
                                          $"pull {number}, {best.StartUtc.ToLocalTime():dd/MM HH:mm}"));
        }

        var recent = prog.TakeLast(10).Where(p => Progression.Score(p) is not null).ToList();
        if (recent.Count > 0 && recent.MaxBy(p => Progression.Score(p)!.Value) is { } recentBest)
            cards.Add(w => Theme.StatCard("Best of last 10", Progression.Label(recentBest), Theme.Crystal, w));

        var instanceHours = Progression.InstanceHours(prog);
        var played = TimeSpan.FromSeconds(prog.Sum(p => p.DurationSeconds));
        cards.Add(w => Theme.StatCard("Time in fight", NumberFormat.Duration(played), Theme.Gold, w,
                                      $"{NumberFormat.Duration(TimeSpan.FromHours(instanceHours[^1]))} in the instance since {prog[0].StartUtc.ToLocalTime():dd/MM}"));
        if (prog.Where(p => p.Outcome != PullOutcome.Kill && p.LastBossCast.Length > 0)
                .MaxBy(p => (Progression.Phase(p), p.DurationSeconds)) is { } furthest)
        {
            var number = prog.IndexOf(furthest) + 1;
            cards.Add(w => Theme.StatCard("Furthest mechanic", furthest.LastBossCast, Theme.Crystal, w,
                                          $"pull {number}, at {NumberFormat.Duration(TimeSpan.FromSeconds(furthest.DurationSeconds))}"));
        }

        DrawCards(cards);

        if (ultimate)
        {
            var phases = prog.Where(p => p.Outcome != PullOutcome.Kill)
                             .GroupBy(Progression.Phase).OrderBy(g => g.Key)
                             .Select(g => $"{UltimateDefinitions.Label(territory, g.Key)} ×{g.Count()}");
            Theme.Hint("Wipes per phase: " + string.Join("   ", phases));
        }

        // What wiped you, most common first.
        var causes = prog.Select(p => (Pull: p, Cause: Progression.WipeCause(p)))
                         .Where(c => c.Cause is not null)
                         .GroupBy(c => c.Cause!)
                         .Select(g => (Cause: g.Key, Count: g.Count(), Last: prog.IndexOf(g.Last().Pull) + 1))
                         .OrderByDescending(c => c.Count)
                         .Take(8)
                         .ToList();

        Theme.SectionHeader("Wiped by", FontAwesomeIcon.Skull);
        var wipes = prog.Count(p => p.Outcome != PullOutcome.Kill);
        ImGui.TextColored(Theme.TextMuted, wipes == 1 ? "1 pull without a kill" : $"{wipes} pulls without a kill");
        Theme.HelpMarker("For each wipe, the most common cause of death in its last 20 seconds (or its first death). "
                         + "Needs the deaths of the party, recorded since this version.");
        if (causes.Count == 0)
        {
            Theme.Hint("No wipe with recorded deaths yet.");
        }
        else
        {
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX;
            using var table = ImRaii.Table("##wipecauses", 3, flags);
            if (table)
            {
                ImGui.TableSetupColumn("Cause", ImGuiTableColumnFlags.WidthStretch, 4f);
                ImGui.TableSetupColumn("Wipes", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Wipes").X + Theme.Px(8));
                ImGui.TableSetupColumn("Last pull", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Last pull").X + Theme.Px(8));
                ImGui.TableHeadersRow();
                foreach (var c in causes)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Bad, c.Cause);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(c.Count.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextMuted, $"#{c.Last}");
                }
            }
        }

        Theme.SectionHeader("Pull after pull", FontAwesomeIcon.ChartLine);
        if (Theme.Chip("Per pull", !progressionByTime))
            progressionByTime = false;
        ImGui.SameLine(0, Theme.Px(4));
        if (Theme.Chip("Per time in the instance", progressionByTime))
            progressionByTime = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hours spent in the instance: the fights and the time between pulls of a same evening (breaks of "
                             + "more than 30 minutes left out).");

        var xs = progressionByTime ? instanceHours.ToList() : Enumerable.Range(1, prog.Count).Select(i => (double)i).ToList();
        var clicked = DpsGraph.DrawProgression(
            "##progression", xs, progressionByTime ? "Hours in the instance" : "Pull", scores,
            prog.Select(p => p.Outcome == PullOutcome.Kill).ToList(), Progression.Max(territory),
            ultimate ? Progression.PhaseNames(territory) : null, ImGui.GetContentRegionAvail().Y,
            i => $"Pull {i + 1}  {NumberFormat.Duration(TimeSpan.FromHours(instanceHours[i]))} in the instance\n{prog[i].StartUtc.ToLocalTime():dd/MM HH:mm}  {NumberFormat.Duration(TimeSpan.FromSeconds(prog[i].DurationSeconds))}\n"
                 + Progression.Label(prog[i])
                 + (Progression.WipeCause(prog[i]) is { } cause ? $"\nWiped by {cause}" : string.Empty)
                 + (prog[i].Outcome != PullOutcome.Kill && prog[i].LastBossCast.Length > 0 ? $"\nLast enemy cast: {prog[i].LastBossCast}" : string.Empty)
                 + "\nClick to open this pull.");
        if (clicked is { } index)
        {
            selectedId = prog[index].Id;
            showProgression = false;
        }
    }

    /// <summary>
    /// Which pulls the progression covers: a group (found from the party members of each pull) and, in a pop-up, one
    /// checkbox per pull to leave some out (saved with the history).
    /// </summary>
    private void DrawProgressionSelection(IReadOnlyList<PullGroup> groups, PullGroup? group, IReadOnlyList<PullRecord> chosen)
    {
        var config = plugin.Configuration;
        static string GroupLabel(PullGroup g) => $"{g.Label} ({g.Pulls.Count})";

        void Choose(string key)
        {
            config.ProgressionGroup = key;
            config.Save();
        }

        ImGui.SetNextItemWidth(Theme.Px(320));
        using (var combo = ImRaii.Combo("Group", group is null ? $"All pulls ({groups.Sum(g => g.Pulls.Count)})" : GroupLabel(group),
                                        ImGuiComboFlags.HeightLarge))
        {
            if (combo)
            {
                // Search by group name or player.
                if (ImGui.IsWindowAppearing())
                {
                    groupSearch = string.Empty;
                    ImGui.SetKeyboardFocusHere();
                }

                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##groupsearch", "Search a group or a player", ref groupSearch, 64);

                if (groupSearch.Length == 0 && ImGui.Selectable($"All pulls ({groups.Sum(g => g.Pulls.Count)})", group is null))
                    Choose(string.Empty);

                var shownSaved = false;
                foreach (var g in groups)
                {
                    if (groupSearch.Length > 0
                        && !g.Label.Contains(groupSearch, StringComparison.OrdinalIgnoreCase)
                        && !g.Members.Contains(groupSearch, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Saved groups first, in gold, then a rule before the automatic ones.
                    if (g.Saved is not null)
                        shownSaved = true;
                    else if (shownSaved)
                    {
                        ImGui.Separator();
                        shownSaved = false;
                    }

                    using (ImRaii.PushColor(ImGuiCol.Text, Theme.GoldBright, g.Saved is not null))
                    {
                        if (ImGui.Selectable($"{GroupLabel(g)}###{g.Key}", g == group))
                            Choose(g.Key);
                    }

                    if (ImGui.IsItemHovered() && g.Members.Length > 0)
                        ImGui.SetTooltip(g.Members);
                }
            }
        }

        if (ImGui.IsItemHovered() && group is { Members.Length: > 0 })
            ImGui.SetTooltip(group.Members);

        // Save an automatic group under a name, or rename / forget a saved one.
        if (group is { Key: not "solo" })
        {
            ImGui.SameLine(0, Theme.Px(8));
            if (Theme.GhostButton(group.Saved is null ? "Save group" : "Rename"))
            {
                groupName = group.Saved?.Name ?? string.Empty;
                ImGui.OpenPopup("##groupname");
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(group.Saved is null
                                     ? "Give this group a name, to find it easily in the list. Its regular players are kept."
                                     : "Rename this group.");
            }

            if (group.Saved is { } savedGroup)
            {
                ImGui.SameLine(0, Theme.Px(4));
                using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
                {
                    if (Theme.GhostButton("Forget"))
                    {
                        config.SavedGroups.Remove(savedGroup);
                        Choose(string.Empty);
                    }
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Hold Ctrl to forget this group's name (its pulls are kept).");
            }

            using var namePopup = ImRaii.Popup("##groupname");
            if (namePopup)
            {
                ImGui.TextColored(Theme.TextMuted, group.Saved is null ? "Name of this group" : "New name");
                if (ImGui.IsWindowAppearing())
                    ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(Theme.Px(240));
                var done = ImGui.InputTextWithHint("##groupnameinput", "e.g. Static", ref groupName, 48, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.SameLine();
                done |= Theme.GhostButton("OK");
                if (done && groupName.Trim().Length > 0)
                {
                    if (group.Saved is { } existing)
                    {
                        existing.Name = groupName.Trim();
                        config.Save();
                    }
                    else
                    {
                        var created = new SavedGroup { Name = groupName.Trim(), Players = group.Regulars.ToList() };
                        config.SavedGroups.Add(created);
                        Choose($"saved:{created.Id}");
                    }

                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.SameLine(0, Theme.Px(8));
        var excluded = chosen.Count(p => p.ExcludedFromProgression);
        if (Theme.GhostButton(excluded > 0 ? $"Pulls ({chosen.Count - excluded}/{chosen.Count})" : "Pulls"))
            ImGui.OpenPopup("##progressionpulls");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tick the pulls to count, e.g. to leave out a night with another group.");

        Theme.HelpMarker("Groups are found from the party members of each pull: a pull goes with the group it shares at least "
                         + "half of its players with. Hover a group for its players.\n"
                         + "Save a group under a name to find it easily: its regular players are kept, and the pulls they "
                         + "play together go to it from then on. Saved groups come first, in gold.");

        using var popup = ImRaii.Popup("##progressionpulls");
        if (!popup)
            return;

        if (Theme.GhostButton("Tick all"))
            SetExcluded(chosen, false);
        ImGui.SameLine();
        if (Theme.GhostButton("Untick all"))
            SetExcluded(chosen, true);
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.TextColored(Theme.TextFaint, "Shift+click: up to the last pull clicked");

        using var child = ImRaii.Child("##progressionpulllist", new Vector2(Theme.Px(460), Math.Min(Theme.Px(320), (chosen.Count + 1) * ImGui.GetFrameHeightWithSpacing())));
        if (!child)
            return;

        // Newest first, with the evening it was played and how far it got. Shift+click sets every pull between the last
        // one clicked and this one the same way.
        var list = chosen.Reverse().ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var p = list[i];
            var included = !p.ExcludedFromProgression;
            if (ImGui.Checkbox($"{p.StartUtc.ToLocalTime():dd/MM HH:mm}###{p.Id}", ref included))
            {
                var anchor = list.FindIndex(x => x.Id == lastTickedPull);
                if (ImGui.GetIO().KeyShift && anchor >= 0)
                    SetExcluded(list.GetRange(Math.Min(anchor, i), Math.Abs(anchor - i) + 1), !included);
                else
                    SetExcluded([p], !included);
                lastTickedPull = p.Id;
            }
            ImGui.SameLine(Theme.Px(150));
            ImGui.TextColored(p.Outcome == PullOutcome.Kill ? Theme.Good : Theme.Text, Progression.Label(p));
            ImGui.SameLine(Theme.Px(270));
            ImGui.TextColored(Theme.TextFaint, groups.FirstOrDefault(g => g.Pulls.Contains(p))?.Label ?? string.Empty);
        }
    }

    private void SetExcluded(IEnumerable<PullRecord> pulls, bool excluded)
    {
        foreach (var p in pulls)
            p.ExcludedFromProgression = excluded;
        plugin.History.Changed();
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
        var titles = pulls.Select(Progression.Encounter).Where(t => t.Length > 0)
                          .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();

        ImGui.SetNextItemWidth(Theme.Px(260));
        using (var combo = ImRaii.Combo("Encounter", historyFilter.Length == 0 ? "All" : historyFilter, ImGuiComboFlags.HeightLarge))
        {
            if (combo)
            {
                // Type to filter the fights.
                if (ImGui.IsWindowAppearing())
                {
                    encounterSearch = string.Empty;
                    ImGui.SetKeyboardFocusHere();
                }

                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##encountersearch", "Search a fight", ref encounterSearch, 64);

                if (encounterSearch.Length == 0 && ImGui.Selectable("All", historyFilter.Length == 0))
                    historyFilter = string.Empty;
                foreach (var title in titles)
                {
                    if (encounterSearch.Length > 0 && !title.Contains(encounterSearch, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ImGui.Selectable(title, title == historyFilter))
                        historyFilter = title;
                }
            }
        }

        ImGui.SameLine(0, Theme.Px(8));
        if (Theme.Chip("Progression", showProgression))
            showProgression = !showProgression;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How far each pull of the chosen encounter got, pull after pull, and what wiped you.");

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

            Text(Progression.IsUltimate(p.TerritoryId)
                     ? $"{Progression.Encounter(p)} · {UltimateDefinitions.Label(p.TerritoryId, Progression.Phase(p))}"
                     : p.Title.Length > 0 ? p.Title : p.Zone);
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
        var top = ImGui.GetCursorPosY();

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

        // Pulls recorded before deaths and failed mechanics were tracked have no GCD analysis either: nothing to say for them.
        if (pull.Gcd is not null)
        {
            DrawDeaths(pull.PartyDeaths);
            DrawMistakes(pull.Mistakes, timeline: true);
            if (pull.PartyDeaths.Count > 0 || pull.Mistakes.Count > 0)
                DrawTimeline(pull);
        }

        if (!config.ShowGraph)
        {
            detailsHeight = ImGui.GetCursorPosY() - top;
            return;
        }

        Theme.SectionHeader("Graph", FontAwesomeIcon.ChartLine);
        var window = config.GraphWindowSeconds;
        graph.DrawOptions(window);

        if (graph.ShowUptime)
        {
            var uptimes = new List<(string, float[], float[])> { ("This pull", pull.UptimeAvailableBySecond, pull.UptimeActiveBySecond) };
            if (compare is { UptimeAvailableBySecond.Length: > 0 })
                uptimes.Add(("Compared", compare.UptimeAvailableBySecond, compare.UptimeActiveBySecond));
            detailsHeight = ImGui.GetCursorPosY() - top + DpsGraph.MinHeight;
            DpsGraph.DrawUptime("##historyuptime", uptimes, ImGui.GetContentRegionAvail().Y, pull.Gcd?.Gaps, pull.PartyDeaths);
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
        detailsHeight = ImGui.GetCursorPosY() - top + DpsGraph.MinHeight;
        DpsGraph.Draw("##historygraph", series, ImGui.GetContentRegionAvail().Y, threshold, pull.PartyDeaths);
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

        if (pull.Gcd is { } analysis)
            DrawGcdAnalysis(analysis, showGaps: true);

        if (pull.Gcd is not null && GaugeTracker.Tracks(pull.Job))
            DrawGaugeWaste(pull.GaugeWaste);

        DrawPositionals(pull.Positionals);
    }

    /// <summary>One line with your positionals hit, then the actions that missed some (hover for when).</summary>
    private static void DrawPositionals(IReadOnlyList<PositionalUse> uses)
    {
        if (uses.Count == 0)
            return;

        var hits = uses.Count(u => u.Hit);
        var share = (double)hits / uses.Count;
        ImGui.TextColored(Theme.TextMuted, "Positionals");
        ImGui.SameLine(0, Theme.Px(6));
        ImGui.TextColored(share >= 1 ? Theme.Good : share >= 0.9 ? Theme.Warn : Theme.Bad, $"{hits}/{uses.Count} ({share:0%})");

        foreach (var action in uses.GroupBy(u => u.Action).Where(g => g.Any(u => !u.Hit)).OrderByDescending(g => g.Count(u => !u.Hit)))
        {
            var missed = action.Where(u => !u.Hit).ToList();
            ImGui.SameLine(0, Theme.Px(10));
            ImGui.TextColored(Theme.Warn, $"{action.Key} {missed.Count}/{action.Count()} missed");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"{action.Key} ({(missed[0].Rear ? "rear" : "flank")}) missed at "
                                 + string.Join(", ", missed.Select(u => NumberFormat.Duration(TimeSpan.FromSeconds(u.AtSeconds)))));
            }
        }

        Theme.HelpMarker(
            "Your positional actions whose positional bonus landed, read from the bonus the game gave each hit "
            + "(True North and bosses without positionals count as hits).\n"
            + "Hover an action for when you missed it.");
    }

    /// <summary>One line with the gauge lost to overcapping, per gauge, and the actions that overcapped.</summary>
    private static void DrawGaugeWaste(IReadOnlyList<GaugeWaste> waste)
    {
        ImGui.TextColored(Theme.TextMuted, "Gauge overcap");
        if (waste.Count == 0)
        {
            ImGui.SameLine(0, Theme.Px(6));
            ImGui.TextColored(Theme.Good, "none");
        }

        foreach (var w in waste)
        {
            ImGui.SameLine(0, Theme.Px(6));
            var amount = w.Max <= 5 ? $"{w.Wasted:0.#}" : $"{w.Wasted:0}";
            ImGui.TextColored(Theme.Warn, $"{w.Resource} {amount}");
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(Theme.TextFaint, w.Count > 1 ? $"({w.Count}×)" : "(1×)");
            if (ImGui.IsItemHovered() && w.Actions.Length > 0)
                ImGui.SetTooltip($"Overcapped by: {w.Actions}");
        }

        Theme.HelpMarker(
            "Gauge lost because it was too full when it was filled. Spend it before using the actions that fill it.\n"
            + "The gain of each action is learned from your own uses while the gauge had room, so the first uses of an "
            + "action are not counted. Lilies, Addersgall and Polyglot: time spent full, in stacks lost.\n"
            + "Hover a gauge for the actions that overcapped it.");
    }

    /// <summary>Every death of the party in time order, with its cause; hover one for the hits taken just before.</summary>
    private static void DrawDeaths(IReadOnlyList<PartyDeath> deaths)
    {
        Theme.SectionHeader("Deaths", FontAwesomeIcon.Skull);
        if (deaths.Count == 0)
            ImGui.TextColored(Theme.Good, "No death.");
        else
            ImGui.TextColored(Theme.Bad, deaths.Count == 1 ? "1 death" : $"{deaths.Count} deaths");
        Theme.HelpMarker("What killed each party member: the hit that took their last HP, or a guess when there was none "
                         + "(a debuff running out, a fall from the arena). Hover a death for the hits taken just before.\n"
                         + "Party members far from you may be missed.");
        if (deaths.Count == 0)
            return;

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX;
        using var table = ImRaii.Table("##deaths", 3, flags);
        if (!table)
            return;

        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("00:00").X + Theme.Px(12));
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Killed by", ImGuiTableColumnFlags.WidthStretch, 4f);
        ImGui.TableHeadersRow();

        for (var i = 0; i < deaths.Count; i++)
        {
            var d = deaths[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.TextFaint))
                ImGui.Selectable($"{NumberFormat.Duration(TimeSpan.FromSeconds(d.AtSeconds))}##death{i}", false,
                                 ImGuiSelectableFlags.SpanAllColumns);
            if (ImGui.IsItemHovered())
                DeathTooltip(d);

            ImGui.TableNextColumn();
            ImGui.TextColored(d.IsYou ? Theme.GoldBright : Theme.Text, d.Player);
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextFaint, d.Job);
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.Bad, d.Cause);
            if (DeathDetails(d) is { Length: > 0 } details)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextMuted, details);
            }
        }
    }

    private static string DeathDetails(PartyDeath d)
    {
        var details = new List<string>();
        if (d.Source.Length > 0)
            details.Add(d.Source);
        if (d.Damage > 0)
            details.Add(d.Damage.ToString("N0", CultureInfo.InvariantCulture));
        return details.Count > 0 ? $"({string.Join(", ", details)})" : string.Empty;
    }

    /// <summary>A death's recap: HP left, then the last hits taken, oldest first.</summary>
    private static void DeathTooltip(PartyDeath d)
    {
        using var tooltip = ImRaii.Tooltip();
        ImGui.TextColored(d.IsYou ? Theme.GoldBright : Theme.Text, d.Player);
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextMuted, $"died at {NumberFormat.Duration(TimeSpan.FromSeconds(d.AtSeconds))}");
        if (d.HpBefore > 0)
            ImGui.TextColored(Theme.TextMuted, $"HP before the last hit: {d.HpBefore.ToString("N0", CultureInfo.InvariantCulture)}");

        if (d.Recap.Count == 0)
        {
            ImGui.TextColored(Theme.TextFaint, "No hit recorded just before.");
            return;
        }

        foreach (var h in d.Recap)
        {
            ImGui.TextColored(Theme.TextFaint, $"-{h.SecondsBefore:0.0}s");
            ImGui.SameLine();
            ImGui.TextColored(Theme.Bad, h.Damage.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.SameLine();
            ImGui.TextUnformatted(h.Ability);
            if (h.Source.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextMuted, $"({h.Source})");
            }
        }
    }

    /// <summary>
    /// The pull on one bar: deaths (red, top) and failed mechanics (orange, bottom) where they happened, minute ticks,
    /// and the events under the mouse in a tooltip.
    /// </summary>
    private static void DrawTimeline(PullRecord pull)
    {
        Theme.SectionHeader("Timeline", FontAwesomeIcon.Stream);
        ImGui.TextColored(Theme.Bad, "Deaths (top)");
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.TextColored(Theme.Warn, "Failed mechanics (bottom)");

        var duration = Math.Max(1, Math.Max(pull.DurationSeconds,
                                            pull.PartyDeaths.Select(d => d.AtSeconds).Concat(pull.Mistakes.Select(m => m.AtSeconds)).DefaultIfEmpty(0).Max()));
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = Theme.Px(30);
        var pad = Theme.Px(8);
        float X(double seconds) => origin.X + pad + (float)(seconds / duration) * (width - pad * 2);

        ImGui.InvisibleButton("##timeline", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();

        dl.AddRectFilled(origin, origin + new Vector2(width, height), Theme.U32(Theme.Panel, 0.8f), Theme.Px(4));
        var mid = origin.Y + height / 2;
        dl.AddLine(new Vector2(X(0), mid), new Vector2(X(duration), mid), Theme.U32(Theme.Line), 1f);
        for (var minute = 60; minute < duration; minute += 60)
            dl.AddLine(new Vector2(X(minute), origin.Y + Theme.Px(4)), new Vector2(X(minute), origin.Y + height - Theme.Px(4)),
                       Theme.U32(Theme.Line, 0.6f), 1f);

        var radius = Theme.Px(4);
        foreach (var m in pull.Mistakes)
            dl.AddCircleFilled(new Vector2(X(m.AtSeconds), mid + height / 4), radius * 0.8f, Theme.U32(Theme.Warn, 0.9f));
        foreach (var d in pull.PartyDeaths)
            dl.AddCircleFilled(new Vector2(X(d.AtSeconds), mid - height / 4), radius, Theme.U32(Theme.Bad));

        ImGui.TextColored(Theme.TextFaint, "0:00");
        var end = NumberFormat.Duration(TimeSpan.FromSeconds(pull.DurationSeconds));
        ImGui.SameLine(width - ImGui.CalcTextSize(end).X);
        ImGui.TextColored(Theme.TextFaint, end);

        if (!hovered)
            return;

        // Everything within a few pixels of the mouse.
        var mouseX = ImGui.GetMousePos().X;
        var reach = Theme.Px(6);
        var nearDeaths = pull.PartyDeaths.Where(d => Math.Abs(X(d.AtSeconds) - mouseX) <= reach).ToList();
        var nearMistakes = pull.Mistakes.Where(m => Math.Abs(X(m.AtSeconds) - mouseX) <= reach).ToList();
        if (nearDeaths.Count == 0 && nearMistakes.Count == 0)
            return;

        using var tooltip = ImRaii.Tooltip();
        foreach (var d in nearDeaths)
        {
            ImGui.TextColored(Theme.TextFaint, NumberFormat.Duration(TimeSpan.FromSeconds(d.AtSeconds)));
            ImGui.SameLine();
            ImGui.TextColored(d.IsYou ? Theme.GoldBright : Theme.Text, d.Player);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Bad, $"died: {d.Cause}");
            if (DeathDetails(d) is { Length: > 0 } details)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextMuted, details);
            }
        }

        foreach (var m in nearMistakes)
        {
            ImGui.TextColored(Theme.TextFaint, NumberFormat.Duration(TimeSpan.FromSeconds(m.AtSeconds)));
            ImGui.SameLine();
            ImGui.TextColored(m.IsYou ? Theme.GoldBright : Theme.Text, m.Player);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warn, m.Status);
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextMuted, $"from {m.Ability}");
        }
    }

    /// <summary>
    /// Failed mechanics: per player, the penalty debuffs taken (most first); in the history, every one in time order.
    /// </summary>
    private static void DrawMistakes(IReadOnlyList<MechanicMistake> mistakes, bool timeline)
    {
        Theme.SectionHeader("Failed mechanics", FontAwesomeIcon.ExclamationTriangle);
        if (mistakes.Count == 0)
            ImGui.TextColored(Theme.Good, "No failed mechanic.");
        else
            ImGui.TextColored(Theme.Bad, mistakes.Count == 1 ? "1 failed mechanic" : $"{mistakes.Count} failed mechanics");
        Theme.HelpMarker("Penalty debuffs put on party members by enemy actions: Vulnerability Up, Damage Down, Down for the "
                         + "Count. Tankbuster vulnerabilities on tanks can be ignored in Settings > Pulls history.");
        if (mistakes.Count == 0)
            return;

        var players = mistakes.GroupBy(m => m.Player)
                              .Select(g => (Player: g.Key, g.First().Job, g.First().IsYou, Count: g.Count(),
                                            Debuffs: string.Join(", ", g.GroupBy(m => m.Status)
                                                                        .OrderByDescending(s => s.Count())
                                                                        .Select(s => s.Count() > 1 ? $"{s.Key} ×{s.Count()}" : s.Key))))
                              .OrderByDescending(p => p.Count)
                              .ToList();

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX;
        using (var table = ImRaii.Table("##mistakes", 3, flags))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 2f);
                ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Count").X + Theme.Px(8));
                ImGui.TableSetupColumn("Debuffs", ImGuiTableColumnFlags.WidthStretch, 4f);
                ImGui.TableHeadersRow();

                foreach (var p in players)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(p.IsYou ? Theme.GoldBright : Theme.Text, p.Player);
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.TextFaint, p.Job);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Bad, p.Count.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextMuted, p.Debuffs);
                }
            }
        }

        if (!timeline)
            return;

        using var node = ImRaii.TreeNode($"Timeline ({mistakes.Count})###mistaketimeline");
        if (!node)
            return;

        foreach (var m in mistakes)
        {
            ImGui.TextColored(Theme.TextFaint, NumberFormat.Duration(TimeSpan.FromSeconds(m.AtSeconds)));
            ImGui.SameLine();
            ImGui.TextColored(m.IsYou ? Theme.GoldBright : Theme.Text, m.Player);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Bad, m.Status);
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextMuted, m.Source.Length > 0 ? $"from {m.Ability} ({m.Source})" : $"from {m.Ability}");
        }
    }

    /// <summary>
    /// One line splitting the lost GCD time by cause, with a help tooltip; in the history, the longest gaps too.
    /// </summary>
    private static void DrawGcdAnalysis(GcdAnalysis gcd, bool showGaps)
    {
        if (gcd.GcdCount == 0)
            return;

        void Part(string label, double seconds, string? extra = null, bool first = false)
        {
            if (!first)
            {
                ImGui.SameLine(0, Theme.Px(4));
                ImGui.TextColored(Theme.TextFaint, "·");
                ImGui.SameLine(0, Theme.Px(4));
            }

            ImGui.TextColored(Theme.TextMuted, label);
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(seconds >= 0.05 ? Theme.Text : Theme.TextFaint, $"{seconds:0.0}s{extra}");
        }

        Part("Clipping", gcd.ClipSeconds, gcd.ClipCount > 0 ? $" ({gcd.ClipCount}×)" : null, first: true);
        Part("Late", gcd.LateSeconds);
        Part("No GCD", gcd.NoGcdSeconds, gcd.NoGcdCount > 0 ? $" ({gcd.NoGcdCount}×)" : null);
        Part("Dead", gcd.DeadSeconds);
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.TextColored(gcd.LostGcds >= 1 ? Theme.Warn : Theme.Good, $"~{gcd.LostGcds:0.0} GCD lost");
        Theme.HelpMarker(
            $"{gcd.GcdCount} GCDs used, {gcd.AverageRecast:0.00}s each on average. Time with the boss targetable and your GCD ready but unused:\n"
            + "- Clipping: held by an oGCD's animation lock. Weave fewer oGCDs per GCD, or earlier.\n"
            + "- Late: free to act, the GCD just came late (reaction, ping, a short step).\n"
            + "- No GCD: a whole GCD or more without one (movement, mechanics). Slidecast, use instant GCDs while moving.\n"
            + "- Dead: time spent dead.");

        if (!showGaps || gcd.Gaps.Count == 0)
            return;

        var longest = gcd.Gaps.OrderByDescending(g => g.Seconds).Take(5).OrderBy(g => g.AtSeconds);
        Theme.Hint("Longest gaps: " + string.Join("   ", longest.Select(g =>
            $"{NumberFormat.Duration(TimeSpan.FromSeconds(g.AtSeconds))} {DpsGraph.GapLabel(g.Kind)} {g.Seconds:0.0}s")));
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
