using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Combat;
using DpsInServerBar.Display;
using DpsInServerBar.Native;
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
    // The Progression tab: the History's filters, then the progression of the chosen encounter.
    private bool ShowProgression => selectedTab == 2;
    private string groupSearch = string.Empty;
    private string encounterSearch = string.Empty;
    private bool progressionByTime;
    private Guid? lastTickedPull; // anchor of a Shift+click range in the Pulls pop-up
    private string groupName = string.Empty;

    /// <summary>Height the selected pull's details need to be fully visible (graph at its smallest), from the last frame.</summary>
    private float detailsHeight;

    /// <summary>Height added to the pull list by dragging its grip.</summary>
    private float pullListExtra;

    // Height the window needs to show everything, from the last frame (pixels); null when a part grows with the
    // window (a graph): the window then has no height limit.
    private float? fitHeight;
    private bool liveElastic;
    private IDisposable? themeScope;

    public MainWindow(Plugin plugin) : base("Raid Companion###DpsInServerBarMain")
    {
        this.plugin = plugin;
        AllowBackgroundBlur = false;
        Size = new Vector2(760, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void ShowHistory()
    {
        IsOpen = true;
        selectedTab = 1;
    }

    public override void PreDraw()
    {
        themeScope = Theme.Push();

        // Back to a view: the height it had when it was left.
        if (restoreHeight is { } restore)
        {
            Size = new Vector2(windowSize.X, restore) / ImGuiHelpers.GlobalScale;
            SizeCondition = ImGuiCond.Always;
            restoreHeight = null;
            fitHeight = null; // the limit of the view left does not apply
        }
        else if (SizeCondition == ImGuiCond.Always)
        {
            Size = null;
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        // No taller than the content: no empty space at the bottom.
        var maxHeight = fitHeight is { } fit ? Math.Max(MinHeight, fit / ImGuiHelpers.GlobalScale) : float.MaxValue;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, MinHeight), MaximumSize = new Vector2(float.MaxValue, maxHeight) };
    }

    private const float MinHeight = 380;

    // Height of each view (Live, History, Progression) when it was last shown, to give it back when coming back.
    private readonly Dictionary<int, float> viewHeights = new();
    private int lastView;
    private Vector2 windowSize;
    private float? restoreHeight;

    private int CurrentView => selectedTab;

    public override void PostDraw()
    {
        themeScope?.Dispose();
        themeScope = null;
    }

    public override void Draw()
    {
        Theme.TabStrip("main", ref selectedTab, (FontAwesomeIcon.Bolt, "Live"), (FontAwesomeIcon.History, "History"),
                       (FontAwesomeIcon.ChartLine, "Progression"));
        DrawSourceAndSettings();
        Theme.Rule(2);

        fitHeight = null;
        if (selectedTab == 0)
        {
            liveElastic = false;
            DrawLive();
            if (!liveElastic)
                fitHeight = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + ImGui.GetStyle().WindowPadding.Y;
        }
        else
        {
            // History: its sections a little further apart.
            Theme.SectionSpacing = Theme.Px(15);
            try
            {
                DrawHistory();
            }
            finally
            {
                Theme.SectionSpacing = 0;
            }
        }

        // Another view now: remember this one's height, and give the new one its own.
        windowSize = ImGui.GetWindowSize();
        var view = CurrentView;
        if (view != lastView)
        {
            viewHeights[lastView] = windowSize.Y;
            if (viewHeights.TryGetValue(view, out var height))
                restoreHeight = height;
            lastView = view;
        }
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

        var enc = snapshot?.Encounter;
        if (snapshot is not null && enc is not null)
        {
            ImGui.TextColored(Theme.GoldBright, string.IsNullOrEmpty(enc.Title) ? "Encounter" : enc.Title);
            ImGui.SameLine();
            Theme.Badge(NumberFormat.Duration(enc.Duration), Theme.Crystal);
            if (!snapshot.IsActive)
            {
                ImGui.SameLine();
                Theme.Badge("ended", Theme.TextMuted);
            }

            ImGui.SameLine();
        }

        DrawLiveButtons();

        if (snapshot is null || enc is null)
        {
            Theme.Hint("No combat data yet. Start a fight.");
            return;
        }

        var partyDps = snapshot.PartyDps(config.IncludeLimitBreakInParty);
        var threshold = config.FindThreshold(plugin.CurrentTerritory, enc.Title, plugin.Boss.Current?.BaseId ?? 0);
        DrawLiveCards(snapshot, partyDps, threshold);

        if (plugin.Ultimates.Current is { } phase)
            DrawPhase(phase);

        DrawPersonal(snapshot);

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

    /// <summary>Compact view and Opener practice, at the right of the line.</summary>
    private void DrawLiveButtons()
    {
        static float ChipWidth(string label) => ImGui.CalcTextSize(label).X + Theme.Px(20);
        var width = ChipWidth("Compact view") + ImGui.GetStyle().ItemSpacing.X + ChipWidth("Opener practice");
        if (ImGui.GetContentRegionAvail().X < width)
            ImGui.NewLine(); // no room right of the title: on their own line
        var x = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width;
        if (x > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(x);

        if (Theme.Chip("Compact view", plugin.CompactOpen))
            plugin.SetCompact(!plugin.CompactOpen);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only the party's DPS meter, in a small pop-up window.");

        ImGui.SameLine();
        if (Theme.Chip("Opener practice", plugin.OpenerOpen))
            plugin.ToggleOpener();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Practice your job's opener: each action used in the right order is checked off.");
    }

    /// <summary>The key figures of the fight in progress, one card each.</summary>
    private void DrawLiveCards(CombatSnapshot snapshot, double partyDps, PartyDpsThreshold? threshold)
    {
        var cards = new List<Card>();
        var you = snapshot.You;

        if (you is not null)
        {
            var rank = snapshot.YourRank();
            var party = snapshot.Combatants.Count(c => c.HasJob);
            cards.Add(new Card("DPS", NumberFormat.Format(you.EncDps, NumberStyle.Grouped), Theme.Gold,
                                          party > 1 && rank > 0 ? $"#{rank} of {party}" : null));
            if (you.HasRdps)
                cards.Add(new Card("rDPS", NumberFormat.Format(you.RDps, NumberStyle.Grouped), Theme.Crystal,
                                              party > 1 ? $"#{snapshot.YourRank(byRdps: true)} of {party}" : null));
        }

        var elapsed = snapshot.Encounter.Duration.TotalSeconds;
        var boss = plugin.Ultimates.Current is null ? plugin.Boss.Current : null;
        var enrage = threshold?.EnrageSeconds ?? 0;

        if (enrage > 0 && PartyGapToEnrage(snapshot, boss, partyDps, elapsed, threshold!) is { } gap)
        {
            var color = gap >= 0 ? Theme.Good : Theme.Bad;
            cards.Add(new Card("Party DPS", NumberFormat.Format(partyDps, NumberStyle.Grouped), color,
                               $"{gap:+0.0%;-0.0%} vs enrage", color));
        }
        else if (threshold is { EffectiveMinPartyDps: > 0 } check)
        {
            var color = partyDps >= check.EffectiveMinPartyDps ? Theme.Good : Theme.Bad;
            cards.Add(new Card("Party DPS", NumberFormat.Format(partyDps, NumberStyle.Grouped), color,
                               $"need {NumberFormat.Format(check.EffectiveMinPartyDps, NumberStyle.Grouped)} "
                               + $"({partyDps / check.EffectiveMinPartyDps - 1:+0.0%;-0.0%})", color));
        }
        else
        {
            cards.Add(new Card("Party DPS", NumberFormat.Format(partyDps, NumberStyle.Grouped), Theme.Gold));
        }

        if (boss is not null)
        {
            string? hint = null;
            Vector4? hintColor = null;
            if (snapshot.IsActive && BossTracker.ProjectedKill(boss, snapshot.Encounter.Duration, partyDps) is { } eta)
            {
                if (enrage > 0)
                {
                    // Time to spare (or missing) between the projected kill and the enrage.
                    var margin = enrage - eta.TotalSeconds;
                    var span = NumberFormat.Duration(TimeSpan.FromSeconds(Math.Abs(margin)));
                    hint = margin >= 0 ? $"kill {span} before enrage" : $"kill {span} after enrage";
                    hintColor = margin >= 0 ? Theme.Good : Theme.Bad;
                }
                else
                {
                    hint = $"kill in {NumberFormat.Duration(eta)}";
                }
            }

            cards.Add(new Card(boss.Name, $"{boss.HpFraction:0.0%}", Theme.Bad, hint, hintColor));
        }

        if (plugin.Pulls.PaceVsBest() is { } pace)
        {
            var color = pace >= 0 ? Theme.Good : Theme.Bad;
            cards.Add(new Card("Pace vs best", $"{pace:+0.0%;-0.0%}", color, "damage so far", ValueColor: color));
        }

        DrawCards(cards, uniform: true);
    }

    /// <summary>
    /// How much more (or less) DPS the party does than the enrage asks: during the fight, against what is left of the
    /// boss in the time left; otherwise against the boss HP over the enrage time.
    /// </summary>
    private static double? PartyGapToEnrage(CombatSnapshot snapshot, BossInfo? boss, double partyDps, double elapsed, PartyDpsThreshold check)
    {
        if (partyDps <= 0)
            return null;
        if (snapshot.IsActive && boss is { CurrentHp: > 0 })
        {
            var left = check.EnrageSeconds - elapsed;
            return left <= 0 ? -1 : partyDps * left / boss.CurrentHp - 1;
        }

        return check.EffectiveMinPartyDps > 0 ? partyDps / check.EffectiveMinPartyDps - 1 : null;
    }

    /// <summary>Your own play in the fight in progress: GCD, gauge, positionals, combos, DoTs, overheal, tank swaps.</summary>
    private void DrawPersonal(CombatSnapshot snapshot)
    {
        var up = plugin.Uptime.Snapshot() is { AvailableSeconds: >= 5 } u ? u : null;
        var job = snapshot.You?.Job ?? string.Empty;
        var role = snapshot.You is null ? (JobRole?)null : JobRoles.Of(job);
        var gauge = up is not null && snapshot.You is not null && GaugeTracker.Tracks(job);
        var positionals = plugin.Positionals.Current;
        var combos = plugin.Combos.Current;
        var showCombos = combos.Count > 0 || plugin.Combos.TracksCurrentJob;
        var dots = plugin.Dots.Current is { Count: > 0 } d && d[0].AvailableSeconds >= 5 ? d : null;
        var heals = role == JobRole.Healer ? plugin.Sources.Native.Heals.Current : null;
        var swaps = role == JobRole.Tank ? plugin.Sources.Native.TankSwaps.Current : null;

        if (up is null && !gauge && positionals.Count == 0 && !showCombos && dots is null
            && heals is not { Count: > 0 } && swaps is not { Count: > 0 })
            return;

        Theme.SectionHeader("Personal", FontAwesomeIcon.User);
        if (up is not null)
            DrawGcdAnalysis(up.Gcd, showGaps: false, uptime: up.Uptime, lostSeconds: up.LostSeconds);
        if (gauge)
            DrawGaugeWaste(plugin.Gauges.Current);
        DrawPositionals(positionals, names: false);
        DrawComboMistakes(combos, names: false, showNone: showCombos);
        if (dots is not null)
            DrawDotUptime(dots);
        if (heals is not null)
            DrawOverheal(heals, foldable: true);
        if (swaps is not null)
            DrawTankSwaps(swaps, showEmpty: true);
    }

    /// <summary>A figure card: see <see cref="Theme.StatCard"/>.</summary>
    private sealed record Card(string Caption, string Value, Vector4 Accent, string? Hint = null, Vector4? HintColor = null, Vector4? ValueColor = null);

    // Width of a figure card; narrower only when the window cannot fit them all.
    private const float CardWidth = 190;

    /// <summary>
    /// Lays the cards out on one line, at a fixed width. Uniform: all as tall (room for a hint) and as wide (the
    /// widest one's need) as each other.
    /// </summary>
    private static void DrawCards(List<Card> cards, bool uniform = false)
    {
        var width = Math.Min(Theme.Px(CardWidth), Theme.CardWidth(cards.Count));
        if (uniform)
            width = cards.Select(c => Theme.StatCardWidth(c.Caption, c.Value, c.Hint)).Append(width).Max();

        // Too wide for one line (a narrow window): as many per line as fit, the rest below.
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var perLine = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (width + spacing)));
        if (perLine < cards.Count)
            width = Math.Max(width, Theme.CardWidth(perLine)); // the lines filled edge to edge
        for (var i = 0; i < cards.Count; i++)
        {
            if (i % perLine != 0)
                ImGui.SameLine();
            var c = cards[i];
            Theme.StatCard(c.Caption, c.Value, c.Accent, width, c.Hint, c.HintColor, c.ValueColor, fullHeight: uniform);
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

        var cards = new List<Card>
        {
            new Card(phase.Phase!.Name, $"{phase.HpFraction:0.0%}", Theme.Bad,
                                $"phase time {NumberFormat.Duration(phase.Elapsed)}"),
        };

        if (phase.RequiredDps is { } required)
        {
            var color = phase.BossDps >= required ? Theme.Good : Theme.Bad;
            cards.Add(new Card("DPS on boss", NumberFormat.Format(phase.BossDps, NumberStyle.Grouped), color,
                                          $"need {NumberFormat.Format(required, NumberStyle.Grouped)} "
                                          + $"({phase.BossDps / required - 1:+0.0%;-0.0%})", color));
        }
        else
        {
            cards.Add(new Card("DPS on boss", NumberFormat.Format(phase.BossDps, NumberStyle.Grouped), Theme.Gold,
                                          "no enrage set"));
        }

        if (phase.ProjectedKill is { } eta)
        {
            Vector4? color = phase.EnrageSeconds is { } enrage ? eta.TotalSeconds <= enrage ? Theme.Good : Theme.Bad : null;
            cards.Add(new Card("Projected kill", NumberFormat.Duration(eta), color ?? Theme.Crystal,
                                          phase.EnrageSeconds is { } e ? $"enrage {NumberFormat.Duration(TimeSpan.FromSeconds(e))}" : null,
                                          color));
        }

        DrawCards(cards, uniform: true);
    }

    private void DrawLiveGraph(PartyDpsThreshold? threshold)
    {
        var window = plugin.Configuration.GraphWindowSeconds;
        graph.DrawOptions(window);

        if (graph.ShowUptime)
        {
            // Uptime of the fight in progress (or the last one).
            if (plugin.Uptime.Snapshot() is { } up)
            {
                liveElastic = true;
                DpsGraph.DrawUptime("##liveuptime", [("You", up.AvailableBySecond, up.ActiveBySecond)], ImGui.GetContentRegionAvail().Y, up.Gcd.Gaps,
                                    plugin.Sources.Latest is { } latest ? plugin.Sources.Native.PartyDeaths.Since(latest.StartUtc) : null);
            }
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

        liveElastic = true;
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
            fitHeight = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + ImGui.GetStyle().WindowPadding.Y;
            return;
        }

        var categories = ShowProgression ? plugin.Configuration.ProgressionCategories & ContentCategory.Progression : plugin.Configuration.HistoryCategories;
        var inCategories = pulls.Where(p => (plugin.ContentCategories.Of(p.TerritoryId) & categories) != 0).ToList();

        // The filters, with a little room around and between them.
        ImGui.Dummy(new Vector2(0, Theme.Px(5)));
        DrawCategoryFilter(pulls);
        ImGui.Dummy(new Vector2(0, Theme.Px(2)));
        DrawHistoryFilter(inCategories);
        ImGui.Dummy(new Vector2(0, Theme.Px(3)));

        if (ShowProgression)
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
        float ListHeight(int rows) => ImGui.GetTextLineHeight() + style.CellPadding.Y * 2
                                      + rows * (ImGui.GetFrameHeight() + style.CellPadding.Y * 2)
                                      + style.ScrollbarSize + style.WindowPadding.Y * 2 + Theme.Px(2);
        var minHeight = ListHeight(3);
        var fullHeight = Math.Max(minHeight, ListHeight(visible.Count)); // every pull shown: no taller than that
        var avail = ImGui.GetContentRegionAvail().Y;
        var listHeight = selected is null
            ? Math.Max(avail - ImGui.GetTextLineHeightWithSpacing() - style.ItemSpacing.Y, minHeight)
            : Math.Max(minHeight, avail - detailsHeight - style.ItemSpacing.Y);
        // With a pull selected, the list stays at its smallest until all the details below fit; past that, all the
        // extra room goes to the list (the details keep their size, the graph its smallest height).
        pullListExtra = Math.Min(pullListExtra, Math.Max(0, fullHeight - listHeight));
        var shown = Math.Min(listHeight + pullListExtra, fullHeight);

        using (var child = ImRaii.Child("##pulllist", new Vector2(-1, shown), true))
        {
            if (child)
                DrawPullTable(visible);
        }

        DrawPullListGrip();

        // Where what follows the list would start with every pull shown: the window needs no more than that.
        var afterFullList = ImGui.GetCursorPosY() + fullHeight - shown;
        if (selected is null)
        {
            Theme.Hint("Select a pull to see its details. Tick \"vs\" on another one to compare.");
            fitHeight = afterFullList + ImGui.GetTextLineHeight() + style.WindowPadding.Y;
            return;
        }

        var compare = pulls.FirstOrDefault(p => p.Id == compareId && p.Id != selectedId);
        DrawPullDetails(selected, compare);
        fitHeight = afterFullList + detailsHeight + style.WindowPadding.Y;
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

        var cards = new List<Card>
        {
            new Card("Pulls", prog.Count.ToString(), Theme.Gold, kills > 0 ? (kills == 1 ? "1 kill" : $"{kills} kills") : "no kill yet",
                                kills > 0 ? Theme.Good : null),
        };
        if (best is not null)
        {
            var number = prog.IndexOf(best) + 1;
            cards.Add(new Card("Best pull", Progression.Label(best), Theme.Good,
                                          $"pull {number}, {best.StartUtc.ToLocalTime():dd/MM HH:mm}"));
        }

        var recent = prog.TakeLast(10).Where(p => Progression.Score(p) is not null).ToList();
        if (recent.Count > 0 && recent.MaxBy(p => Progression.Score(p)!.Value) is { } recentBest)
            cards.Add(new Card("Best of last 10", Progression.Label(recentBest), Theme.Crystal));

        var instanceHours = Progression.InstanceHours(prog);
        var played = TimeSpan.FromSeconds(prog.Sum(p => p.DurationSeconds));
        cards.Add(new Card("Time in fight", NumberFormat.Duration(played), Theme.Gold,
                                      $"{NumberFormat.Duration(TimeSpan.FromHours(instanceHours[^1]))} in the instance since {prog[0].StartUtc.ToLocalTime():dd/MM}"));
        // A kill is as far as it goes: the first one, else the last enemy cast of the furthest wipe.
        if (prog.FirstOrDefault(p => p.Outcome == PullOutcome.Kill) is { } kill)
        {
            var number = prog.IndexOf(kill) + 1;
            cards.Add(new Card("Furthest mechanic", "Kill", Theme.Good,
                               $"pull {number}, {kill.StartUtc.ToLocalTime():dd/MM HH:mm}", ValueColor: Theme.Good));
        }
        else if (prog.Where(p => p.LastBossCast.Length > 0)
                     .MaxBy(p => (Progression.Phase(p), p.DurationSeconds)) is { } furthest)
        {
            var number = prog.IndexOf(furthest) + 1;
            cards.Add(new Card("Furthest mechanic", furthest.LastBossCast, Theme.Crystal,
                                          $"pull {number}, at {NumberFormat.Duration(TimeSpan.FromSeconds(furthest.DurationSeconds))}"));
        }

        DrawCards(cards, uniform: true);

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
            "##progression", xs, progressionByTime ? "Time in the instance" : "Pull", scores,
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
            selectedTab = 1; // open it in History
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

    /// <summary>
    /// One chip per kind of content (several can be on), with the number of pulls in each. The Progression tab has its
    /// own choice, among the content with a real progression only.
    /// </summary>
    private void DrawCategoryFilter(IReadOnlyList<PullRecord> pulls)
    {
        var config = plugin.Configuration;
        var offered = ShowProgression ? ContentCategory.Progression : ContentCategory.All;
        var selected = ShowProgression ? config.ProgressionCategories : config.HistoryCategories;
        void Set(ContentCategory value)
        {
            if (ShowProgression)
                config.ProgressionCategories = value;
            else
                config.HistoryCategories = value;
            config.Save();
        }

        var counts = pulls.GroupBy(p => plugin.ContentCategories.Of(p.TerritoryId)).ToDictionary(g => g.Key, g => g.Count());

        var first = true;
        foreach (var (category, label) in ContentCategories.Labels.Where(l => (offered & l.Category) != 0))
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
            var on = (selected & category) != 0;
            if (Theme.Chip(text, on))
                Set(on ? selected & ~category : selected | category);
        }

        ImGui.SameLine(0, Theme.Px(4));
        var all = (selected & offered) == offered;
        if (Theme.Chip(all ? "None" : "All", false))
            Set(all ? ContentCategory.None : offered);
    }

    private void DrawHistoryFilter(IReadOnlyList<PullRecord> pulls)
    {
        // In Progression, the fights go by their instance (with the fight's name when the instance has two, e.g. a door boss).
        var zoneOf = pulls.Where(p => Progression.Encounter(p).Length > 0)
                          .GroupBy(Progression.Encounter, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => g.Select(p => p.Zone).FirstOrDefault(z => z.Length > 0) ?? g.Key,
                                        StringComparer.OrdinalIgnoreCase);
        string Label(string title)
        {
            if (!ShowProgression || !zoneOf.TryGetValue(title, out var zone))
                return title;
            zone = char.ToUpperInvariant(zone[0]) + zone[1..];
            return zoneOf.Values.Count(z => z.Equals(zoneOf[title], StringComparison.OrdinalIgnoreCase)) > 1 ? $"{zone} ({title})" : zone;
        }

        var titles = zoneOf.Keys.OrderBy(Label).ToList();

        ImGui.SetNextItemWidth(Theme.Px(260));
        using (var combo = ImRaii.Combo(ShowProgression ? "Instance" : "Encounter", historyFilter.Length == 0 ? "All" : Label(historyFilter),
                                        ImGuiComboFlags.HeightLarge))
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
                    if (encounterSearch.Length > 0 && !Label(title).Contains(encounterSearch, StringComparison.OrdinalIgnoreCase)
                        && !title.Contains(encounterSearch, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ImGui.Selectable(Label(title), title.Equals(historyFilter, StringComparison.OrdinalIgnoreCase)))
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

    /// <summary>A grip under the pull list: drag it down to make the list taller, double-click to bring it back.</summary>
    private void DrawPullListGrip()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - spacing); // right under the list
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = Theme.Px(8);
        ImGui.InvisibleButton("##pulllistgrip", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (hovered || active)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
        if (active)
            pullListExtra = Math.Max(0, pullListExtra + ImGui.GetIO().MouseDelta.Y);
        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            pullListExtra = 0;
        if (hovered)
            ImGui.SetTooltip("Drag to make the list taller, double-click to reset it.");

        var mid = new Vector2(pos.X + width / 2, pos.Y + height / 2);
        var color = Theme.U32(active ? Theme.Gold : hovered ? Theme.TextMuted : Theme.Line);
        for (var i = -1; i <= 1; i += 2)
        {
            var y = mid.Y + i * Theme.Px(1.5f);
            ImGui.GetWindowDrawList().AddLine(new Vector2(mid.X - Theme.Px(18), y), new Vector2(mid.X + Theme.Px(18), y), color, Theme.Px(1));
        }
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

    private void DrawPullDetails(PullRecord pull, PullRecord? compare)
    {
        var config = plugin.Configuration;
        var top = ImGui.GetCursorPosY();

        ImGui.TextColored(Theme.GoldBright, pull.Title.Length > 0 ? pull.Title : pull.Zone);
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextMuted, $"{pull.StartUtc.ToLocalTime():g}");
        ImGui.SameLine();
        Theme.Badge(pull.Source, Theme.TextMuted);

        DrawPullCards(pull, compare);
        DrawPullPersonal(pull, compare);

        Theme.SectionHeader("Party", FontAwesomeIcon.Users);
        DrawCombatantTable(pull.Combatants);

        // Pulls recorded before deaths and failed mechanics were tracked have no GCD analysis either: nothing to say for them.
        if (pull.Gcd is not null)
        {
            DrawDeaths(pull.PartyDeaths);
            if (pull.Mitigations.Count > 0)
                DrawMitigations(pull.Mitigations);
            DrawMistakes(pull.Mistakes, timeline: true);
            if (pull.PartyDeaths.Count > 0 || pull.Mistakes.Count > 0 || pull.TankSwaps.Count > 0 || pull.Mitigations.Count > 0)
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

        var cards = new List<Card>();

        var (dpsHint, dpsColor) = Gap(pull.YourDps, compare?.YourDps);
        if (dpsHint is null && pull.PartySize > 1 && pull.Rank > 0)
            dpsHint = $"#{pull.Rank} of {pull.PartySize}";
        cards.Add(new Card("DPS", NumberFormat.Format(pull.YourDps, NumberStyle.Grouped), Theme.Gold, dpsHint, dpsColor));

        if (pull.YourRdps is { } rdps)
        {
            var (hint, color) = Gap(rdps, compare?.YourRdps);
            if (hint is null && pull.PartySize > 1 && pull.RdpsRank > 0)
                hint = $"#{pull.RdpsRank} of {pull.PartySize}";
            cards.Add(new Card("rDPS", NumberFormat.Format(rdps, NumberStyle.Grouped), Theme.Crystal, hint, color));
        }

        var (partyHint, partyColor) = Gap(pull.PartyDps, compare?.PartyDps);
        cards.Add(new Card("Party DPS", NumberFormat.Format(pull.PartyDps, NumberStyle.Grouped), Theme.Gold, partyHint, partyColor));

        var durationHint = compare is not null ? $"vs {NumberFormat.Duration(TimeSpan.FromSeconds(compare.DurationSeconds))}" : null;
        cards.Add(new Card("Duration", NumberFormat.Duration(TimeSpan.FromSeconds(pull.DurationSeconds)),
                                      pull.Outcome switch { PullOutcome.Kill => Theme.Good, PullOutcome.Wipe => Theme.Bad, _ => Theme.TextMuted },
                                      durationHint));

        DrawCards(cards, uniform: true);
    }

    /// <summary>
    /// Your own play in a pull, one foldable line each: the figures on the line, the detail inside.
    /// </summary>
    private static void DrawPullPersonal(PullRecord pull, PullRecord? compare)
    {
        var role = JobRoles.Of(pull.Job);
        var gauge = pull.Gcd is not null && GaugeTracker.Tracks(pull.Job);
        var dots = pull.DotUptimes is { Count: > 0 } d && d[0].AvailableSeconds >= 5 ? d : null;
        var heals = role == JobRole.Healer ? pull.Overheal.Where(h => JobRoles.Of(h.Job) == JobRole.Healer).ToList() : [];
        var tank = role == JobRole.Tank;
        if (pull.Uptime is null && !gauge && pull.Positionals.Count == 0 && pull.ComboMistakes.Count == 0 && dots is null
            && heals.Count == 0 && !(tank && pull.TankSwaps.Count > 0) && pull.Mitigations.Count == 0)
            return;

        Theme.SectionHeader("Personal", FontAwesomeIcon.User);
        if (pull.Uptime is { } uptime)
            FoldGcd(pull, compare, uptime);
        if (gauge)
            FoldGauge(pull.GaugeWaste);
        if (pull.Positionals.Count > 0)
            FoldPositionals(pull.Positionals);
        if (pull.ComboMistakes.Count > 0)
            FoldCombos(pull.ComboMistakes);
        if (dots is not null)
            FoldDots(dots);
        if (heals.Count > 0)
            FoldOverheal(heals);
        if (pull.Mitigations.Count > 0)
            FoldMitigations(pull.Mitigations.Where(m => m.IsYou).ToList());
        if (tank)
            FoldTankSwaps(pull.TankSwaps);
    }

    // A foldable line: its title, then the figures drawn by the caller on the same line.
    private static bool Fold(string title, string id)
    {
        var open = ImGui.TreeNode($"{title}###{id}");
        ImGui.SameLine(0, Theme.Px(8));
        return open;
    }

    // A detail table in a foldable line: fixed-width columns get a width, the others stretch.
    private static ImRaii.TableDisposable DetailTable(string id, params (string Name, float Width)[] columns)
    {
        var table = ImRaii.Table(id, columns.Length, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table)
            return table;
        foreach (var (name, width) in columns)
        {
            if (width > 0)
                ImGui.TableSetupColumn(name, ImGuiTableColumnFlags.WidthFixed, Theme.Px(width));
            else
                ImGui.TableSetupColumn(name, ImGuiTableColumnFlags.WidthStretch, -width);
        }

        ImGui.TableHeadersRow();
        return table;
    }

    private static void TimeCell(double seconds)
    {
        ImGui.TableNextColumn();
        ImGui.TextColored(Theme.TextFaint, NumberFormat.Duration(TimeSpan.FromSeconds(seconds)));
    }

    private static void IconCell(uint icon, string name, Vector4? color = null)
    {
        ImGui.TableNextColumn();
        ActionIcons.Draw(icon, ImGui.GetTextLineHeight());
        ImGui.SameLine(0, Theme.Px(4));
        ImGui.TextColored(color ?? Theme.Text, name);
    }

    private static void FoldGcd(PullRecord pull, PullRecord? compare, double uptime)
    {
        var open = Fold("GCD uptime", "pgcd");
        ImGui.TextColored(Theme.Crystal, $"{uptime:0.0%}");
        if (compare?.Uptime is { } other)
        {
            var diff = (uptime - other) * 100;
            ImGui.SameLine(0, Theme.Px(8));
            ImGui.TextColored(diff >= 0 ? Theme.Good : Theme.Bad, $"{diff:+0.0;-0.0} pts vs compared");
        }

        ImGui.SameLine(0, Theme.Px(8));
        ImGui.TextColored(Theme.TextFaint, pull.Gcd is { GcdCount: > 0 } g
                                               ? $"{g.GcdCount} GCDs, lost {pull.UptimeLostSeconds:0.0}s"
                                               : $"lost {pull.UptimeLostSeconds:0.0}s");
        if (!open)
            return;

        if (pull.PhaseUptimes.Count > 0)
        {
            Theme.Hint("Per phase: " + string.Join("   ", pull.PhaseUptimes.Select(
                                         p => $"{UltimateDefinitions.Label(pull.TerritoryId, p.Number)} {p.Uptime:0.0%}")));
        }

        if (pull.Gcd is { } analysis)
        {
            DrawGcdAnalysis(analysis, showGaps: false);
            DrawClipTimeline(analysis, pull.DurationSeconds, folded: false);
        }

        ImGui.TreePop();
    }

    private static void FoldGauge(IReadOnlyList<GaugeWaste> waste)
    {
        var open = Fold("Gauge overcap", "pgauge");
        if (waste.Count == 0)
        {
            ImGui.TextColored(Theme.Good, "none");
        }
        else
        {
            for (var i = 0; i < waste.Count; i++)
            {
                if (i > 0)
                    ImGui.SameLine(0, Theme.Px(10));
                ActionIcons.Draw(ActionIcons.OfGauge(waste[i].Resource), ImGui.GetTextLineHeight());
                ImGui.SameLine(0, Theme.Px(4));
                ImGui.TextColored(Theme.Warn, GaugeAmount(waste[i]));
            }
        }

        if (!open)
            return;

        if (waste.Count == 0)
        {
            Theme.Hint("No gauge lost to overcapping.");
        }
        else
        {
            using (var table = DetailTable("##pgaugetable", ("Gauge", -2), ("Lost", 60), ("Times", 50), ("Overcapped by", -4)))
            {
                if (table)
                {
                    foreach (var w in waste)
                    {
                        ImGui.TableNextRow();
                        IconCell(ActionIcons.OfGauge(w.Resource), w.Resource);
                        ImGui.TableNextColumn();
                        ImGui.TextColored(Theme.Warn, GaugeAmount(w));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"{w.Count}×");
                        ImGui.TableNextColumn();
                        ImGui.TextColored(Theme.TextMuted, w.Actions);
                    }
                }
            }
        }

        ImGui.TreePop();
    }

    private static string GaugeAmount(GaugeWaste w) => w.Max <= 5 ? $"{w.Wasted:0.#}" : $"{w.Wasted:0}";

    private static void FoldPositionals(IReadOnlyList<PositionalUse> uses)
    {
        var hits = uses.Count(u => u.Hit);
        var share = (double)hits / uses.Count;
        var open = Fold("Positionals", "ppos");
        ImGui.TextColored(share >= 1 ? Theme.Good : Theme.Bad, $"{hits}/{uses.Count} ({share:0%})");
        foreach (var action in uses.GroupBy(u => u.ActionId).Where(g => g.Any(u => !u.Hit)).OrderByDescending(g => g.Count(u => !u.Hit)))
        {
            ImGui.SameLine(0, Theme.Px(10));
            ActionIcons.Draw(ActionIcons.OfAction(action.Key), ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(Theme.Bad, $"{action.Count(u => !u.Hit)}/{action.Count()}");
        }

        if (!open)
            return;

        var missed = uses.Where(u => !u.Hit).ToList();
        if (missed.Count == 0)
        {
            Theme.Hint("Every positional landed.");
        }
        else
        {
            using var table = DetailTable("##ppostable", ("Time", 60), ("Action", -3), ("Needed", -2));
            if (table)
            {
                foreach (var u in missed)
                {
                    ImGui.TableNextRow();
                    TimeCell(u.AtSeconds);
                    IconCell(ActionIcons.OfAction(u.ActionId), u.Action);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Bad, u.Rear ? "rear" : "flank");
                }
            }
        }

        ImGui.TreePop();
    }

    private static void FoldCombos(IReadOnlyList<ComboMistake> mistakes)
    {
        var open = Fold("Combo errors", "pcombo");
        ImGui.TextColored(Theme.Bad, mistakes.Count.ToString());
        foreach (var group in mistakes.GroupBy(m => m.ActionId).OrderByDescending(g => g.Count()))
        {
            ImGui.SameLine(0, Theme.Px(10));
            ActionIcons.Draw(ActionIcons.OfAction(group.Key), ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(Theme.Bad, $"×{group.Count()}");
        }

        if (!open)
            return;

        using (var table = DetailTable("##pcombotable", ("Time", 60), ("Action", -2), ("Error", -5)))
        {
            if (table)
            {
                foreach (var m in mistakes)
                {
                    ImGui.TableNextRow();
                    TimeCell(m.AtSeconds);
                    IconCell(ActionIcons.OfAction(m.ActionId), m.Action);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextMuted, ComboMistakeDetail(m));
                }
            }
        }

        ImGui.TreePop();
    }

    private static void FoldDots(IReadOnlyList<DotUptime> dots)
    {
        var open = Fold("DoT uptime", "pdot");
        for (var i = 0; i < dots.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, Theme.Px(10));
            ActionIcons.Draw(dots[i].Icon, ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(DotColor(dots[i].Uptime), $"{dots[i].Uptime:0.0%}");
        }

        if (!open)
            return;

        foreach (var dot in dots)
        {
            ActionIcons.Draw(dot.Icon, ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(DotColor(dot.Uptime), $"{dot.Name} {dot.Uptime:0.0%}");
            ImGui.SameLine(0, Theme.Px(8));
            ImGui.TextColored(Theme.TextFaint, $"off the boss {dot.AvailableSeconds - dot.ActiveSeconds:0.0}s "
                                               + $"of {NumberFormat.Duration(TimeSpan.FromSeconds(dot.AvailableSeconds))}");
        }

        var drops = dots.SelectMany(dot => (dot.Drops ?? []).Select(x => (Dot: dot, Drop: x))).OrderBy(x => x.Drop.AtSeconds).ToList();
        if (dots.All(dot => dot.Drops is null))
        {
            Theme.Hint("The drops were not recorded for this pull.");
        }
        else if (drops.Count == 0)
        {
            Theme.Hint("Never off the boss for a second or more.");
        }
        else
        {
            using var table = DetailTable("##pdottable", ("Time", 60), ("DoT", -3), ("Off for", -2));
            if (table)
            {
                foreach (var (dot, drop) in drops)
                {
                    ImGui.TableNextRow();
                    TimeCell(drop.AtSeconds);
                    IconCell(dot.Icon, dot.Name);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(drop.Seconds >= 3 ? Theme.Bad : Theme.Warn, $"{drop.Seconds:0.0}s");
                }
            }
        }

        ImGui.TreePop();
    }

    private static Vector4 DotColor(double uptime) => uptime >= 0.95 ? Theme.Good : uptime >= 0.85 ? Theme.Warn : Theme.Bad;

    private static void FoldOverheal(IReadOnlyList<HealerOverheal> healers)
    {
        var mine = healers.FirstOrDefault(h => h.IsYou);
        var open = Fold("Overheal", "pheal");
        ImGui.TextColored(Theme.Text, mine is null ? "-" : $"{mine.Share:0%}");
        ImGui.SameLine(0, Theme.Px(8));
        ImGui.TextColored(Theme.TextFaint, "for information");
        if (!open)
            return;

        DrawOverheal(healers);
        if (mine?.Sources is { Length: > 0 } sources)
        {
            using var table = DetailTable("##phealtable", ("Your heals", -3), ("Healing", 80), ("Overheal", 80), ("%", 50));
            if (table)
            {
                foreach (var s in sources)
                {
                    ImGui.TableNextRow();
                    IconCell(s.Icon, s.IsHot ? $"{s.Name} (HoT)" : s.Name);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(NumberFormat.Format(s.Healing, NumberStyle.Compact));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(NumberFormat.Format(s.Overheal, NumberStyle.Compact));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{s.Share:0%}");
                }
            }
        }
        else if (mine is not null)
        {
            Theme.Hint("The detail per heal was not recorded for this pull.");
        }

        ImGui.TreePop();
    }

    /// <summary>When each party member used each of their mitigations and defensive buffs: tanks, healers, then DPS.</summary>
    private static void DrawMitigations(IReadOnlyList<MitigationUse> uses)
    {
        Theme.SectionHeader("Mitigation", FontAwesomeIcon.ShieldAlt, "Mitigations (on the party or on the boss) and defensive buffs used by each party member, with "
                         + "the time of each use. GCD shields (Succor, Eukrasian Prognosis...) are left out. Party members "
                         + "far from you may be missed.");

        static int RoleOrder(string job) => JobRoles.Of(job) switch { JobRole.Tank => 0, JobRole.Healer => 1, _ => 2 };
        var players = uses.GroupBy(u => u.Player)
                          .OrderBy(g => RoleOrder(g.First().Job))
                          .ThenByDescending(g => g.First().IsYou)
                          .ThenBy(g => g.Key)
                          .ToList();

        // One foldable line per player (job, name, number of uses), the detail inside.
        foreach (var player in players)
        {
            var p = player.First();
            var lineStart = ImGui.GetCursorPosX();
            var open = ImGui.TreeNode($"###mit{player.Key}");
            ImGui.SameLine(lineStart + ImGui.GetTreeNodeToLabelSpacing()); // after the arrow
            JobInfo.DrawIcon(p.Job, ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(p.IsYou ? Theme.GoldBright : Theme.Text, p.Player);
            ImGui.SameLine(0, Theme.Px(8));
            ImGui.TextColored(Theme.TextFaint, player.Count() == 1 ? "1 use" : $"{player.Count()} uses");
            if (!open)
                continue;

            using (var table = DetailTable($"##mittable{player.Key}", ("Mitigation", -2), ("Uses", 40), ("Times", -5)))
            {
                if (table)
                {
                    foreach (var action in player.GroupBy(u => u.Action).OrderBy(g => g.First().AtSeconds))
                    {
                        ImGui.TableNextRow();
                        IconCell(ActionIcons.OfAction(action.First().ActionId), action.Key);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(action.Count().ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextWrapped(string.Join("   ", action.Select(u => NumberFormat.Duration(TimeSpan.FromSeconds(u.AtSeconds)))));
                    }
                }
            }

            ImGui.TreePop();
        }
    }

    private static void FoldMitigations(IReadOnlyList<MitigationUse> mine)
    {
        var open = Fold("Mitigation", "pmit");
        ImGui.TextColored(mine.Count == 0 ? Theme.TextFaint : Theme.Text, mine.Count == 0 ? "none" : $"{mine.Count} uses");
        if (!open)
            return;

        if (mine.Count == 0)
        {
            Theme.Hint("You used no mitigation or defensive buff.");
        }
        else
        {
            using var table = DetailTable("##pmittable", ("Action", -3), ("Uses", 60));
            if (table)
            {
                foreach (var g in mine.GroupBy(m => m.Action).OrderByDescending(g => g.Count()))
                {
                    ImGui.TableNextRow();
                    IconCell(ActionIcons.OfAction(g.First().ActionId), g.Key);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(g.Count().ToString());
                }
            }
        }

        ImGui.TreePop();
    }

    private static void FoldTankSwaps(IReadOnlyList<TankSwap> swaps)
    {
        var open = Fold("Tank swaps", "pswaps");
        ImGui.TextColored(Theme.Text, swaps.Count.ToString());
        if (!open)
            return;

        if (swaps.Count == 0)
            Theme.Hint("The boss never changed tanks.");
        else
            DrawTankSwapTable(swaps);
        ImGui.TreePop();
    }

    /// <summary>One line with the uptime of each of your DoTs on the boss, by icon and name.</summary>
    private static void DrawDotUptime(IReadOnlyList<DotUptime> dots)
    {
        if (dots.Count == 0 || dots[0].AvailableSeconds < 5)
            return;

        ImGui.TextColored(Theme.TextMuted, "DoT uptime");
        foreach (var dot in dots)
        {
            ImGui.SameLine(0, Theme.Px(8));
            ActionIcons.Draw(dot.Icon, ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(dot.Uptime >= 0.95 ? Theme.Good : dot.Uptime >= 0.85 ? Theme.Warn : Theme.Bad, $"{dot.Name} {dot.Uptime:0.0%}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Off the boss for {dot.AvailableSeconds - dot.ActiveSeconds:0.0}s of {NumberFormat.Duration(TimeSpan.FromSeconds(dot.AvailableSeconds))}");
        }

        Theme.HelpMarker("Share of the time your DoT was on the boss, while the boss could be attacked (jumps, "
                         + "transitions and cutscenes left out).");
    }

    /// <summary>One line with the overheal of each healer of the party (for information: no color, it has many causes).</summary>
    /// <param name="foldable">One foldable line with your overheal, the healers' detail inside.</param>
    private static void DrawOverheal(IReadOnlyList<HealerOverheal> heals, bool foldable = false)
    {
        var healers = heals.Where(h => JobRoles.Of(h.Job) == JobRole.Healer).ToList();
        if (healers.Count == 0)
            return;

        if (foldable)
        {
            var mine = healers.FirstOrDefault(h => h.IsYou);
            var open = ImGui.TreeNode(mine is null ? "Overheal###overheal" : $"Overheal ({mine.Share:0%})###overheal");
            ImGui.SameLine();
            Theme.HelpMarker("Share of each healer's healing (pets and HoTs included) that went over full HP. For information "
                             + "only: pre-healing a raidwide or a HoT running out its time also overheals.");
            if (!open)
                return;
        }
        else
        {
            ImGui.TextColored(Theme.TextMuted, "Overheal");
        }

        foreach (var h in healers.OrderByDescending(h => h.IsYou))
        {
            if (!foldable)
                ImGui.SameLine(0, Theme.Px(8));
            JobInfo.DrawIcon(h.Job, ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(h.IsYou ? Theme.GoldBright : Theme.Text, $"{(h.IsYou ? "You" : h.Player)} {h.Share:0%}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{NumberFormat.Format(h.Overheal, NumberStyle.Compact)} of {NumberFormat.Format(h.Healing, NumberStyle.Compact)} healing went over full HP");
        }

        if (foldable)
        {
            ImGui.TreePop();
            return;
        }

        Theme.HelpMarker("Share of each healer's healing (pets and HoTs included) that went over full HP. For information "
                         + "only: pre-healing a raidwide or a HoT running out its time also overheals.");
    }

    /// <summary>Foldable list of the boss going from one tank to another, with why.</summary>
    private static void DrawTankSwaps(IReadOnlyList<TankSwap> swaps, bool showEmpty = false)
    {
        if (swaps.Count == 0 && showEmpty)
        {
            ImGui.TextColored(Theme.TextMuted, "Tank swaps");
            ImGui.SameLine(0, Theme.Px(6));
            ImGui.TextColored(Theme.TextFaint, "0");
            return;
        }

        if (swaps.Count == 0 || !ImGui.TreeNode($"Tank swaps ({swaps.Count})###tankswaps"))
            return;

        DrawTankSwapTable(swaps);
        ImGui.TreePop();
    }

    private static void DrawTankSwapTable(IReadOnlyList<TankSwap> swaps)
    {
        using (var table = ImRaii.Table("##tankswaps", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, Theme.Px(60));
                ImGui.TableSetupColumn("Swap");
                ImGui.TableSetupColumn("Why");
                ImGui.TableHeadersRow();
                foreach (var s in swaps)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextFaint, NumberFormat.Duration(TimeSpan.FromSeconds(s.AtSeconds)));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{s.From} → {s.To}");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(s.Reason == "Death" ? Theme.Bad : Theme.TextMuted, s.Reason);
                }
            }
        }

    }

    /// <summary>
    /// One line with your positionals hit, then the actions that missed some, by icon (with their name in the
    /// history); hover one for when.
    /// </summary>
    private static void DrawPositionals(IReadOnlyList<PositionalUse> uses, bool names)
    {
        if (uses.Count == 0)
            return;

        var hits = uses.Count(u => u.Hit);
        var share = (double)hits / uses.Count;
        ImGui.TextColored(Theme.TextMuted, "Positionals");
        ImGui.SameLine(0, Theme.Px(6));
        ImGui.TextColored(share >= 1 ? Theme.Good : Theme.Bad, $"{hits}/{uses.Count} ({share:0%})");

        var iconSize = ImGui.GetTextLineHeight();
        foreach (var action in uses.GroupBy(u => u.ActionId).Where(g => g.Any(u => !u.Hit)).OrderByDescending(g => g.Count(u => !u.Hit)))
        {
            var missed = action.Where(u => !u.Hit).ToList();
            ImGui.SameLine(0, Theme.Px(10));
            var hovered = ActionIcons.Draw(ActionIcons.OfAction(action.Key), iconSize);
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(Theme.Bad, names ? $"{missed[0].Action} {missed.Count}/{action.Count()} missed" : $"{missed.Count}/{action.Count()}");
            if (hovered || ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"{missed[0].Action} ({(missed[0].Rear ? "rear" : "flank")}): {missed.Count} of {action.Count()} missed, at "
                                 + string.Join(", ", missed.Select(u => NumberFormat.Duration(TimeSpan.FromSeconds(u.AtSeconds)))));
            }
        }

        Theme.HelpMarker(
            "Your positional actions whose positional bonus landed, read from the bonus the game gave each hit "
            + "(True North and bosses without positionals count as hits).\n"
            + "Hover an action for when you missed it.");
    }

    /// <summary>One line with your combo errors, by icon of the action used (with its name in the history); hover for details.</summary>
    private static void DrawComboMistakes(IReadOnlyList<ComboMistake> mistakes, bool names, bool showNone)
    {
        if (mistakes.Count == 0 && !showNone)
            return;

        ImGui.TextColored(Theme.TextMuted, "Combo errors");
        ImGui.SameLine(0, Theme.Px(6));
        if (mistakes.Count == 0)
        {
            ImGui.TextColored(Theme.Good, "none");
        }
        else
        {
            ImGui.TextColored(Theme.Bad, mistakes.Count.ToString());
            var iconSize = ImGui.GetTextLineHeight();
            foreach (var group in mistakes.GroupBy(m => m.ActionId).OrderByDescending(g => g.Count()))
            {
                var list = group.ToList();
                ImGui.SameLine(0, Theme.Px(10));
                var hovered = ActionIcons.Draw(ActionIcons.OfAction(group.Key), iconSize);
                ImGui.SameLine(0, Theme.Px(4));
                ImGui.TextColored(Theme.Bad, names ? $"{list[0].Action} ×{list.Count}" : $"×{list.Count}");
                if (hovered || ImGui.IsItemHovered())
                    ImGui.SetTooltip(string.Join("\n", list.Select(ComboMistakeText)));
            }
        }

        Theme.HelpMarker("Combo steps used without the step before them (they lose their combo bonus), combos restarted "
                         + "before they were finished, and combos whose timer ran out before the next step. Actions the game lets you use without breaking a combo are ignored.");
    }

    private static string ComboMistakeText(ComboMistake m) =>
        $"{NumberFormat.Duration(TimeSpan.FromSeconds(m.AtSeconds))}  {ComboMistakeDetail(m)}";

    private static string ComboMistakeDetail(ComboMistake m) => m.Kind switch
    {
        ComboMistakeKind.NoCombo => $"{m.Action} without its combo (needs {m.Expected} before)",
        ComboMistakeKind.TimedOut => $"combo lost after {m.Action}: its timer ran out before the next step",
        _ => $"{m.Action} restarted the combo: {m.Expected} was waiting for its next step",
    };

    /// <summary>A foldable strip of the pull with each clip at its time, shown by the icon of the oGCD that caused it.</summary>
    private static void DrawClipTimeline(GcdAnalysis gcd, double pullSeconds, bool folded = true)
    {
        var clips = gcd.Gaps.Where(g => g.IsClip).ToList();
        if (clips.Count == 0 || (folded && !ImGui.TreeNode($"Clipping timeline ({clips.Count})###clipping")))
            return;

        var marks = clips.Select(g =>
        {
            var locked = g.LockedSeconds > 0 ? g.LockedSeconds : g.Seconds;
            var cause = g.ActionId != 0 ? $" by {ActionIcons.NameOf(g.ActionId)}" : string.Empty;
            return (g.AtSeconds, ActionIcons.OfAction(g.ActionId),
                    $"{NumberFormat.Duration(TimeSpan.FromSeconds(g.AtSeconds))}  GCD clipped {locked:0.00}s{cause}");
        }).ToList();
        DrawIconStrip("clipping", marks, [], pullSeconds);
        Theme.Hint("Each icon is the oGCD that landed when your GCD was held by an animation lock.");
        if (folded)
            ImGui.TreePop();
    }

    /// <summary>
    /// The pull as a strip: optional ticks at the top (green good, red bad), then icons at their time, in lanes so
    /// none overlap, each red-framed; hovering one shows its tooltip.
    /// </summary>
    private static void DrawIconStrip(
        string id, IReadOnlyList<(double At, uint Icon, string Tooltip)> marks, IReadOnlyList<(double At, bool Good)> ticks,
        double pullSeconds)
    {
        var duration = Math.Max(1, Math.Max(pullSeconds, marks.Select(m => m.At).Concat(ticks.Select(t => t.At)).DefaultIfEmpty(0).Max()));
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var size = Theme.Px(24);
        var pad = size / 2 + Theme.Px(2);
        float X(double seconds) => origin.X + pad + (float)(seconds / duration) * (width - pad * 2);

        // Icons that would overlap go one lane lower.
        var lanes = new List<float>();
        var placed = new List<(int Index, float Left, int Lane)>();
        foreach (var (mark, index) in marks.Select((m, i) => (m, i)).OrderBy(p => p.m.At))
        {
            var left = X(mark.At) - size / 2;
            var lane = lanes.FindIndex(end => end <= left);
            if (lane < 0)
            {
                lanes.Add(0);
                lane = lanes.Count - 1;
            }

            lanes[lane] = left + size + Theme.Px(2);
            placed.Add((index, left, lane));
        }

        var tickHeight = ticks.Count > 0 ? Theme.Px(6) : 0;
        var tickGap = ticks.Count > 0 ? Theme.Px(4) : 0;
        var height = Theme.Px(3) + tickHeight + tickGap + Math.Max(1, lanes.Count) * (size + Theme.Px(3)) + Theme.Px(2);
        ImGui.InvisibleButton($"##{id}", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();

        dl.AddRectFilled(origin, origin + new Vector2(width, height), Theme.U32(Theme.Panel, 0.8f), Theme.Px(4));
        for (var minute = 60; minute < duration; minute += 60)
            dl.AddLine(new Vector2(X(minute), origin.Y + Theme.Px(2)), new Vector2(X(minute), origin.Y + height - Theme.Px(2)),
                       Theme.U32(Theme.Line, 0.6f), 1f);

        var tickTop = origin.Y + Theme.Px(3);
        foreach (var (at, good) in ticks)
        {
            dl.AddLine(new Vector2(X(at), tickTop), new Vector2(X(at), tickTop + tickHeight),
                       Theme.U32(good ? Theme.Good : Theme.Bad, good ? 0.5f : 1f), Theme.Px(1.5f));
        }

        var iconsTop = tickTop + tickHeight + tickGap;
        string? tooltip = null;
        var mouse = ImGui.GetMousePos();
        foreach (var (index, left, lane) in placed)
        {
            var pos = new Vector2(left, iconsTop + lane * (size + Theme.Px(3)));
            var icon = marks[index].Icon;
            if (icon != 0)
                ActionIcons.DrawAt(dl, icon, pos, size);
            else
                dl.AddRectFilled(pos, pos + new Vector2(size, size), Theme.U32(Theme.SurfaceActive), size * 0.15f);
            dl.AddRect(pos, pos + new Vector2(size, size), Theme.U32(Theme.Bad), size * 0.15f, ImDrawFlags.None, Theme.Px(1.5f));
            if (hovered && mouse.X >= pos.X && mouse.X <= pos.X + size && mouse.Y >= pos.Y && mouse.Y <= pos.Y + size)
                tooltip = marks[index].Tooltip;
        }

        ImGui.TextColored(Theme.TextFaint, "0:00");
        var end = NumberFormat.Duration(TimeSpan.FromSeconds(pullSeconds));
        ImGui.SameLine(width - ImGui.CalcTextSize(end).X);
        ImGui.TextColored(Theme.TextFaint, end);

        if (tooltip is not null)
            ImGui.SetTooltip(tooltip);
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
            ImGui.SameLine(0, Theme.Px(8));
            var iconHovered = ActionIcons.Draw(ActionIcons.OfGauge(w.Resource), ImGui.GetTextLineHeight());
            if (iconHovered && w.Actions.Length > 0)
                ImGui.SetTooltip($"Overcapped by: {w.Actions}");
            ImGui.SameLine(0, Theme.Px(4));
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
    /// The pull as lanes, chosen with the boxes above it: deaths (red), tank swaps (blue), failed mechanics (orange) and
    /// mitigation (one lane per player, by the icon of each action). Minute ticks; hover a mark for its detail.
    /// </summary>
    private void DrawTimeline(PullRecord pull)
    {
        var config = plugin.Configuration;
        Theme.SectionHeader("Timeline", FontAwesomeIcon.Stream);

        var changed = false;
        bool Toggle(string label, bool value, Vector4 color, bool first = false)
        {
            if (!first)
                ImGui.SameLine(0, Theme.Px(14));
            using (ImRaii.PushColor(ImGuiCol.Text, color))
            {
                if (ImGui.Checkbox(label, ref value))
                    changed = true;
            }

            return value;
        }

        config.TimelineDeaths = Toggle($"Deaths ({pull.PartyDeaths.Count})", config.TimelineDeaths, Theme.Bad, first: true);
        config.TimelineTankSwaps = Toggle($"Tank swaps ({pull.TankSwaps.Count})", config.TimelineTankSwaps, Theme.Tank);
        config.TimelineMistakes = Toggle($"Failed mechanics ({pull.Mistakes.Count})", config.TimelineMistakes, Theme.Warn);
        config.TimelineMitigation = Toggle($"Mitigation ({pull.Mitigations.Count})", config.TimelineMitigation, Theme.Crystal);
        if (changed)
            config.Save();

        // The lanes, top to bottom.
        var dotLane = Theme.Px(18);
        var iconSize = Theme.Px(20);
        var iconLane = iconSize + Theme.Px(2);
        var lanes = new List<TimelineLane>();
        if (config.TimelineDeaths && pull.PartyDeaths.Count > 0)
        {
            lanes.Add(new TimelineLane(FontAwesomeIcon.Skull, null, dotLane, Theme.Bad, TimelineMark.Dot,
                                       pull.PartyDeaths.Select(d => (d.AtSeconds, 0u, DeathText(d))).ToList()));
        }

        if (config.TimelineTankSwaps && pull.TankSwaps.Count > 0)
        {
            lanes.Add(new TimelineLane(FontAwesomeIcon.ExchangeAlt, null, dotLane, Theme.Tank, TimelineMark.Diamond,
                                       pull.TankSwaps.Select(s => (s.AtSeconds, 0u, $"Tank swap {s.From} → {s.To} ({s.Reason})")).ToList()));
        }

        if (config.TimelineMistakes && pull.Mistakes.Count > 0)
        {
            lanes.Add(new TimelineLane(FontAwesomeIcon.ExclamationTriangle, null, dotLane, Theme.Warn, TimelineMark.Dot,
                                       pull.Mistakes.Select(m => (m.AtSeconds, 0u, $"{m.Player}: {MistakeText(m)}")).ToList()));
        }

        if (config.TimelineMitigation)
        {
            static int RoleOrder(string job) => JobRoles.Of(job) switch { JobRole.Tank => 0, JobRole.Healer => 1, _ => 2 };
            foreach (var player in pull.Mitigations.GroupBy(u => u.Player)
                                       .OrderBy(g => RoleOrder(g.First().Job)).ThenByDescending(g => g.First().IsYou).ThenBy(g => g.Key))
            {
                var p = player.First();
                lanes.Add(new TimelineLane(null, p.Job, iconLane, Theme.Crystal, TimelineMark.Icon,
                                           player.Select(u => (u.AtSeconds, ActionIcons.OfAction(u.ActionId), $"{u.Player}: {u.Action}")).ToList()));
            }
        }

        if (lanes.Count == 0)
        {
            Theme.Hint("Nothing to show: tick what the timeline should show.");
            return;
        }

        var duration = Math.Max(1, Math.Max(pull.DurationSeconds, lanes.SelectMany(l => l.Marks).Select(m => m.At).DefaultIfEmpty(0).Max()));
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var gutter = Theme.Px(26);
        var pad = iconSize / 2 + Theme.Px(2);
        float X(double seconds) => origin.X + gutter + pad + (float)(seconds / duration) * (width - gutter - pad * 2);

        // Icons that would overlap in a player's lane go one row lower: the lane grows.
        var rows = lanes.Select(l => l.Mark == TimelineMark.Icon ? PackIcons(l.Marks, X, iconSize) : null).ToList();
        var heights = lanes.Select((l, i) => rows[i] is { } r ? Math.Max(1, r.Max(x => x.Row) + 1) * l.Height : l.Height).ToList();
        var height = heights.Sum() + Theme.Px(4);

        ImGui.InvisibleButton("##timeline", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetMousePos();

        dl.AddRectFilled(origin, origin + new Vector2(width, height), Theme.U32(Theme.Panel, 0.8f), Theme.Px(4));
        for (var minute = 60; minute < duration; minute += 60)
            dl.AddLine(new Vector2(X(minute), origin.Y + Theme.Px(2)), new Vector2(X(minute), origin.Y + height - Theme.Px(2)), Theme.U32(Theme.Line, 0.6f), 1f);

        var tooltip = new List<string>();
        var y = origin.Y + Theme.Px(2);
        for (var i = 0; i < lanes.Count; i++)
        {
            var lane = lanes[i];
            var top = y;
            var bottom = y + heights[i];
            if (i > 0)
                dl.AddLine(new Vector2(origin.X + Theme.Px(4), top), new Vector2(origin.X + width - Theme.Px(4), top), Theme.U32(Theme.Line, 0.35f), 1f);

            // What the lane is: a glyph, or the player's job.
            var labelSize = Math.Min(lane.Height - Theme.Px(4), Theme.Px(16));
            var labelPos = new Vector2(origin.X + Theme.Px(5), top + (lane.Height - labelSize) / 2);
            if (lane.Job is { } job)
            {
                ImGui.SetCursorScreenPos(labelPos);
                JobInfo.DrawIcon(job, labelSize);
            }
            else if (lane.Glyph is { } glyph)
            {
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    dl.AddText(labelPos, Theme.U32(lane.Color, 0.8f), glyph.ToIconString());
            }

            var mid = top + lane.Height / 2;
            var laneHovered = hovered && mouse.Y >= top && mouse.Y < bottom;
            if (lane.Mark != TimelineMark.Icon)
                dl.AddLine(new Vector2(X(0), mid), new Vector2(X(duration), mid), Theme.U32(Theme.Line, 0.5f), 1f);

            var radius = Theme.Px(4);
            for (var k = 0; k < lane.Marks.Count; k++)
            {
                var (at, icon, text) = lane.Marks[k];
                var x = X(at);
                switch (lane.Mark)
                {
                    case TimelineMark.Dot:
                        dl.AddCircleFilled(new Vector2(x, mid), radius, Theme.U32(lane.Color));
                        if (laneHovered && Math.Abs(mouse.X - x) <= Theme.Px(6))
                            tooltip.Add($"{NumberFormat.Duration(TimeSpan.FromSeconds(at))}  {text}");
                        break;

                    case TimelineMark.Diamond:
                        dl.AddQuadFilled(new Vector2(x, mid - radius), new Vector2(x + radius, mid), new Vector2(x, mid + radius),
                                         new Vector2(x - radius, mid), Theme.U32(lane.Color));
                        if (laneHovered && Math.Abs(mouse.X - x) <= Theme.Px(6))
                            tooltip.Add($"{NumberFormat.Duration(TimeSpan.FromSeconds(at))}  {text}");
                        break;

                    default:
                        var pos = new Vector2(x - iconSize / 2, top + rows[i]![k].Row * lane.Height + Theme.Px(1));
                        if (icon != 0)
                            ActionIcons.DrawAt(dl, icon, pos, iconSize);
                        else
                            dl.AddRectFilled(pos, pos + new Vector2(iconSize, iconSize), Theme.U32(Theme.SurfaceActive), iconSize * 0.15f);
                        if (hovered && mouse.X >= pos.X && mouse.X <= pos.X + iconSize && mouse.Y >= pos.Y && mouse.Y <= pos.Y + iconSize)
                            tooltip.Add($"{NumberFormat.Duration(TimeSpan.FromSeconds(at))}  {text}");
                        break;
                }
            }

            y = bottom;
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height + ImGui.GetStyle().ItemSpacing.Y));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + gutter);
        ImGui.TextColored(Theme.TextFaint, "0:00");
        var end = NumberFormat.Duration(TimeSpan.FromSeconds(pull.DurationSeconds));
        ImGui.SameLine(width - ImGui.CalcTextSize(end).X);
        ImGui.TextColored(Theme.TextFaint, end);

        if (tooltip.Count > 0)
            ImGui.SetTooltip(string.Join("\n", tooltip));
    }

    private enum TimelineMark { Dot, Diamond, Icon }

    /// <summary>A row of the timeline: a glyph or a job at its left, then its marks (time, icon, tooltip).</summary>
    private sealed record TimelineLane(
        FontAwesomeIcon? Glyph, string? Job, float Height, Vector4 Color, TimelineMark Mark, List<(double At, uint Icon, string Text)> Marks);

    // Places icons along a lane, one row lower when they would overlap the one before; the row of each, in order.
    private static List<(int Row, float Left)> PackIcons(List<(double At, uint Icon, string Text)> marks, Func<double, float> x, float size)
    {
        var ends = new List<float>();
        var placed = new (int Row, float Left)[marks.Count];
        foreach (var (mark, index) in marks.Select((m, i) => (m, i)).OrderBy(p => p.m.At))
        {
            var left = x(mark.At) - size / 2;
            var row = ends.FindIndex(e => e <= left);
            if (row < 0)
            {
                ends.Add(0);
                row = ends.Count - 1;
            }

            ends[row] = left + size + 1;
            placed[index] = (row, left);
        }

        return placed.ToList();
    }

    // A death in one line: who, of what, and the detail.
    private static string DeathText(PartyDeath d) =>
        $"{d.Player} died: {d.Cause}" + (DeathDetails(d) is { Length: > 0 } details ? $" {details}" : string.Empty);

    /// <summary>
    /// Failed mechanics: per player, the penalty debuffs taken (most first); in the history, every one in time order.
    /// </summary>
    // One failed mechanic: when, the debuff, and the action that put it.
    private static string MistakeText(MechanicMistake m) =>
        $"{NumberFormat.Duration(TimeSpan.FromSeconds(m.AtSeconds))}  {m.Status}  from {m.Ability}"
        + (m.Source.Length > 0 ? $" ({m.Source})" : string.Empty);

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
                              .Select(g => (Player: g.Key, g.First().Job, g.First().IsYou, Count: g.Count(), List: g.ToList(),
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
                    // The whole row is hoverable, for the detail of each failed mechanic.
                    var start = ImGui.GetCursorPos();
                    ImGui.Selectable($"##mistakerow{p.Player}", false,
                                     ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
                                     new Vector2(0, ImGui.GetTextLineHeight()));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Join("\n", p.List.Select(MistakeText)));
                    ImGui.SetCursorPos(start);
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
    private static void DrawGcdAnalysis(GcdAnalysis gcd, bool showGaps, double? uptime = null, double lostSeconds = 0)
    {
        if (uptime is { } share)
        {
            ImGui.TextColored(Theme.TextMuted, "GCD uptime");
            ImGui.SameLine(0, Theme.Px(6));
            ImGui.TextColored(Theme.Crystal, $"{share:0.0%}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{gcd.GcdCount} GCDs used\n{lostSeconds:0.0}s lost with the boss targetable and your GCD ready");
            return; // Live: the overall value only, the breakdown is in the history
        }

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
