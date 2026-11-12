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

/// <summary>Live encounter breakdown and pull history, opened with right click on the bar entry or /raidcompanion.</summary>
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

    // History: the table of enemy casts and party debuffs under the timeline.
    private bool showFightLog;
    private bool fightLogCasts = true;
    private bool fightLogDebuffs = true;
    private string fightLogSearch = string.Empty;
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

    public MainWindow(Plugin plugin) : base(Loc.T("Raid Companion###DpsInServerBarMain"))
    {
        Loc.Changed += () => WindowName = Loc.T("Raid Companion###DpsInServerBarMain");
        this.plugin = plugin;
        AllowBackgroundBlur = false;
        Size = new Vector2(760, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>History tab with this pull selected (when it is still recorded).</summary>
    public void ShowPull(Guid pullId)
    {
        ShowHistory();
        if (plugin.History.Pulls.FirstOrDefault(p => p.Id == pullId) is not { } pull)
            return;

        selectedId = pullId;
        // Another encounter picked in the filter: show them all, so the pull shows in the list.
        if (historyFilter.Length > 0 && !Progression.Encounter(pull).Equals(historyFilter, StringComparison.OrdinalIgnoreCase))
            historyFilter = string.Empty;
    }

    public void ShowHistory()
    {
        IsOpen = true;
        selectedTab = 1;
    }

    public override void PreDraw()
    {
        themeScope = Theme.Push();

        // Screenshot in progress: the window at the height of its whole content, even taller than the screen, kept in
        // the game's viewport (part of it off screen), and moved up slice after slice to capture all of it.
        if (shotStage is ShotStage.Grow or ShotStage.Slice or ShotStage.Capturing or ShotStage.Saving)
        {
            var screen = ImGui.GetMainViewport();
            ImGuiHelpers.ForceNextWindowMainViewport();
            Flags |= ImGuiWindowFlags.NoInputs; // no hover, no tooltip in the picture
            var x = Math.Clamp(shotSavedPos.X, screen.Pos.X, Math.Max(screen.Pos.X, screen.Pos.X + screen.Size.X - shotSavedSize.X));
            Position = new Vector2(MathF.Floor(x), screen.Pos.Y + SliceTop(shotSlice, screen.Size.Y));
            PositionCondition = ImGuiCond.Always;
            Size = new Vector2(shotSavedSize.X, shotHeight) / ImGuiHelpers.GlobalScale;
            SizeCondition = ImGuiCond.Always;
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, MinHeight), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
            return;
        }

        Flags &= ~ImGuiWindowFlags.NoInputs;

        if (shotStage == ShotStage.Restore)
        {
            Position = shotSavedPos;
            Size = shotSavedSize / ImGuiHelpers.GlobalScale;
            SizeCondition = ImGuiCond.Always;
            shotStage = ShotStage.None;
            restoreHeight = null;
            fitHeight = null;
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, MinHeight), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
            return;
        }

        // The frame after: the window is free to move again.
        if (Position is not null)
            Position = null;

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

    // Screenshot of a view (right click on its tab): the view is shown, the window grows to its whole content,
    // is captured and saved, then gets its size and place back.
    private enum ShotStage { None, Pending, Grow, Slice, Capturing, Saving, Restore }
    private static string[] TabNames => [Loc.T("Live"), Loc.T("History"), Loc.T("Progression")];
    private ShotStage shotStage;
    private int shotFrames;
    private Vector2 shotSavedPos, shotSavedSize;
    private float shotHeight;
    private int shotSlice;
    private readonly List<WindowScreenshot.Slice> shotSlices = new();
    private System.Threading.Tasks.Task<Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap>? shotCapture;
    private System.Threading.Tasks.Task<string>? shotTask;
    private Vector2 windowPos;
    // History's pull list: its height last drawn, and the one kept during a screenshot.
    private float pullListHeight, pullListFullHeight;
    private float? shotPullListHeight;
    private bool ShotInProgress => shotStage is ShotStage.Grow or ShotStage.Slice or ShotStage.Capturing or ShotStage.Saving;

    // Slices of the screen's height, the last one aligned on the window's bottom. Top of slice k, from the screen top.
    private int SliceCount(float screenHeight) => shotHeight <= screenHeight ? 1 : (int)Math.Ceiling(shotHeight / screenHeight);

    private float SliceTop(int slice, float screenHeight)
    {
        if (SliceCount(screenHeight) == 1)
        {
            var screen = ImGui.GetMainViewport();
            return MathF.Floor(Math.Clamp(shotSavedPos.Y - screen.Pos.Y, 0, screenHeight - shotHeight));
        }

        return slice < SliceCount(screenHeight) - 1 ? -slice * screenHeight : screenHeight - shotHeight;
    }
    // Top of the graph that fills the rest of the window (Live, Progression), to know the height of everything above.
    private float? graphTop;

    private void StartScreenshot(int tab)
    {
        selectedTab = tab;
        shotStage = ShotStage.Pending;
        shotFrames = 0;
    }

    // Height of the whole view: everything shown, a graph filling the window at a readable height.
    private float ContentHeight() =>
        fitHeight ?? (graphTop is { } top
                          ? Math.Max(windowSize.Y, top + Theme.Px(300) + ImGui.GetStyle().WindowPadding.Y)
                          : windowSize.Y);

    private void StepScreenshot()
    {
        var screen = ImGui.GetMainViewport();
        switch (shotStage)
        {
            case ShotStage.Pending:
                // A few frames for the view chosen to be drawn at its own height, and the menu to close.
                if (++shotFrames < 3)
                    return;
                shotSavedPos = windowPos;
                shotSavedSize = windowSize;
                shotPullListHeight = selectedTab == 1 && pullListHeight > 0 ? pullListHeight : null;
                shotHeight = MathF.Ceiling(ContentHeight() - (shotPullListHeight is null ? 0 : pullListFullHeight - pullListHeight));
                shotSlice = 0;
                shotStage = ShotStage.Grow;
                shotFrames = 0;
                return;

            case ShotStage.Grow:
                // The layout settles at the new height (what the content needs may change once).
                shotHeight = Math.Max(shotHeight, MathF.Ceiling(ContentHeight()));
                if (++shotFrames >= 4)
                {
                    shotStage = ShotStage.Slice;
                    shotFrames = 0;
                }
                return;

            case ShotStage.Slice:
                // The window in place for this slice: capture the whole game screen.
                if (++shotFrames < 3)
                    return;
                shotSlices.Add(new WindowScreenshot.Slice(null!, windowPos - screen.Pos));
                shotCapture = WindowScreenshot.CaptureScreen(screen.ID);
                shotStage = ShotStage.Capturing;
                return;

            case ShotStage.Capturing when shotCapture is { IsCompleted: true } capture:
                shotCapture = null;
                if (!capture.IsCompletedSuccessfully)
                {
                    WindowScreenshot.ReportError(capture.Exception?.GetBaseException());
                    EndScreenshot();
                    return;
                }

                shotSlices[^1] = shotSlices[^1] with { Screen = capture.Result };
                if (++shotSlice < SliceCount(screen.Size.Y))
                {
                    shotStage = ShotStage.Slice;
                    shotFrames = 0;
                    return;
                }

                // Every slice taken: put them together and save, the window gets its size back meanwhile.
                shotTask = WindowScreenshot.StitchAndSaveAsync(shotSlices.ToList(), windowSize, screen.Size, ImGui.GetStyle().WindowRounding, TabNames[selectedTab]);
                shotSlices.Clear();
                shotStage = ShotStage.Saving;
                return;

            case ShotStage.Saving when shotTask is { IsCompleted: true } task:
                if (task.IsCompletedSuccessfully)
                    WindowScreenshot.Report(task.Result);
                else
                    WindowScreenshot.ReportError(task.Exception?.GetBaseException());
                shotTask = null;
                shotStage = ShotStage.Restore;
                return;
        }
    }

    private void EndScreenshot()
    {
        foreach (var slice in shotSlices)
            slice.Screen?.Dispose();
        shotSlices.Clear();
        shotStage = ShotStage.Restore;
    }

    public override void PostDraw()
    {
        themeScope?.Dispose();
        themeScope = null;
    }

    public override void Draw()
    {
        Theme.TabStrip("main", ref selectedTab, tab =>
                       {
                           if (ImGui.MenuItem(Loc.T("Screenshot"), false, shotStage == ShotStage.None))
                               StartScreenshot(tab);
                           if (ImGui.IsItemHovered())
                               ImGui.SetTooltip(Loc.T("Saves a picture of this view, the window grown to show all of it, in Pictures\\Raid Companion."));
                       },
                       (FontAwesomeIcon.Bolt, Loc.T("Live")), (FontAwesomeIcon.History, Loc.T("History")), (FontAwesomeIcon.ChartLine, Loc.T("Progression")));
        DrawSourceAndSettings();
        Theme.Rule(2);

        fitHeight = null;
        graphTop = null;
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
        windowPos = ImGui.GetWindowPos();
        StepScreenshot();

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
        var status = $"{source.Name}: {source.StatusText}{(plugin.Configuration.Source == CombatSourceKind.Auto ? Loc.T(" (auto)") : "")}";
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
        if (Theme.IconButton("settings", FontAwesomeIcon.Cog, Loc.T("Settings")))
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
            ImGui.TextColored(Theme.GoldBright, string.IsNullOrEmpty(enc.Title) ? Loc.T("Encounter") : enc.Title);
            ImGui.SameLine();
            Theme.Badge(NumberFormat.Duration(enc.Duration), Theme.Crystal);
            if (!snapshot.IsActive)
            {
                ImGui.SameLine();
                Theme.Badge(Loc.T("ended"), Theme.TextMuted);
            }

            ImGui.SameLine();
        }

        DrawLiveButtons();

        if (snapshot is null || enc is null)
        {
            Theme.Hint(Loc.T("No combat data yet. Start a fight."));
            return;
        }

        var partyDps = snapshot.PartyDps(config.IncludeLimitBreakInParty);
        var threshold = config.FindThreshold(plugin.CurrentTerritory, enc.Title, plugin.Boss.Current?.BaseId ?? 0);
        DrawLiveCards(snapshot, partyDps, threshold);

        if (plugin.Ultimates.Current is { } phase)
            DrawPhase(phase);

        DrawPersonal(snapshot);

        Theme.SectionHeader(Loc.T("Party"), FontAwesomeIcon.Users);
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
            Theme.SectionHeader(Loc.T("Graph"), FontAwesomeIcon.ChartLine);
            DrawLiveGraph(threshold);
        }
    }

    /// <summary>Compact view and Opener practice, at the right of the line.</summary>
    private void DrawLiveButtons()
    {
        static float ChipWidth(string label) => ImGui.CalcTextSize(label).X + Theme.Px(20);
        var width = ChipWidth(Loc.T("Compact view")) + ImGui.GetStyle().ItemSpacing.X + ChipWidth(Loc.T("Opener practice"));
        if (ImGui.GetContentRegionAvail().X < width)
            ImGui.NewLine(); // no room right of the title: on their own line
        var x = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width;
        if (x > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(x);

        if (Theme.Chip(Loc.T("Compact view"), plugin.CompactOpen))
            plugin.SetCompact(!plugin.CompactOpen);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Only the party's DPS meter, in a small pop-up window."));

        ImGui.SameLine();
        if (Theme.Chip(Loc.T("Opener practice"), plugin.OpenerOpen))
            plugin.ToggleOpener();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Practice your job's opener: each action used in the right order is checked off."));
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
            cards.Add(new Card(Loc.T("DPS"), NumberFormat.Format(you.EncDps, NumberStyle.Grouped), Theme.Gold,
                                          party > 1 && rank > 0 ? Loc.F("#{0} of {1}",rank,party) : null));
            if (you.HasRdps)
                cards.Add(new Card(Loc.T("rDPS"), NumberFormat.Format(you.RDps, NumberStyle.Grouped), Theme.Crystal,
                                              party > 1 ? Loc.F("#{0} of {1}",snapshot.YourRank(byRdps: true),party) : null));
        }

        var elapsed = snapshot.Encounter.Duration.TotalSeconds;
        var boss = plugin.Ultimates.Current is null ? plugin.Boss.Current : null;
        var enrage = threshold?.EnrageSeconds ?? 0;

        if (enrage > 0 && PartyGapToEnrage(snapshot, boss, partyDps, elapsed, threshold!) is { } gap)
        {
            var color = gap >= 0 ? Theme.Good : Theme.Bad;
            cards.Add(new Card(Loc.T("Party DPS"), NumberFormat.Format(partyDps, NumberStyle.Grouped), color,
                               Loc.F("{0:+0.0%;-0.0%} vs enrage",gap), color));
        }
        else if (threshold is { EffectiveMinPartyDps: > 0 } check)
        {
            var color = partyDps >= check.EffectiveMinPartyDps ? Theme.Good : Theme.Bad;
            cards.Add(new Card(Loc.T("Party DPS"), NumberFormat.Format(partyDps, NumberStyle.Grouped), color,
                               Loc.F("need {0} ",NumberFormat.Format(check.EffectiveMinPartyDps, NumberStyle.Grouped))
                               + $"({partyDps / check.EffectiveMinPartyDps - 1:+0.0%;-0.0%})", color));
        }
        else
        {
            cards.Add(new Card(Loc.T("Party DPS"), NumberFormat.Format(partyDps, NumberStyle.Grouped), Theme.Gold));
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
                    hint = margin >= 0 ? Loc.F("kill {0} before enrage",span) : Loc.F("kill {0} after enrage",span);
                    hintColor = margin >= 0 ? Theme.Good : Theme.Bad;
                }
                else
                {
                    hint = Loc.F("kill in {0}",NumberFormat.Duration(eta));
                }
            }

            cards.Add(new Card(boss.Name, $"{boss.HpFraction:0.0%}", Theme.Bad, hint, hintColor));
        }

        if (plugin.Pulls.PaceVsBest() is { } pace)
        {
            var color = pace >= 0 ? Theme.Good : Theme.Bad;
            cards.Add(new Card(Loc.T("Pace vs best"), $"{pace:+0.0%;-0.0%}", color, Loc.T("damage so far"), ValueColor: color));
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

        Theme.SectionHeader(Loc.T("Personal"), FontAwesomeIcon.User);
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
            Theme.Hint(Loc.T("Phase not supported yet."));
            return;
        }

        if (phase.IsTransition)
        {
            Theme.Hint(Loc.F("Transition, {0}.",NumberFormat.Duration(phase.Elapsed)));
            return;
        }

        if (!phase.Started)
        {
            Theme.Hint(Loc.F("{0}: waiting for the boss.",phase.Phase!.Name));
            return;
        }

        var cards = new List<Card>
        {
            new Card(phase.Phase!.Name, $"{phase.HpFraction:0.0%}", Theme.Bad,
                                Loc.F("phase time {0}",NumberFormat.Duration(phase.Elapsed))),
        };

        if (phase.RequiredDps is { } required)
        {
            var color = phase.BossDps >= required ? Theme.Good : Theme.Bad;
            cards.Add(new Card(Loc.T("DPS on boss"), NumberFormat.Format(phase.BossDps, NumberStyle.Grouped), color,
                                          Loc.F("need {0} ",NumberFormat.Format(required, NumberStyle.Grouped))
                                          + $"({phase.BossDps / required - 1:+0.0%;-0.0%})", color));
        }
        else
        {
            cards.Add(new Card(Loc.T("DPS on boss"), NumberFormat.Format(phase.BossDps, NumberStyle.Grouped), Theme.Gold,
                                          Loc.T("no enrage set")));
        }

        if (phase.ProjectedKill is { } eta)
        {
            Vector4? color = phase.EnrageSeconds is { } enrage ? eta.TotalSeconds <= enrage ? Theme.Good : Theme.Bad : null;
            cards.Add(new Card(Loc.T("Projected kill"), NumberFormat.Duration(eta), color ?? Theme.Crystal,
                                          phase.EnrageSeconds is { } e ? Loc.F("enrage {0}",NumberFormat.Duration(TimeSpan.FromSeconds(e))) : null,
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
                graphTop = ImGui.GetCursorPosY();
                DpsGraph.DrawUptime("##liveuptime", [(Loc.T("You"), up.AvailableBySecond, up.ActiveBySecond)], ImGui.GetContentRegionAvail().Y, up.Gcd.Gaps,
                                    plugin.Sources.Latest is { } latest ? plugin.Sources.Native.PartyDeaths.Since(latest.StartUtc) : null);
            }
            else
                Theme.Hint(Loc.T("No fight yet."));
            return;
        }

        var pull = plugin.Pulls.Current;
        if (pull is null)
        {
            Theme.Hint(Loc.T("The graph shows the pull in progress; finished pulls are in the History tab."));
            return;
        }

        var points = graph.ShowParty ? pull.PartyPoints : pull.YourPoints;
        var cumulative = Timeline.Resample(points, pull.LastSeconds);

        var series = new List<DpsGraph.Series>();
        graph.AddCurves(series, graph.ShowParty ? Loc.T("Party") : Loc.T("You"), cumulative, window);

        var best = plugin.History.BestFor(pull.Title);
        if (best is not null && !graph.ShowParty)
            series.Add(new DpsGraph.Series(Loc.T("Best pull average"), Timeline.Average(best.YourDamageTimeline)));

        liveElastic = true;
        graphTop = ImGui.GetCursorPosY();
        DpsGraph.Draw("##livegraph", series, ImGui.GetContentRegionAvail().Y,
                      graph.ShowParty ? threshold?.EffectiveMinPartyDps : null, plugin.Sources.Native.PartyDeaths.Since(pull.StartUtc));
    }

    // ---------------------------------------------------------------- History

    private void DrawHistory()
    {
        var pulls = plugin.History.Pulls;
        if (pulls.Count == 0)
        {
            Theme.Hint(Loc.T("No pull recorded yet."));
            Theme.Hint(plugin.Configuration.RecordOnlyInDuties
                           ? Loc.T("Pulls are recorded inside duties only (see Settings > Pulls history).")
                           : Loc.T("Pulls shorter than the minimum duration are ignored."));
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
        // During a screenshot the list keeps the height it had when it was asked for, while the window grows.
        var frozen = ShotInProgress && shotPullListHeight is not null;
        if (frozen)
            shown = shotPullListHeight!.Value;
        else
            (pullListHeight, pullListFullHeight) = (shown, fullHeight);

        using (var child = ImRaii.Child("##pulllist", new Vector2(-1, shown), true))
        {
            if (child)
                DrawPullTable(visible);
        }

        DrawPullListGrip();

        // Where what follows the list would start with every pull shown: the window needs no more than that.
        var afterFullList = ImGui.GetCursorPosY() + (frozen ? 0 : fullHeight - shown);
        if (selected is null)
        {
            Theme.Hint(Loc.T("Select a pull to see its details. Tick \"vs\" on another one to compare."));
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
            Theme.Hint(Loc.T("Choose an encounter above to follow its progression pull after pull."));
            return;
        }

        var fight = Progression.PullsOf(pulls, historyFilter);
        if (fight.Count == 0)
        {
            Theme.Hint(Loc.T("No pull of this encounter in the ticked kinds of content."));
            return;
        }

        var groups = Progression.Groups(fight, plugin.Configuration.SavedGroups);
        var group = groups.FirstOrDefault(g => g.Key == plugin.Configuration.ProgressionGroup);
        var chosen = group?.Pulls ?? fight;
        DrawProgressionSelection(groups, group, chosen);

        var prog = chosen.Where(p => !p.ExcludedFromProgression).ToList();
        if (prog.Count == 0)
        {
            Theme.Hint(Loc.T("Every pull is left out: tick some in Pulls."));
            return;
        }

        var territory = prog[^1].TerritoryId;
        var ultimate = Progression.IsUltimate(territory);
        var scores = prog.Select(Progression.Score).ToList();
        var kills = prog.Count(p => p.Outcome == PullOutcome.Kill);
        var best = prog.Where(p => Progression.Score(p) is not null).MaxBy(p => Progression.Score(p)!.Value);

        var cards = new List<Card>
        {
            new Card(Loc.T("Pulls"), prog.Count.ToString(), Theme.Gold, kills > 0 ? (kills == 1 ? Loc.T("1 kill") : Loc.F("{0} kills",kills)) : Loc.T("no kill yet"),
                                kills > 0 ? Theme.Good : null),
        };
        if (best is not null)
        {
            var number = prog.IndexOf(best) + 1;
            cards.Add(new Card(Loc.T("Best pull"), Progression.Label(best), Theme.Good,
                                          Loc.F("pull {0}, {1:dd/MM HH:mm}",number,best.StartUtc.ToLocalTime())));
        }

        var recent = prog.TakeLast(10).Where(p => Progression.Score(p) is not null).ToList();
        if (recent.Count > 0 && recent.MaxBy(p => Progression.Score(p)!.Value) is { } recentBest)
            cards.Add(new Card(Loc.T("Best of last 10"), Progression.Label(recentBest), Theme.Crystal));

        var instanceHours = Progression.InstanceHours(prog);
        var played = TimeSpan.FromSeconds(prog.Sum(p => p.DurationSeconds));
        cards.Add(new Card(Loc.T("Time in fight"), NumberFormat.Duration(played), Theme.Gold,
                                      Loc.F("{0} in the instance since {1:dd/MM}",NumberFormat.Duration(TimeSpan.FromHours(instanceHours[^1])),prog[0].StartUtc.ToLocalTime())));
        // A kill is as far as it goes: the first one, else the last enemy cast of the furthest wipe.
        if (prog.FirstOrDefault(p => p.Outcome == PullOutcome.Kill) is { } kill)
        {
            var number = prog.IndexOf(kill) + 1;
            cards.Add(new Card(Loc.T("Furthest mechanic"), Loc.T("Kill"), Theme.Good,
                               Loc.F("pull {0}, {1:dd/MM HH:mm}",number,kill.StartUtc.ToLocalTime()), ValueColor: Theme.Good));
        }
        else if (prog.Where(p => p.LastBossCast.Length > 0)
                     .MaxBy(p => (Progression.Phase(p), p.DurationSeconds)) is { } furthest)
        {
            var number = prog.IndexOf(furthest) + 1;
            cards.Add(new Card(Loc.T("Furthest mechanic"), furthest.LastBossCast, Theme.Crystal,
                                          Loc.F("pull {0}, at {1}",number,NumberFormat.Duration(TimeSpan.FromSeconds(furthest.DurationSeconds)))));
        }

        DrawCards(cards, uniform: true);

        if (ultimate)
        {
            var phases = prog.Where(p => p.Outcome != PullOutcome.Kill)
                             .GroupBy(Progression.Phase).OrderBy(g => g.Key)
                             .Select(g => $"{UltimateDefinitions.Label(territory, g.Key)} ×{g.Count()}");
            Theme.Hint(Loc.T("Wipes per phase: ") + string.Join("   ", phases));
        }

        // What wiped you, most common first.
        var causes = prog.Select(p => (Pull: p, Cause: Progression.WipeCause(p)))
                         .Where(c => c.Cause is not null)
                         .GroupBy(c => c.Cause!)
                         .Select(g => (Cause: g.Key, Count: g.Count(), Last: prog.IndexOf(g.Last().Pull) + 1))
                         .OrderByDescending(c => c.Count)
                         .Take(8)
                         .ToList();

        Theme.SectionHeader(Loc.T("Wiped by"), FontAwesomeIcon.Skull);
        var wipes = prog.Count(p => p.Outcome != PullOutcome.Kill);
        ImGui.TextColored(Theme.TextMuted, wipes == 1 ? Loc.T("1 pull without a kill") : Loc.F("{0} pulls without a kill",wipes));
        Theme.HelpMarker(Loc.T("For each wipe, the most common cause of death in its last 20 seconds (or its first death). Needs the deaths of the party, recorded since this version."));
        if (causes.Count == 0)
        {
            Theme.Hint(Loc.T("No wipe with recorded deaths yet."));
        }
        else
        {
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX;
            using var table = ImRaii.Table("##wipecauses", 3, flags);
            if (table)
            {
                ImGui.TableSetupColumn(Loc.T("Cause"), ImGuiTableColumnFlags.WidthStretch, 4f);
                ImGui.TableSetupColumn(Loc.T("Wipes"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(Loc.T("Wipes")).X + Theme.Px(8));
                ImGui.TableSetupColumn(Loc.T("Last pull"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(Loc.T("Last pull")).X + Theme.Px(8));
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

        Theme.SectionHeader(Loc.T("Pull after pull"), FontAwesomeIcon.ChartLine);
        if (Theme.Chip(Loc.T("Per pull"), !progressionByTime))
            progressionByTime = false;
        ImGui.SameLine(0, Theme.Px(4));
        if (Theme.Chip(Loc.T("Per time in the instance"), progressionByTime))
            progressionByTime = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Hours spent in the instance: the fights and the time between pulls of a same evening (breaks of more than 30 minutes left out)."));

        var xs = progressionByTime ? instanceHours.ToList() : Enumerable.Range(1, prog.Count).Select(i => (double)i).ToList();
        graphTop = ImGui.GetCursorPosY();
        var clicked = DpsGraph.DrawProgression(
            "##progression", xs, progressionByTime ? Loc.T("Time in the instance") : Loc.T("Pull"), scores,
            prog.Select(p => p.Outcome == PullOutcome.Kill).ToList(), Progression.Max(territory),
            ultimate ? Progression.PhaseNames(territory) : null, ImGui.GetContentRegionAvail().Y,
            i => Loc.F("Pull {0}  {1} in the instance\n{2:dd/MM HH:mm}  {3}\n",i + 1,NumberFormat.Duration(TimeSpan.FromHours(instanceHours[i])),prog[i].StartUtc.ToLocalTime(),NumberFormat.Duration(TimeSpan.FromSeconds(prog[i].DurationSeconds)))
                 + Progression.Label(prog[i])
                 + (Progression.WipeCause(prog[i]) is { } cause ? Loc.F("\nWiped by {0}",cause) : string.Empty)
                 + (prog[i].Outcome != PullOutcome.Kill && prog[i].LastBossCast.Length > 0 ? Loc.F("\nLast enemy cast: {0}",prog[i].LastBossCast) : string.Empty)
                 + Loc.T("\nClick to open this pull."));
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
        using (var combo = ImRaii.Combo(Loc.T("Group"), group is null ? Loc.F("All pulls ({0})",groups.Sum(g => g.Pulls.Count)) : GroupLabel(group),
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
                ImGui.InputTextWithHint("##groupsearch", Loc.T("Search a group or a player"), ref groupSearch, 64);

                if (groupSearch.Length == 0 && ImGui.Selectable(Loc.F("All pulls ({0})",groups.Sum(g => g.Pulls.Count)), group is null))
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
            if (Theme.GhostButton(group.Saved is null ? Loc.T("Save group") : Loc.T("Rename")))
            {
                groupName = group.Saved?.Name ?? string.Empty;
                ImGui.OpenPopup("##groupname");
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(group.Saved is null
                                     ? Loc.T("Give this group a name, to find it easily in the list. Its regular players are kept.")
                                     : Loc.T("Rename this group."));
            }

            if (group.Saved is { } savedGroup)
            {
                ImGui.SameLine(0, Theme.Px(4));
                using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
                {
                    if (Theme.GhostButton(Loc.T("Forget")))
                    {
                        config.SavedGroups.Remove(savedGroup);
                        Choose(string.Empty);
                    }
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(Loc.T("Hold Ctrl to forget this group's name (its pulls are kept)."));
            }

            using var namePopup = ImRaii.Popup("##groupname");
            if (namePopup)
            {
                ImGui.TextColored(Theme.TextMuted, group.Saved is null ? Loc.T("Name of this group") : Loc.T("New name"));
                if (ImGui.IsWindowAppearing())
                    ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(Theme.Px(240));
                var done = ImGui.InputTextWithHint("##groupnameinput", Loc.T("e.g. Static"), ref groupName, 48, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.SameLine();
                done |= Theme.GhostButton(Loc.T("OK"));
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
        if (Theme.GhostButton(excluded > 0 ? Loc.F("Pulls ({0}/{1})",chosen.Count - excluded,chosen.Count) : Loc.T("Pulls")))
            ImGui.OpenPopup("##progressionpulls");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Tick the pulls to count, e.g. to leave out a night with another group."));

        Theme.HelpMarker(Loc.T("Groups are found from the party members of each pull: a pull goes with the group it shares at least half of its players with. Hover a group for its players.\nSave a group under a name to find it easily: its regular players are kept, and the pulls they play together go to it from then on. Saved groups come first, in gold."));

        using var popup = ImRaii.Popup("##progressionpulls");
        if (!popup)
            return;

        if (Theme.GhostButton(Loc.T("Tick all")))
            SetExcluded(chosen, false);
        ImGui.SameLine();
        if (Theme.GhostButton(Loc.T("Untick all")))
            SetExcluded(chosen, true);
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.TextColored(Theme.TextFaint, Loc.T("Shift+click: up to the last pull clicked"));

        // One block per entry into the duty, newest first: its box ticks or unticks all its pulls at once.
        var runs = Runs(chosen);
        runs.Reverse();
        using var child = ImRaii.Child("##progressionpulllist",
                                       new Vector2(Theme.Px(480), Math.Min(Theme.Px(360), (chosen.Count + runs.Count + 1) * ImGui.GetFrameHeightWithSpacing())));
        if (!child)
            return;

        // Newest first, with the evening it was played and how far it got. Shift+click sets every pull between the last
        // one clicked and this one the same way.
        var list = runs.SelectMany(r => Enumerable.Reverse(r)).ToList();
        var runStart = 0;
        for (var i = 0; i < list.Count; i++)
        {
            var p = list[i];
            var run = runs.First(r => r.Contains(p));
            if (i == runStart)
            {
                runStart += run.Count;
                if (i > 0)
                    ImGui.Spacing();
                var ticked = run.Count(x => !x.ExcludedFromProgression);
                var all = ticked == run.Count;
                if (ImGui.Checkbox($"{Loc.F("Entry {0}", run[0].StartUtc.ToLocalTime().ToString("dd/MM HH:mm"))}###run{run[0].Id}", ref all))
                    SetExcluded(run, !all);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Loc.T("Tick or untick every pull of this entry into the duty."));
                ImGui.SameLine(Theme.Px(170));
                var best = run.OrderByDescending(x => Progression.Score(x) ?? -1).First();
                ImGui.TextColored(Theme.TextMuted, Loc.F("{0}/{1} pulls", ticked, run.Count));
                ImGui.SameLine(Theme.Px(270));
                ImGui.TextColored(best.Outcome == PullOutcome.Kill ? Theme.Good : Theme.TextMuted, Loc.F("best: {0}", Progression.Label(best)));
            }

            using var indent = ImRaii.PushIndent();
            var included = !p.ExcludedFromProgression;
            if (ImGui.Checkbox($"{p.StartUtc.ToLocalTime():HH:mm}###{p.Id}", ref included))
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

    /// <summary>
    /// The pulls split by entry into the duty, oldest first. Older pulls, recorded without it: the pulls of the same
    /// zone less than 15 minutes apart go together.
    /// </summary>
    private static List<List<PullRecord>> Runs(IEnumerable<PullRecord> pulls)
    {
        var runs = new List<List<PullRecord>>();
        PullRecord? previous = null;
        foreach (var p in pulls.OrderBy(p => p.StartUtc))
        {
            var same = previous is not null
                       && (p.InstanceRun is { } run
                               ? run == previous.InstanceRun
                               : previous.InstanceRun is null && p.TerritoryId == previous.TerritoryId
                                 && p.StartUtc - previous.StartUtc.AddSeconds(previous.DurationSeconds) < TimeSpan.FromMinutes(15));
            if (!same)
                runs.Add([]);
            runs[^1].Add(p);
            previous = p;
        }

        return runs;
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
        if (Theme.Chip(all ? Loc.T("None") : Loc.T("All"), false))
            Set(all ? ContentCategory.None : offered);
    }

    private void DrawHistoryFilter(IReadOnlyList<PullRecord> pulls)
    {
        // The fights go by their instance (with the fight's name when the instance has two, e.g. a door boss).
        var zoneOf = pulls.Where(p => Progression.Encounter(p).Length > 0)
                          .GroupBy(Progression.Encounter, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => g.Select(p => p.Zone).FirstOrDefault(z => z.Length > 0) ?? g.Key,
                                        StringComparer.OrdinalIgnoreCase);
        string Label(string title)
        {
            if (!zoneOf.TryGetValue(title, out var zone))
                return title;
            zone = char.ToUpperInvariant(zone[0]) + zone[1..];
            return zoneOf.Values.Count(z => z.Equals(zoneOf[title], StringComparison.OrdinalIgnoreCase)) > 1 ? $"{zone} ({title})" : zone;
        }

        var titles = zoneOf.Keys.OrderBy(Label).ToList();

        ImGui.SetNextItemWidth(Theme.Px(260));
        using (var combo = ImRaii.Combo(Loc.T("Instance"), historyFilter.Length == 0 ? Loc.T("All") : Label(historyFilter),
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
                ImGui.InputTextWithHint("##encountersearch", Loc.T("Search a fight"), ref encounterSearch, 64);

                if (encounterSearch.Length == 0 && ImGui.Selectable(Loc.T("All"), historyFilter.Length == 0))
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

        var label = Loc.T("Clear all");
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
            ImGui.SetTooltip(Loc.T("Hold Ctrl to delete the whole history."));
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
            ImGui.SetTooltip(Loc.T("Drag to make the list taller, double-click to reset it."));

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
        var innerWidth = Math.Max(ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize, Theme.Px(840));
        using var table = ImRaii.Table("##pulls", 12, flags, new Vector2(-1, -1), innerWidth);
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(1, 1); // the date and the headers stay visible while scrolling
        ImGui.TableSetupColumn(Loc.T("When"), ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn(Loc.T("Encounter"), ImGuiTableColumnFlags.WidthStretch, 2.4f);
        ImGui.TableSetupColumn(Loc.T("Time"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn(Loc.T("Result"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(Loc.T("Boss HP"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn(Loc.T("Job"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn(Loc.T("DPS"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(Loc.T("rDPS"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(Loc.T("pDPS"), ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn(Loc.T("Rank"), ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn(Loc.T("vs"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
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
            // In an ultimate, the time in the phase reached (the whole pull on hover).
            Text(NumberFormat.Duration(TimeSpan.FromSeconds(p.PhaseSeconds ?? p.DurationSeconds)), Theme.TextMuted);
            if (p.PhaseSeconds is not null && ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.F("Time in {0}. Whole pull: {1}", UltimateDefinitions.Label(p.TerritoryId, Progression.Phase(p)),
                                       NumberFormat.Duration(TimeSpan.FromSeconds(p.DurationSeconds))));
            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + framePadding - Theme.Px(1));
            switch (p.Outcome)
            {
                case PullOutcome.Kill:
                    Theme.Badge(Loc.T("Kill"), Theme.Good);
                    break;
                case PullOutcome.Wipe:
                    Theme.Badge(Loc.T("Wipe"), Theme.Bad);
                    break;
                default:
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Theme.Px(1));
                    ImGui.TextColored(Theme.TextFaint, "-");
                    break;
            }

            // Boss HP left when the pull ended (of the phase reached, in an ultimate), or Kill.
            if (p.Outcome == PullOutcome.Kill)
                Text(Loc.T("Kill"), Theme.Good);
            else if (p.BossHpLeft is { } hpLeft)
                Text($"{hpLeft:0.0%}", Theme.TextMuted);
            else
                Text("-", Theme.TextFaint);

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
                ImGui.SetTooltip(Loc.T("Compare the selected pull with this one."));

            ImGui.TableNextColumn();
            if (Theme.IconButton("del", FontAwesomeIcon.TrashAlt, Loc.T("Delete this pull"), Theme.TextFaint))
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

        Theme.SectionHeader(Loc.T("Party"), FontAwesomeIcon.Users);
        DrawCombatantTable(pull.Combatants);

        // Pulls recorded before deaths and failed mechanics were tracked have no GCD analysis either: nothing to say for them.
        if (pull.Gcd is not null)
        {
            DrawDeaths(pull.PartyDeaths);
            if (pull.Mitigations.Count > 0)
                DrawMitigations(pull.Mitigations);
            DrawMistakes(pull.Mistakes, timeline: true);
            if (pull.PartyDeaths.Count > 0 || pull.Mistakes.Count > 0 || pull.TankSwaps.Count > 0 || pull.Mitigations.Count > 0
                || pull.BossCasts.Count > 0 || pull.Debuffs.Count > 0)
            {
                DrawTimeline(pull);
                if (showFightLog)
                    DrawFightLog(pull);
            }
        }

        if (!config.ShowGraph)
        {
            detailsHeight = ImGui.GetCursorPosY() - top;
            return;
        }

        Theme.SectionHeader(Loc.T("Graph"), FontAwesomeIcon.ChartLine);
        var window = config.GraphWindowSeconds;
        graph.DrawOptions(window);

        if (graph.ShowUptime)
        {
            var uptimes = new List<(string, float[], float[])> { (Loc.T("This pull"), pull.UptimeAvailableBySecond, pull.UptimeActiveBySecond) };
            if (compare is { UptimeAvailableBySecond.Length: > 0 })
                uptimes.Add((Loc.T("Compared"), compare.UptimeAvailableBySecond, compare.UptimeActiveBySecond));
            detailsHeight = ImGui.GetCursorPosY() - top + DpsGraph.MinHeight;
            DpsGraph.DrawUptime("##historyuptime", uptimes, ImGui.GetContentRegionAvail().Y, pull.Gcd?.Gaps, pull.PartyDeaths);
            return;
        }

        var series = new List<DpsGraph.Series>();
        graph.AddCurves(series, Loc.T("This pull"), graph.ShowParty ? pull.PartyDamageTimeline : pull.YourDamageTimeline, window);
        if (compare is not null)
        {
            graph.AddCurves(series, Loc.T("Compared"), graph.ShowParty ? compare.PartyDamageTimeline : compare.YourDamageTimeline,
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
            var text = rate ? Loc.F("{0:+0.0;-0.0} pts vs compared",diff) : Loc.F("{0:+0.0%;-0.0%} vs compared",diff);
            return (text, diff >= 0 ? Theme.Good : Theme.Bad);
        }

        var cards = new List<Card>();

        var (dpsHint, dpsColor) = Gap(pull.YourDps, compare?.YourDps);
        if (dpsHint is null && pull.PartySize > 1 && pull.Rank > 0)
            dpsHint = Loc.F("#{0} of {1}",pull.Rank,pull.PartySize);
        cards.Add(new Card(Loc.T("DPS"), NumberFormat.Format(pull.YourDps, NumberStyle.Grouped), Theme.Gold, dpsHint, dpsColor));

        if (pull.YourRdps is { } rdps)
        {
            var (hint, color) = Gap(rdps, compare?.YourRdps);
            if (hint is null && pull.PartySize > 1 && pull.RdpsRank > 0)
                hint = Loc.F("#{0} of {1}",pull.RdpsRank,pull.PartySize);
            cards.Add(new Card(Loc.T("rDPS"), NumberFormat.Format(rdps, NumberStyle.Grouped), Theme.Crystal, hint, color));
        }

        var (partyHint, partyColor) = Gap(pull.PartyDps, compare?.PartyDps);
        cards.Add(new Card(Loc.T("Party DPS"), NumberFormat.Format(pull.PartyDps, NumberStyle.Grouped), Theme.Gold, partyHint, partyColor));

        // In an ultimate, the time in the phase reached, the whole pull below.
        var durationHint = compare is not null ? Loc.F("vs {0}",NumberFormat.Duration(TimeSpan.FromSeconds(compare.PhaseSeconds ?? compare.DurationSeconds)))
                           : pull.PhaseSeconds is not null ? Loc.F("whole pull {0}", NumberFormat.Duration(TimeSpan.FromSeconds(pull.DurationSeconds)))
                           : null;
        var durationCaption = pull.PhaseSeconds is not null
                                  ? Loc.F("{0} duration", UltimateDefinitions.Label(pull.TerritoryId, Progression.Phase(pull)))
                                  : Loc.T("Duration");
        cards.Add(new Card(durationCaption, NumberFormat.Duration(TimeSpan.FromSeconds(pull.PhaseSeconds ?? pull.DurationSeconds)),
                                      pull.Outcome switch { PullOutcome.Kill => Theme.Good, PullOutcome.Wipe => Theme.Bad, _ => Theme.TextMuted },
                                      durationHint));

        // Boss HP left when the pull ended (of the phase reached, in an ultimate), or Kill.
        if (pull.Outcome == PullOutcome.Kill || pull.BossHpLeft is not null)
        {
            var ultimate = Progression.IsUltimate(pull.TerritoryId);
            var value = pull.Outcome == PullOutcome.Kill ? Loc.T("Kill") : Loc.F("{0:0.0%} left", pull.BossHpLeft!.Value);
            string? hint = ultimate ? UltimateDefinitions.Label(pull.TerritoryId, Progression.Phase(pull))
                           : pull.BossName.Length > 0 ? pull.BossName : null;
            Vector4? hintColor = null;
            if (compare is not null && pull.Outcome != PullOutcome.Kill && Progression.Score(pull) is { } score && Progression.Score(compare) is { } other)
            {
                // Progress compared: boss HP points (or phases) further or not as far.
                hint = ultimate ? hint : Loc.F("{0:+0.0;-0.0} pts vs compared", score - other);
                hintColor = score >= other ? Theme.Good : Theme.Bad;
            }

            cards.Add(new Card(Loc.T("Boss HP"), value, Theme.Bad, hint, hintColor,
                               pull.Outcome == PullOutcome.Kill ? Theme.Good : null));
        }

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
        var losses = DamageDownLoss.Of(pull);
        var tank = role == JobRole.Tank;
        if (pull.Uptime is null && !gauge && pull.Positionals.Count == 0 && pull.ComboMistakes.Count == 0 && dots is null
            && heals.Count == 0 && !(tank && pull.TankSwaps.Count > 0) && pull.Mitigations.Count == 0 && losses.Count == 0)
            return;

        Theme.SectionHeader(Loc.T("Personal"), FontAwesomeIcon.User);
        if (pull.Uptime is { } uptime)
            FoldGcd(pull, compare, uptime);
        if (losses.Count > 0)
            FoldDamageLoss(pull, losses);
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

    /// <summary>DPS lost to Damage Down (failed mechanics) and to Weakness / Brink of Death (after a raise).</summary>
    private static void FoldDamageLoss(PullRecord pull, IReadOnlyList<DamageLoss> losses)
    {
        var duration = Math.Max(1, pull.DurationSeconds);
        double Dps(IEnumerable<DamageLoss> l) => l.Sum(x => x.LostDamage) / duration;
        string Format(double dps) => NumberFormat.Format(dps, NumberStyle.Grouped);

        var open = Fold(Loc.T("Damage Down & Weakness"), "pdown");
        var down = losses.Where(l => !l.Revive).ToList();
        var revive = losses.Where(l => l.Revive).ToList();
        ImGui.TextColored(Theme.Warn, Loc.T("(experimental)"));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Experimental: an estimate, the strength of Damage Down is not always known. To be checked against your logs."));
        // Not checked yet against logs: the figures only once unfolded.
        if (!open)
            return;

        ImGui.TextColored(Theme.Bad, Loc.F("-{0} DPS", Format(Dps(losses))));
        if (down.Count > 0)
        {
            ImGui.SameLine(0, Theme.Px(10));
            ImGui.TextColored(Theme.TextFaint, Loc.F("Damage Down -{0} ({1}x)", Format(Dps(down)), down.Count));
        }

        if (revive.Count > 0)
        {
            ImGui.SameLine(0, Theme.Px(10));
            ImGui.TextColored(Theme.TextFaint, Loc.F("Weakness -{0} ({1}x)", Format(Dps(revive)), revive.Count));
        }

        var dealt = pull.YourDamageTimeline.Length > 0 ? pull.YourDamageTimeline[^1] : 0;
        if (dealt > 0)
        {
            Theme.Hint(Loc.F("Without them you would have done {0} DPS instead of {1} (+{2:0.0%}).",
                             Format((dealt + losses.Sum(l => l.LostDamage)) / duration), Format(dealt / duration),
                             losses.Sum(l => l.LostDamage) / dealt));
        }

        using (var table = DetailTable("##pdowntable", (Loc.T("Time"), 60), (Loc.T("Debuff"), -3), (Loc.T("Length"), 60),
                                       (Loc.T("Strength"), 70), (Loc.T("Lost"), -2)))
        {
            if (table)
            {
                foreach (var l in losses)
                {
                    ImGui.TableNextRow();
                    TimeCell(l.Start);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(l.Revive ? Theme.Warn : Theme.Bad, l.Status);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextMuted, $"{l.End - l.Start:0}s");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(l.Guessed ? Theme.TextFaint : Theme.Text, $"{(l.Guessed ? "~" : "")}-{l.Reduction:0%}");
                    if (l.Guessed && ImGui.IsItemHovered())
                        ImGui.SetTooltip(Loc.T("The game did not send this debuff's strength: the usual one is used (-90% in an ultimate, -25% elsewhere)."));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Bad, Loc.F("{0} damage (-{1} DPS)", Format(l.LostDamage), Format(l.LostDamage / duration)));
                }
            }
        }

        Theme.Hint(Loc.T("Experimental estimate: your damage while the debuff was on, scaled back up by its strength. Weakness -25% and Brink of Death -50% (main stats lowered by that much)."));
        ImGui.TreePop();
    }

    private static void FoldGcd(PullRecord pull, PullRecord? compare, double uptime)
    {
        var open = Fold(Loc.T("GCD uptime"), "pgcd");
        ImGui.TextColored(Theme.Crystal, $"{uptime:0.0%}");
        if (compare?.Uptime is { } other)
        {
            var diff = (uptime - other) * 100;
            ImGui.SameLine(0, Theme.Px(8));
            ImGui.TextColored(diff >= 0 ? Theme.Good : Theme.Bad, Loc.F("{0:+0.0;-0.0} pts vs compared",diff));
        }

        ImGui.SameLine(0, Theme.Px(8));
        ImGui.TextColored(Theme.TextFaint, pull.Gcd is { GcdCount: > 0 } g
                                               ? Loc.F("{0} GCDs, lost {1:0.0}s",g.GcdCount,pull.UptimeLostSeconds)
                                               : Loc.F("lost {0:0.0}s",pull.UptimeLostSeconds));
        if (!open)
            return;

        if (pull.PhaseUptimes.Count > 0)
        {
            Theme.Hint(Loc.T("Per phase: ") + string.Join("   ", pull.PhaseUptimes.Select(
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
        var open = Fold(Loc.T("Gauge overcap"), "pgauge");
        if (waste.Count == 0)
        {
            ImGui.TextColored(Theme.Good, Loc.T("none"));
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
            Theme.Hint(Loc.T("No gauge lost to overcapping."));
        }
        else
        {
            using (var table = DetailTable("##pgaugetable", (Loc.T("Gauge"), -2), (Loc.T("Lost"), 60), (Loc.T("Times"), 50), (Loc.T("Overcapped by"), -4)))
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
        var open = Fold(Loc.T("Positionals"), "ppos");
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
            Theme.Hint(Loc.T("Every positional landed."));
        }
        else
        {
            using var table = DetailTable("##ppostable", (Loc.T("Time"), 60), (Loc.T("Action"), -3), (Loc.T("Needed"), -2));
            if (table)
            {
                foreach (var u in missed)
                {
                    ImGui.TableNextRow();
                    TimeCell(u.AtSeconds);
                    IconCell(ActionIcons.OfAction(u.ActionId), u.Action);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Bad, u.Rear ? Loc.T("rear") : Loc.T("flank"));
                }
            }
        }

        ImGui.TreePop();
    }

    private static void FoldCombos(IReadOnlyList<ComboMistake> mistakes)
    {
        var open = Fold(Loc.T("Combo errors"), "pcombo");
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

        using (var table = DetailTable("##pcombotable", (Loc.T("Time"), 60), (Loc.T("Action"), -2), (Loc.T("Error"), -5)))
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
        var open = Fold(Loc.T("DoT uptime"), "pdot");
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
            ImGui.TextColored(Theme.TextFaint, Loc.F("off the boss {0:0.0}s ",dot.AvailableSeconds - dot.ActiveSeconds)
                                               + Loc.F("of {0}",NumberFormat.Duration(TimeSpan.FromSeconds(dot.AvailableSeconds))));
        }

        var drops = dots.SelectMany(dot => (dot.Drops ?? []).Select(x => (Dot: dot, Drop: x))).OrderBy(x => x.Drop.AtSeconds).ToList();
        if (dots.All(dot => dot.Drops is null))
        {
            Theme.Hint(Loc.T("The drops were not recorded for this pull."));
        }
        else if (drops.Count == 0)
        {
            Theme.Hint(Loc.T("Never off the boss for a second or more."));
        }
        else
        {
            using var table = DetailTable("##pdottable", (Loc.T("Time"), 60), (Loc.T("DoT"), -3), (Loc.T("Off for"), -2));
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
        var open = Fold(Loc.T("Overheal"), "pheal");
        ImGui.TextColored(Theme.Text, mine is null ? "-" : $"{mine.Share:0%}");
        ImGui.SameLine(0, Theme.Px(8));
        ImGui.TextColored(Theme.TextFaint, Loc.T("for information"));
        if (!open)
            return;

        DrawOverheal(healers);
        if (mine?.Sources is { Length: > 0 } sources)
        {
            using var table = DetailTable("##phealtable", (Loc.T("Your heals"), -3), (Loc.T("Healing"), 80), (Loc.T("Overheal"), 80), ("%", 50));
            if (table)
            {
                foreach (var s in sources)
                {
                    ImGui.TableNextRow();
                    IconCell(s.Icon, s.IsHot ? Loc.F("{0} (HoT)",s.Name) : s.Name);
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
            Theme.Hint(Loc.T("The detail per heal was not recorded for this pull."));
        }

        ImGui.TreePop();
    }

    /// <summary>When each party member used each of their mitigations and defensive buffs: tanks, healers, then DPS.</summary>
    private static void DrawMitigations(IReadOnlyList<MitigationUse> uses)
    {
        Theme.SectionHeader(Loc.T("Mitigation"), FontAwesomeIcon.ShieldAlt, Loc.T("Mitigations (on the party or on the boss) and defensive buffs used by each party member, with the time of each use. GCD shields (Succor, Eukrasian Prognosis...) are left out. Party members far from you may be missed."));

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
            ImGui.TextColored(p.IsYou ? Theme.GoldBright : Theme.Text, Names.Show(p.Player));
            ImGui.SameLine(0, Theme.Px(8));
            ImGui.TextColored(Theme.TextFaint, player.Count() == 1 ? Loc.T("1 use") : Loc.F("{0} uses",player.Count()));
            if (!open)
                continue;

            using (var table = DetailTable($"##mittable{player.Key}", (Loc.T("Mitigation"), -2), (Loc.T("Uses"), 40), (Loc.T("Times"), -5)))
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
        var open = Fold(Loc.T("Mitigation"), "pmit");
        ImGui.TextColored(mine.Count == 0 ? Theme.TextFaint : Theme.Text, mine.Count == 0 ? Loc.T("none") : Loc.F("{0} uses",mine.Count));
        if (!open)
            return;

        if (mine.Count == 0)
        {
            Theme.Hint(Loc.T("You used no mitigation or defensive buff."));
        }
        else
        {
            using var table = DetailTable("##pmittable", (Loc.T("Action"), -3), (Loc.T("Uses"), 60));
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
        var open = Fold(Loc.T("Tank swaps"), "pswaps");
        ImGui.TextColored(Theme.Text, swaps.Count.ToString());
        if (!open)
            return;

        if (swaps.Count == 0)
            Theme.Hint(Loc.T("The boss never changed tanks."));
        else
            DrawTankSwapTable(swaps);
        ImGui.TreePop();
    }

    /// <summary>One line with the uptime of each of your DoTs on the boss, by icon and name.</summary>
    private static void DrawDotUptime(IReadOnlyList<DotUptime> dots)
    {
        if (dots.Count == 0 || dots[0].AvailableSeconds < 5)
            return;

        ImGui.TextColored(Theme.TextMuted, Loc.T("DoT uptime"));
        foreach (var dot in dots)
        {
            ImGui.SameLine(0, Theme.Px(8));
            ActionIcons.Draw(dot.Icon, ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(dot.Uptime >= 0.95 ? Theme.Good : dot.Uptime >= 0.85 ? Theme.Warn : Theme.Bad, $"{dot.Name} {dot.Uptime:0.0%}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.F("Off the boss for {0:0.0}s of {1}",dot.AvailableSeconds - dot.ActiveSeconds,NumberFormat.Duration(TimeSpan.FromSeconds(dot.AvailableSeconds))));
        }

        Theme.HelpMarker(Loc.T("Share of the time your DoT was on the boss, while the boss could be attacked (jumps, transitions and cutscenes left out)."));
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
            var open = ImGui.TreeNode(mine is null ? Loc.T("Overheal###overheal") : Loc.F("Overheal ({0:0%})###overheal",mine.Share));
            ImGui.SameLine();
            Theme.HelpMarker(Loc.T("Share of each healer's healing (pets and HoTs included) that went over full HP. For information only: pre-healing a raidwide or a HoT running out its time also overheals."));
            if (!open)
                return;
        }
        else
        {
            ImGui.TextColored(Theme.TextMuted, Loc.T("Overheal"));
        }

        foreach (var h in healers.OrderByDescending(h => h.IsYou))
        {
            if (!foldable)
                ImGui.SameLine(0, Theme.Px(8));
            JobInfo.DrawIcon(h.Job, ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(h.IsYou ? Theme.GoldBright : Theme.Text, $"{(h.IsYou ? Loc.T("You") : Names.Show(h.Player))} {h.Share:0%}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.F("{0} of {1} healing went over full HP",NumberFormat.Format(h.Overheal, NumberStyle.Compact),NumberFormat.Format(h.Healing, NumberStyle.Compact)));
        }

        if (foldable)
        {
            ImGui.TreePop();
            return;
        }

        Theme.HelpMarker(Loc.T("Share of each healer's healing (pets and HoTs included) that went over full HP. For information only: pre-healing a raidwide or a HoT running out its time also overheals."));
    }

    /// <summary>Foldable list of the boss going from one tank to another, with why.</summary>
    private static void DrawTankSwaps(IReadOnlyList<TankSwap> swaps, bool showEmpty = false)
    {
        if (swaps.Count == 0 && showEmpty)
        {
            ImGui.TextColored(Theme.TextMuted, Loc.T("Tank swaps"));
            ImGui.SameLine(0, Theme.Px(6));
            ImGui.TextColored(Theme.TextFaint, "0");
            return;
        }

        if (swaps.Count == 0 || !ImGui.TreeNode(Loc.F("Tank swaps ({0})###tankswaps",swaps.Count)))
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
                ImGui.TableSetupColumn(Loc.T("Time"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(60));
                ImGui.TableSetupColumn(Loc.T("Swap"));
                ImGui.TableSetupColumn(Loc.T("Why"));
                ImGui.TableHeadersRow();
                foreach (var s in swaps)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextFaint, NumberFormat.Duration(TimeSpan.FromSeconds(s.AtSeconds)));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{Names.Show(s.From)} → {Names.Show(s.To)}");
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
        ImGui.TextColored(Theme.TextMuted, Loc.T("Positionals"));
        ImGui.SameLine(0, Theme.Px(6));
        ImGui.TextColored(share >= 1 ? Theme.Good : Theme.Bad, $"{hits}/{uses.Count} ({share:0%})");

        var iconSize = ImGui.GetTextLineHeight();
        foreach (var action in uses.GroupBy(u => u.ActionId).Where(g => g.Any(u => !u.Hit)).OrderByDescending(g => g.Count(u => !u.Hit)))
        {
            var missed = action.Where(u => !u.Hit).ToList();
            ImGui.SameLine(0, Theme.Px(10));
            var hovered = ActionIcons.Draw(ActionIcons.OfAction(action.Key), iconSize);
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(Theme.Bad, names ? Loc.F("{0} {1}/{2} missed",missed[0].Action,missed.Count,action.Count()) : $"{missed.Count}/{action.Count()}");
            if (hovered || ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Loc.F("{0} ({1}): {2} of {3} missed, at ",missed[0].Action,(missed[0].Rear ? Loc.T("rear") : Loc.T("flank")),missed.Count,action.Count())
                                 + string.Join(", ", missed.Select(u => NumberFormat.Duration(TimeSpan.FromSeconds(u.AtSeconds)))));
            }
        }

        Theme.HelpMarker(
            Loc.T("Your positional actions whose positional bonus landed, read from the bonus the game gave each hit (True North and bosses without positionals count as hits).\nHover an action for when you missed it."));
    }

    /// <summary>One line with your combo errors, by icon of the action used (with its name in the history); hover for details.</summary>
    private static void DrawComboMistakes(IReadOnlyList<ComboMistake> mistakes, bool names, bool showNone)
    {
        if (mistakes.Count == 0 && !showNone)
            return;

        ImGui.TextColored(Theme.TextMuted, Loc.T("Combo errors"));
        ImGui.SameLine(0, Theme.Px(6));
        if (mistakes.Count == 0)
        {
            ImGui.TextColored(Theme.Good, Loc.T("none"));
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

        Theme.HelpMarker(Loc.T("Combo steps used without the step before them (they lose their combo bonus), combos restarted before they were finished, and combos whose timer ran out before the next step. Actions the game lets you use without breaking a combo are ignored."));
    }

    private static string ComboMistakeText(ComboMistake m) =>
        $"{NumberFormat.Duration(TimeSpan.FromSeconds(m.AtSeconds))}  {ComboMistakeDetail(m)}";

    private static string ComboMistakeDetail(ComboMistake m) => m.Kind switch
    {
        ComboMistakeKind.NoCombo => Loc.F("{0} without its combo (needs {1} before)",m.Action,m.Expected),
        ComboMistakeKind.TimedOut => Loc.F("combo lost after {0}: its timer ran out before the next step",m.Action),
        _ => Loc.F("{0} restarted the combo: {1} was waiting for its next step",m.Action,m.Expected),
    };

    /// <summary>A foldable strip of the pull with each clip at its time, shown by the icon of the oGCD that caused it.</summary>
    private static void DrawClipTimeline(GcdAnalysis gcd, double pullSeconds, bool folded = true)
    {
        var clips = gcd.Gaps.Where(g => g.IsClip).ToList();
        if (clips.Count == 0 || (folded && !ImGui.TreeNode(Loc.F("Clipping timeline ({0})###clipping",clips.Count))))
            return;

        var marks = clips.Select(g =>
        {
            var locked = g.LockedSeconds > 0 ? g.LockedSeconds : g.Seconds;
            var cause = g.ActionId != 0 ? Loc.F(" by {0}",ActionIcons.NameOf(g.ActionId)) : string.Empty;
            return (g.AtSeconds, ActionIcons.OfAction(g.ActionId),
                    Loc.F("{0}  GCD clipped {1:0.00}s{2}",NumberFormat.Duration(TimeSpan.FromSeconds(g.AtSeconds)),locked,cause));
        }).ToList();
        DrawIconStrip("clipping", marks, [], pullSeconds);
        Theme.Hint(Loc.T("Each icon is the oGCD that landed when your GCD was held by an animation lock."));
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
        ImGui.TextColored(Theme.TextMuted, Loc.T("Gauge overcap"));
        if (waste.Count == 0)
        {
            ImGui.SameLine(0, Theme.Px(6));
            ImGui.TextColored(Theme.Good, Loc.T("none"));
        }

        foreach (var w in waste)
        {
            ImGui.SameLine(0, Theme.Px(8));
            var iconHovered = ActionIcons.Draw(ActionIcons.OfGauge(w.Resource), ImGui.GetTextLineHeight());
            if (iconHovered && w.Actions.Length > 0)
                ImGui.SetTooltip(Loc.F("Overcapped by: {0}",w.Actions));
            ImGui.SameLine(0, Theme.Px(4));
            var amount = w.Max <= 5 ? $"{w.Wasted:0.#}" : $"{w.Wasted:0}";
            ImGui.TextColored(Theme.Warn, $"{w.Resource} {amount}");
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(Theme.TextFaint, w.Count > 1 ? $"({w.Count}×)" : "(1×)");
            if (ImGui.IsItemHovered() && w.Actions.Length > 0)
                ImGui.SetTooltip(Loc.F("Overcapped by: {0}",w.Actions));
        }

        Theme.HelpMarker(
            Loc.T("Gauge lost because it was too full when it was filled. Spend it before using the actions that fill it.\nThe gain of each action is learned from your own uses while the gauge had room, so the first uses of an action are not counted. Lilies, Addersgall and Polyglot: time spent full, in stacks lost.\nHover a gauge for the actions that overcapped it."));
    }

    /// <summary>Every death of the party in time order, with its cause; hover one for the hits taken just before.</summary>
    private static void DrawDeaths(IReadOnlyList<PartyDeath> deaths)
    {
        Theme.SectionHeader(Loc.T("Deaths"), FontAwesomeIcon.Skull);
        if (deaths.Count == 0)
            ImGui.TextColored(Theme.Good, Loc.T("No death."));
        else
            ImGui.TextColored(Theme.Bad, deaths.Count == 1 ? Loc.T("1 death") : Loc.F("{0} deaths",deaths.Count));
        Theme.HelpMarker(Loc.T("What killed each party member: the hit that took their last HP, or a guess when there was none (a debuff running out, a fall from the arena). Hover a death for the hits taken just before.\nParty members far from you may be missed."));
        if (deaths.Count == 0)
            return;

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX;
        using var table = ImRaii.Table("##deaths", 3, flags);
        if (!table)
            return;

        ImGui.TableSetupColumn(Loc.T("Time"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("00:00").X + Theme.Px(12));
        ImGui.TableSetupColumn(Loc.T("Player"), ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn(Loc.T("Killed by"), ImGuiTableColumnFlags.WidthStretch, 4f);
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
            ImGui.TextColored(d.IsYou ? Theme.GoldBright : Theme.Text, Names.Show(d.Player));
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
        ImGui.TextColored(d.IsYou ? Theme.GoldBright : Theme.Text, Names.Show(d.Player));
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextMuted, Loc.F("died at {0}",NumberFormat.Duration(TimeSpan.FromSeconds(d.AtSeconds))));
        if (d.HpBefore > 0)
            ImGui.TextColored(Theme.TextMuted, Loc.F("HP before the last hit: {0}",d.HpBefore.ToString("N0", CultureInfo.InvariantCulture)));

        if (d.Recap.Count == 0)
        {
            ImGui.TextColored(Theme.TextFaint, Loc.T("No hit recorded just before."));
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

    /// <summary>The enemy casts and party debuffs of a pull, by time, in a scrolling table.</summary>
    private void DrawFightLog(PullRecord pull)
    {
        ImGui.Dummy(new Vector2(0, Theme.Px(4)));
        if (Theme.Chip(Loc.F("Boss casts ({0})", pull.BossCasts.Count), fightLogCasts))
            fightLogCasts = !fightLogCasts;
        ImGui.SameLine(0, Theme.Px(4));
        if (Theme.Chip(Loc.F("Debuffs ({0})", pull.Debuffs.Count), fightLogDebuffs))
            fightLogDebuffs = !fightLogDebuffs;
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.SetNextItemWidth(Theme.Px(220));
        ImGui.InputTextWithHint("##fightlogsearch", Loc.T("Search an action, a debuff or a player"), ref fightLogSearch, 64);

        bool Match(params string[] texts) =>
            fightLogSearch.Length == 0 || texts.Any(t => t.Contains(fightLogSearch, StringComparison.OrdinalIgnoreCase));

        // One row per cast or debuff: time, kind, name, on whom, from whom, end, length.
        var rows = new List<(double At, bool Cast, string Name, string On, string Job, string From, double? End, double? Length)>();
        if (fightLogCasts)
        {
            rows.AddRange(pull.BossCasts.Where(c => Match(c.Action, c.Caster))
                              .Select(c => (c.AtSeconds, true, c.Action, string.Empty, string.Empty, c.Caster,
                                            c.CastSeconds > 0 ? c.AtSeconds + c.CastSeconds : (double?)null,
                                            c.CastSeconds > 0 ? c.CastSeconds : (double?)null)));
        }

        if (fightLogDebuffs)
        {
            rows.AddRange(pull.Debuffs.Where(d => Match(d.Status, d.Player, Names.Show(d.Player), d.Source))
                              .Select(d => (d.AppliedSeconds, false, d.Status, Names.Show(d.Player), d.Job, d.Source, d.EndSeconds,
                                            d.EndSeconds is { } end ? end - d.AppliedSeconds : (double?)null)));
        }

        if (rows.Count == 0)
        {
            Theme.Hint(pull.BossCasts.Count == 0 && pull.Debuffs.Count == 0
                           ? Loc.T("Casts and debuffs were not recorded for this pull.")
                           : Loc.T("Nothing matches."));
            return;
        }

        rows.Sort((a, b) => a.At.CompareTo(b.At));
        var height = Math.Min(Theme.Px(420), (rows.Count + 1) * ImGui.GetTextLineHeightWithSpacing() + Theme.Px(12));
        using var table = ImRaii.Table("##fightlog", 7,
                                       ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp
                                       | ImGuiTableFlags.BordersOuterH, new Vector2(0, height));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Loc.T("Time"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(50));
        ImGui.TableSetupColumn(Loc.T("Type"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(60));
        ImGui.TableSetupColumn(Loc.T("Name"), ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn(Loc.T("On"), ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn(Loc.T("From"), ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn(Loc.T("Ends"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(80));
        ImGui.TableSetupColumn(Loc.T("Length"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(60));
        ImGui.TableHeadersRow();

        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            TimeCell(row.At);

            ImGui.TableNextColumn();
            ImGui.TextColored(row.Cast ? Theme.Warn : Theme.Bad, row.Cast ? Loc.T("Cast") : Loc.T("Debuff"));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Name);

            ImGui.TableNextColumn();
            if (row.On.Length > 0)
            {
                ImGui.TextUnformatted(row.On);
                if (row.Job.Length > 0)
                {
                    ImGui.SameLine(0, Theme.Px(6));
                    ImGui.TextColored(Theme.TextFaint, row.Job);
                }
            }

            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextMuted, row.From);

            ImGui.TableNextColumn();
            if (row.End is { } end)
                ImGui.TextColored(Theme.TextFaint, NumberFormat.Duration(TimeSpan.FromSeconds(end)));
            else if (!row.Cast)
                ImGui.TextColored(Theme.TextFaint, Loc.T("end of pull"));

            ImGui.TableNextColumn();
            if (row.Length is { } length)
                ImGui.TextColored(Theme.TextFaint, Loc.F("{0:0.0}s", length));
        }
    }

    /// <summary>
    /// The pull as lanes, chosen with the boxes above it: deaths (red), tank swaps (blue), failed mechanics (orange) and
    /// mitigation (one lane per player, by the icon of each action). Minute ticks; hover a mark for its detail.
    /// </summary>
    private void DrawTimeline(PullRecord pull)
    {
        var config = plugin.Configuration;
        Theme.SectionHeader(Loc.T("Timeline"), FontAwesomeIcon.Stream);

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

        config.TimelineDeaths = Toggle(Loc.F("Deaths ({0})",pull.PartyDeaths.Count), config.TimelineDeaths, Theme.Bad, first: true);
        config.TimelineTankSwaps = Toggle(Loc.F("Tank swaps ({0})",pull.TankSwaps.Count), config.TimelineTankSwaps, Theme.Tank);
        config.TimelineMistakes = Toggle(Loc.F("Failed mechanics ({0})",pull.Mistakes.Count), config.TimelineMistakes, Theme.Warn);
        config.TimelineMitigation = Toggle(Loc.F("Mitigation ({0})",pull.Mitigations.Count), config.TimelineMitigation, Theme.Crystal);

        // On the right: the table of the enemy casts and party debuffs, below the timeline.
        if (pull.BossCasts.Count > 0 || pull.Debuffs.Count > 0)
        {
            var label = Loc.T("Casts & debuffs");
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(label).X - Theme.Px(20));
            if (Theme.Chip(label, showFightLog))
                showFightLog = !showFightLog;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("The casts of the enemies and the debuffs of the party members, with when they were applied and ran out, in a table under the timeline."));
        }
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
                                       pull.TankSwaps.Select(s => (s.AtSeconds, 0u, Loc.F("Tank swap {0} → {1} ({2})",Names.Show(s.From),Names.Show(s.To),s.Reason))).ToList()));
        }

        if (config.TimelineMistakes && pull.Mistakes.Count > 0)
        {
            lanes.Add(new TimelineLane(FontAwesomeIcon.ExclamationTriangle, null, dotLane, Theme.Warn, TimelineMark.Dot,
                                       pull.Mistakes.Select(m => (m.AtSeconds, 0u, $"{Names.Show(m.Player)}: {MistakeText(m)}")).ToList()));
        }

        if (config.TimelineMitigation)
        {
            static int RoleOrder(string job) => JobRoles.Of(job) switch { JobRole.Tank => 0, JobRole.Healer => 1, _ => 2 };
            foreach (var player in pull.Mitigations.GroupBy(u => u.Player)
                                       .OrderBy(g => RoleOrder(g.First().Job)).ThenByDescending(g => g.First().IsYou).ThenBy(g => g.Key))
            {
                var p = player.First();
                lanes.Add(new TimelineLane(null, p.Job, iconLane, Theme.Crystal, TimelineMark.Icon,
                                           player.Select(u => (u.AtSeconds, ActionIcons.OfAction(u.ActionId), $"{Names.Show(u.Player)}: {u.Action}")).ToList()));
            }
        }

        if (lanes.Count == 0)
        {
            Theme.Hint(Loc.T("Nothing to show: tick what the timeline should show."));
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
        Loc.F("{0} died: {1}",Names.Show(d.Player),d.Cause) + (DeathDetails(d) is { Length: > 0 } details ? $" {details}" : string.Empty);

    /// <summary>
    /// Failed mechanics: per player, the penalty debuffs taken (most first); in the history, every one in time order.
    /// </summary>
    // One failed mechanic: when, the debuff, and the action that put it.
    private static string MistakeText(MechanicMistake m) =>
        Loc.F("{0}  {1}  from {2}",NumberFormat.Duration(TimeSpan.FromSeconds(m.AtSeconds)),m.Status,m.Ability)
        + (m.Source.Length > 0 ? $" ({m.Source})" : string.Empty);

    private static void DrawMistakes(IReadOnlyList<MechanicMistake> mistakes, bool timeline)
    {
        Theme.SectionHeader(Loc.T("Failed mechanics"), FontAwesomeIcon.ExclamationTriangle);
        if (mistakes.Count == 0)
            ImGui.TextColored(Theme.Good, Loc.T("No failed mechanic."));
        else
            ImGui.TextColored(Theme.Bad, mistakes.Count == 1 ? Loc.T("1 failed mechanic") : Loc.F("{0} failed mechanics",mistakes.Count));
        Theme.HelpMarker(Loc.T("Penalty debuffs put on party members by enemy actions: Vulnerability Up, Damage Down, Down for the Count. Tankbuster vulnerabilities on tanks can be ignored in Settings > Pulls history."));
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
                ImGui.TableSetupColumn(Loc.T("Player"), ImGuiTableColumnFlags.WidthStretch, 2f);
                ImGui.TableSetupColumn(Loc.T("Count"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(Loc.T("Count")).X + Theme.Px(8));
                ImGui.TableSetupColumn(Loc.T("Debuffs"), ImGuiTableColumnFlags.WidthStretch, 4f);
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
                    ImGui.TextColored(p.IsYou ? Theme.GoldBright : Theme.Text, Names.Show(p.Player));
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

        using var node = ImRaii.TreeNode(Loc.F("Timeline ({0})###mistaketimeline",mistakes.Count));
        if (!node)
            return;

        foreach (var m in mistakes)
        {
            ImGui.TextColored(Theme.TextFaint, NumberFormat.Duration(TimeSpan.FromSeconds(m.AtSeconds)));
            ImGui.SameLine();
            ImGui.TextColored(m.IsYou ? Theme.GoldBright : Theme.Text, Names.Show(m.Player));
            ImGui.SameLine();
            ImGui.TextColored(Theme.Bad, m.Status);
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextMuted, m.Source.Length > 0 ? Loc.F("from {0} ({1})",m.Ability,m.Source) : Loc.F("from {0}",m.Ability));
        }
    }

    /// <summary>
    /// One line splitting the lost GCD time by cause, with a help tooltip; in the history, the longest gaps too.
    /// </summary>
    private static void DrawGcdAnalysis(GcdAnalysis gcd, bool showGaps, double? uptime = null, double lostSeconds = 0)
    {
        if (uptime is { } share)
        {
            ImGui.TextColored(Theme.TextMuted, Loc.T("GCD uptime"));
            ImGui.SameLine(0, Theme.Px(6));
            ImGui.TextColored(Theme.Crystal, $"{share:0.0%}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.F("{0} GCDs used\n{1:0.0}s lost with the boss targetable and your GCD ready",gcd.GcdCount,lostSeconds));
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

        Part(Loc.T("Clipping"), gcd.ClipSeconds, gcd.ClipCount > 0 ? $" ({gcd.ClipCount}×)" : null, first: true);
        Part(Loc.T("Late"), gcd.LateSeconds);
        Part(Loc.T("No GCD"), gcd.NoGcdSeconds, gcd.NoGcdCount > 0 ? $" ({gcd.NoGcdCount}×)" : null);
        Part(Loc.T("Dead"), gcd.DeadSeconds);
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.TextColored(gcd.LostGcds >= 1 ? Theme.Warn : Theme.Good, Loc.F("~{0:0.0} GCD lost",gcd.LostGcds));
        Theme.HelpMarker(
            Loc.F("{0} GCDs used, {1:0.00}s each on average. Time with the boss targetable and your GCD ready but unused:\n",gcd.GcdCount,gcd.AverageRecast)
            + Loc.T("- Clipping: held by an oGCD's animation lock. Weave fewer oGCDs per GCD, or earlier.\n")
            + Loc.T("- Late: free to act, the GCD just came late (reaction, ping, a short step).\n")
            + Loc.T("- No GCD: a whole GCD or more without one (movement, mechanics). Slidecast, use instant GCDs while moving.\n")
            + Loc.T("- Dead: time spent dead."));

        if (!showGaps || gcd.Gaps.Count == 0)
            return;

        var longest = gcd.Gaps.OrderByDescending(g => g.Seconds).Take(5).OrderBy(g => g.AtSeconds);
        Theme.Hint(Loc.T("Longest gaps: ") + string.Join("   ", longest.Select(g =>
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
            ImGui.TextColored(Theme.TextMuted, Loc.T("Sort by"));
            ImGui.SameLine();
            if (Theme.Chip(Loc.T("DPS"), !config.SortByRdps) && config.SortByRdps)
            {
                config.SortByRdps = false;
                config.Save();
            }

            ImGui.SameLine(0, Theme.Px(4));
            if (Theme.Chip(Loc.T("rDPS"), config.SortByRdps) && !config.SortByRdps)
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
        ImGui.TableSetupColumn(Loc.T("Player"), ImGuiTableColumnFlags.WidthStretch, 3.4f);
        ImGui.TableSetupColumn(Loc.T("DPS"), ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn(Loc.T("DPS %"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
        if (showRdps)
        {
            ImGui.TableSetupColumn(Loc.T("rDPS"), ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableSetupColumn(Loc.T("rDPS %"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
        }

        ImGui.TableSetupColumn(Loc.T("Crit"), ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn(Loc.T("DH"), ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn(Loc.T("Deaths"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(Loc.T("Deaths")).X + Theme.Px(8));
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
            ImGui.TextColored(c.IsYou ? Theme.GoldBright : Theme.Text, Names.Show(c.Name));

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
                        Loc.F("Given to others' buffs: -{0} DPS\n",NumberFormat.Format(b.GivenDps, NumberStyle.Grouped))
                        + Loc.F("Received from own buffs: +{0} DPS\n",NumberFormat.Format(b.ReceivedDps, NumberStyle.Grouped))
                        + Loc.F("Crit rate used: {0:0.0%}   DH rate used: {1:0.0%}\n",b.CritRate,b.DirectHitRate)
                        + Loc.F("({0} hits observed; exact stats are used for you at level 100)",b.Samples));
                }

                Cell(c.Rdps is { } own ? Share(own, rdpsTotal) : "-", Theme.TextMuted);
            }

            Cell($"{c.CritPercent:0}%", Theme.TextMuted);
            Cell($"{c.DirectHitPercent:0}%", Theme.TextMuted);
            Cell(c.Deaths.ToString(), c.Deaths > 0 ? Theme.Bad : Theme.TextFaint);
        }
    }
}
