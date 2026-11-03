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
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;

    // Connection fields are edited locally and only applied on "Apply", to avoid reconnecting on every keystroke.
    private string hostInput;
    private bool editingCustomSeparator;
    private string fightFilter = string.Empty;
    private int portInput;

    public ConfigWindow(Plugin plugin) : base("DPS In Server Bar - Settings###DpsInServerBarConfig")
    {
        this.plugin = plugin;
        hostInput = plugin.Configuration.Host;
        portInput = plugin.Configuration.Port;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(680, 420) };
        Size = new Vector2(860, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private sealed record Section(FontAwesomeIcon Icon, string Label, string Description, System.Action Draw);

    private Section[] Sections => sections ??=
    [
        new(FontAwesomeIcon.Desktop, "Display", "What the server bar shows, in which order, and how.", DrawDisplayTab),
        new(FontAwesomeIcon.CommentDots, "Chat summary", "The recap printed in chat after each pull and each ultimate phase.", DrawChatSummaryTab),
        new(FontAwesomeIcon.History, "Pulls history", "Which pulls are recorded and kept.", DrawPullsTab),
        new(FontAwesomeIcon.Crosshairs, "DPS check", "Boss HP and enrage of each fight: the party DPS needed to clear it.", DrawDpsCheckTab),
        new(FontAwesomeIcon.ChartLine, "FFLogs", "Estimate of your FFLogs percentile, from the FFLogs API.", DrawFflogsTab),
        new(FontAwesomeIcon.Plug, "Data source", "Where the damage numbers come from: ACT or the built-in tracker.", DrawSourceTab),
    ];

    private Section[]? sections;
    private int selectedSection;
    private IDisposable? themeScope;

    public override void PreDraw() => themeScope = Theme.Push();

    public override void PostDraw()
    {
        themeScope?.Dispose();
        themeScope = null;
    }

    public override void Draw()
    {
        Theme.WindowHeader(FontAwesomeIcon.Cog, "DPS In Server Bar", $"Settings  v{Plugin.PluginInterface.Manifest.AssemblyVersion}");

        var sidebarWidth = Theme.Px(170);
        using (var nav = ImRaii.Child("##nav", new Vector2(sidebarWidth, -1)))
        {
            if (nav)
            {
                for (var i = 0; i < Sections.Length; i++)
                {
                    if (Theme.NavItem($"##nav{i}", Sections[i].Icon, Sections[i].Label, i == selectedSection))
                        selectedSection = i;
                }
            }
        }

        // Thin vertical rule between the menu and the page.
        ImGui.SameLine();
        var top = ImGui.GetCursorScreenPos();
        var height = ImGui.GetContentRegionAvail().Y;
        ImGui.GetWindowDrawList().AddLine(top, top with { Y = top.Y + height }, Theme.U32(Theme.Line));
        ImGui.Dummy(new Vector2(Theme.Px(6), 0));
        ImGui.SameLine();

        var section = Sections[selectedSection];
        using var page = ImRaii.Child("##page", new Vector2(-1, -1));
        if (!page)
            return;

        Theme.Icon(section.Icon, Theme.Gold);
        ImGui.SameLine();
        ImGui.TextColored(Theme.GoldBright, section.Label);
        Theme.Hint(section.Description);
        Theme.Rule();
        section.Draw();
    }

    private void DrawDisplayTab()
    {
        var c = plugin.Configuration;
        var changed = false;

        Theme.SectionHeader("Value");
        var metric = (int)c.Metric;
        ImGui.SetNextItemWidth(Theme.Px(240));
        if (ImGui.Combo("Metric", ref metric, "Personal DPS\0Party DPS (pDPS)\0Raid-contributed DPS (rDPS)\0"))
        {
            c.Metric = (DisplayMetric)metric;
            changed = true;
        }

        var style = (int)c.NumberStyle;
        ImGui.SetNextItemWidth(Theme.Px(240));
        if (ImGui.Combo("Number format", ref style, "12345\012,345\012.3k\0"))
        {
            c.NumberStyle = (NumberStyle)style;
            changed = true;
        }

        Theme.SectionHeader("Server bar elements");
        Theme.Hint("Tick what to show and use the arrows to change the order.");
        changed |= DrawBarLayout(c);
        changed |= DrawSeparator(c);
        DrawBarPreviewButton();

        Theme.SectionHeader("Behavior");
        changed |= Checkbox("Show DPS graphs in the main window", c.ShowGraph, v => c.ShowGraph = v);
        changed |= Checkbox("Compute rDPS (FFLogs-style)", c.ComputeRdps, v => c.ComputeRdps = v);
        Help("Credits the damage your buffs gave to others and removes what others' buffs gave you. "
             + "Computed by the built-in tracker, which then also runs next to ACT. Approximation of FFLogs' rDPS.");
        changed |= Checkbox("Include Limit Break in party DPS", c.IncludeLimitBreakInParty, v => c.IncludeLimitBreakInParty = v);
        changed |= Checkbox("Hide while no data source is available", c.HideWhenDisconnected, v => c.HideWhenDisconnected = v);

        var hide = c.HideAfterSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Hide N seconds after combat (0 = never)", ref hide))
        {
            c.HideAfterSeconds = Math.Clamp(hide, 0, 3600);
            changed = true;
        }

        Theme.SectionHeader("Colors");
        changed |= Checkbox("Use colors", c.UseColors, v => c.UseColors = v);
        using (ImRaii.Disabled(!c.UseColors))
        {
            changed |= ColorEdit("Final value", c.FinalColor, v => c.FinalColor = v);
            changed |= ColorEdit("pDPS below check", c.BelowThresholdColor, v => c.BelowThresholdColor = v);
            changed |= ColorEdit("pDPS above check", c.AboveThresholdColor, v => c.AboveThresholdColor = v);
        }

        if (changed)
            plugin.OnConfigChanged();
    }

    private static readonly (SummaryElement Element, string Label)[] SummaryLabels =
    [
        (SummaryElement.Job, "Job"),
        (SummaryElement.Dps, "DPS"),
        (SummaryElement.Rdps, "rDPS"),
        (SummaryElement.Percentile, "FFLogs %"),
        (SummaryElement.Uptime, "GCD"),
        (SummaryElement.PartyDps, "pDPS"),
        (SummaryElement.Deaths, "Deaths"),
        (SummaryElement.KilledBy, "Killed by"),
    ];

    private static readonly (string Label, string Value)[] SeparatorPresets =
    [
        ("Space", " "),
        ("|", " | "),
        ("/", " / "),
        ("-", " - "),
        ("Double space", "  "),
    ];

    private bool DrawSeparator(Configuration c)
    {
        var changed = false;
        var preset = Array.FindIndex(SeparatorPresets, p => p.Value == c.BarSeparator);
        var labels = SeparatorPresets.Select(p => p.Label).Append("Custom").ToArray();
        var custom = labels.Length - 1;
        var selected = editingCustomSeparator || preset < 0 ? custom : preset;

        ImGui.SetNextItemWidth(130);
        if (ImGui.Combo("Separator between elements", ref selected, labels, labels.Length))
        {
            // "Custom" keeps the current text as a starting point.
            editingCustomSeparator = selected == custom;
            if (selected < SeparatorPresets.Length)
            {
                c.BarSeparator = SeparatorPresets[selected].Value;
                changed = true;
            }
        }

        if (selected == custom)
        {
            var text = c.BarSeparator;
            ImGui.SetNextItemWidth(130);
            if (ImGui.InputText("Custom separator", ref text, 16))
            {
                c.BarSeparator = text;
                changed = true;
            }

            Help("Spaces count: \" | \" puts a space on each side. Some characters may not exist in the game font.");
        }

        return changed;
    }

    private void DrawBarPreviewButton()
    {
        var left = plugin.Bar.PreviewLeft;
        if (left > TimeSpan.Zero)
        {
            if (Theme.GhostButton($"Stop the preview ({Math.Ceiling(left.TotalSeconds):0}s)"))
                plugin.Bar.StopPreview();
        }
        else if (Theme.PrimaryButton("Preview the server bar"))
        {
            plugin.Bar.StartPreview();
        }

        Help("Shows made-up values in the server bar for 10 seconds, for every element ticked above, in their order. "
             + "Changes made meanwhile show up right away.");
    }

    private static bool DrawBarLayout(Configuration c)
    {
        var changed = false;
        var layout = c.BarLayout;

        using var table = ImRaii.Table("##barlayout", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
        if (!table)
            return false;

        ImGui.TableSetupColumn("##order", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableSetupColumn("##element", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##example", ImGuiTableColumnFlags.WidthFixed, Theme.Px(170));

        for (var i = 0; i < layout.Count; i++)
        {
            var item = layout[i];
            using var id = ImRaii.PushId(i);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            using (ImRaii.Disabled(i == 0))
            {
                if (ImGui.ArrowButton("##up", ImGuiDir.Up))
                {
                    (layout[i - 1], layout[i]) = (layout[i], layout[i - 1]);
                    changed = true;
                }
            }

            ImGui.SameLine(0, 2);
            using (ImRaii.Disabled(i == layout.Count - 1))
            {
                if (ImGui.ArrowButton("##down", ImGuiDir.Down))
                {
                    (layout[i + 1], layout[i]) = (layout[i], layout[i + 1]);
                    changed = true;
                }
            }

            ImGui.TableNextColumn();
            var (label, example, help) = Describe(item.Element);
            var enabled = item.Enabled;
            if (ImGui.Checkbox(label, ref enabled))
            {
                item.Enabled = enabled;
                changed = true;
            }

            if (help is not null)
                Help(help);

            ImGui.TableNextColumn();
            ImGui.TextDisabled(example);
        }

        return changed;
    }

    private static (string Label, string Example, string? Help) Describe(BarElement element) => element switch
    {
        BarElement.JobIcon => ("Job icon", "(icon)", null),
        BarElement.JobName => ("Job name", "DRK", null),
        BarElement.Rank => ("Rank in the party", "#2", "Personal DPS and rDPS only, when in a party."),
        BarElement.RoleGap => ("Rank in your role and gap", "#1 (+8%)",
                               "Compared with players of your role only (tanks, healers or DPS): with the first one, "
                               + "or with the second one when you are first. Uses rDPS when the metric is rDPS."),
        BarElement.Value => ("Value", "21,234 DPS", null),
        BarElement.Pace => ("Pace vs your best pull", "Best +4%",
                            "Damage done so far compared with your best pull of the same encounter, at the same second."),
        BarElement.BossHp => ("Boss HP", "HP 54%", "HP of the enemy with the most HP in the fight, read from the game."),
        BarElement.Percentile => ("FFLogs percentile (estimate)", "~92",
                                  "Your rDPS placed among FFLogs' rankings for your job on this fight. "
                                  + "Needs FFLogs API credentials (FFLogs tab)."),
        BarElement.Uptime => ("GCD uptime", "GCD 98.7%",
                              "Share of the time an enemy was targetable during which your GCD was rolling or you were casting. "
                              + "Deaths count as lost time. In an ultimate, uptime of the current phase."),
        BarElement.PhaseDps => ("Ultimate phase boss DPS", "Boss 145,000 (+3.6%)",
                                "In an ultimate, DPS on the bosses of the current phase only, and its gap to the DPS needed "
                                + "for the phase enrage (DPS check tab). Green / red against that requirement."),
        BarElement.Phase => ("Ultimate phase", "P1", "Current phase number, in the supported ultimates (DPS check tab)."),
        BarElement.EnrageTimer => ("Enrage timer", "Enrage 3:12",
                                   "Time left before the enrage entered for this fight in the DPS check tab. "
                                   + "Red for the last 30 seconds."),
        _ => (element.ToString(), string.Empty, null),
    };

    private void DrawDpsCheckTab()
    {
        var changed = false;

        Theme.Hint("Min pDPS = boss HP / enrage time, or the value typed by hand when one of them is missing. "
                   + "It colors the pDPS value and the enrage timer.");

        var boss = plugin.Boss.Current;
        var territory = plugin.CurrentTerritory;
        ImGui.TextDisabled(boss is not null
                               ? $"Boss detected: {boss.Name} - {boss.MaxHp:N0} HP"
                               : "Boss HP is read from the game during a fight (enemy with the most HP).");
        ImGui.TextDisabled($"Current zone: {ZoneName(territory)} ({territory})");

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##fightfilter", "Search a fight", ref fightFilter, 64);
        ImGui.Spacing();

        using (var child = ImRaii.Child("##dpschecks", new Vector2(-1, -1)))
        {
            if (child)
            {
                changed |= DrawFightSection(FightCategory.Ultimate, "Ultimate", boss);
                changed |= DrawFightSection(FightCategory.Savage, "Savage", boss);
                changed |= DrawFightSection(FightCategory.Extreme, "Extreme", boss);
                changed |= DrawCustomChecks(boss);
            }
        }

        if (changed)
            plugin.OnConfigChanged();
    }

    /// <summary>One row per fight of the kind, most recent first. A check is only saved once something is entered.</summary>
    private bool DrawFightSection(FightCategory category, string label, BossInfo? boss)
    {
        var c = plugin.Configuration;
        var changed = false;
        var fights = plugin.Fights.Of(category)
                           .Where(f => fightFilter.Length == 0 || f.Name.Contains(fightFilter, StringComparison.OrdinalIgnoreCase))
                           .ToList();

        var flags = category == FightCategory.Ultimate || fightFilter.Length > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (!ImGui.CollapsingHeader($"{label} ({fights.Count})###section{category}", flags))
            return false;

        if (category == FightCategory.Ultimate)
        {
            changed |= Checkbox("Track ultimate phases", c.UltimateMode, v => c.UltimateMode = v);
            Help("In an ultimate the fight never stops between phases. Each supported phase gets its own timer, starting "
                 + "when its boss becomes targetable, and only its own bosses count for the DPS check (adds are ignored). "
                 + "Boss HP, Enrage timer and Ultimate phase boss DPS then follow the current phase. "
                 + "Each phase's enrage is measured from the phase start.");
        }

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp;
        using var table = ImRaii.Table($"##fights{category}", 5, tableFlags);
        if (!table)
            return changed;

        ImGui.TableSetupColumn("Fight", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Boss HP", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableSetupColumn("Enrage (m:s)", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Min pDPS", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("##notes", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        foreach (var fight in fights)
        {
            using var id = ImRaii.PushId((int)fight.TerritoryId);
            ImGui.TableNextRow();
            var here = fight.TerritoryId == plugin.CurrentTerritory;
            var check = FightCheck(fight);

            ImGui.TableNextColumn();
            if (here)
                ImGui.TextColored(c.AboveThresholdColor, fight.Name);
            else
                ImGui.TextUnformatted(fight.Name);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(here ? $"You are here (zone {fight.TerritoryId})." : $"Zone {fight.TerritoryId}");

            ImGui.TableNextColumn();
            if (DrawHpInput(check?.BossHp ?? 0, here ? boss : null, out var hp))
                changed |= EditFightCheck(fight, t => t.BossHp = hp);

            ImGui.TableNextColumn();
            if (DrawEnrageInput(check?.EnrageSeconds ?? 0, out var enrage))
                changed |= EditFightCheck(fight, t => t.EnrageSeconds = enrage);

            ImGui.TableNextColumn();
            if (check is { UsesBossHp: true })
            {
                // Computed from HP and enrage: shown, not editable.
                ImGui.TextUnformatted(check.EffectiveMinPartyDps.ToString("N0"));
            }
            else
            {
                var min = check?.MinPartyDps ?? 0;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputDouble("##minpdps", ref min, 0, 0, "%.0f"))
                    changed |= EditFightCheck(fight, t => t.MinPartyDps = Math.Max(0, min));
            }

            ImGui.TableNextColumn();
            var ultimate = category == FightCategory.Ultimate
                               ? UltimateDefinitions.All.FirstOrDefault(u => u.TerritoryId == fight.TerritoryId)
                               : null;
            if (ultimate is not null)
                ImGui.TextDisabled("phases below");

            if (ultimate is not null)
                DrawPhaseRows(ultimate);
        }

        return changed;
    }

    /// <summary>The phases of a supported ultimate, under its row: each with its own HP, enrage and required DPS.</summary>
    private void DrawPhaseRows(UltimateFight fight)
    {
        var c = plugin.Configuration;
        var tracker = plugin.Ultimates;

        foreach (var phase in fight.Phases)
        {
            using var id = ImRaii.PushId(phase.Key);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.Indent();
            if (phase.IsTransition)
            {
                // A transition has no boss, so no HP, enrage or required DPS.
                ImGui.TextDisabled(phase.Label);
                ImGui.Unindent();
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                ImGui.TextDisabled("transition");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("No boss: lasts until the next phase's bosses are targetable.");
                continue;
            }

            ImGui.TextUnformatted($"{phase.Label} {phase.Name}");
            ImGui.Unindent();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Tracked: {string.Join(", ", phase.TrackedBosses)}");

            ImGui.TableNextColumn();
            DrawPhaseHp(phase);

            ImGui.TableNextColumn();
            var enrage = c.UltimateEnrageSeconds.GetValueOrDefault(phase.Key);
            if (DrawEnrageInput(enrage, out var newEnrage))
                SetPhaseEnrage(phase.Key, newEnrage);

            ImGui.TableNextColumn();
            var phaseHp = c.PhaseCheckHp(phase.Key, 0);
            if (phaseHp > 0 && enrage > 0)
                ImGui.TextUnformatted(NumberFormat.Format(phaseHp / enrage, NumberStyle.Grouped));
            else
                ImGui.TextDisabled("-");

            ImGui.TableNextColumn();
            if (tracker.LastWipes.TryGetValue(phase.Key, out var wipe))
            {
                if (ImGui.SmallButton($"Use last wipe ({NumberFormat.Duration(wipe)})"))
                    SetPhaseEnrage(phase.Key, Math.Round(wipe.TotalSeconds));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Phase time when your last pull ended in this phase, e.g. a wipe to the enrage.");
            }

            if (tracker.LastClears.TryGetValue(phase.Key, out var clear))
            {
                if (tracker.LastWipes.ContainsKey(phase.Key))
                    ImGui.SameLine();
                ImGui.TextDisabled($"last clear {NumberFormat.Duration(clear)}");
            }
        }
    }

    /// <summary>Checks that are not the zone-wide check of a listed fight: other zones, or a single encounter.</summary>
    private bool DrawCustomChecks(BossInfo? boss)
    {
        var c = plugin.Configuration;
        var changed = false;

        if (!ImGui.CollapsingHeader("Other / custom###sectionCustom"))
            return false;

        Theme.Hint("Any other zone, or a single encounter of a zone: an entry with an encounter name only applies to "
                          + "that fight and wins over a zone-wide one. Zone 0 = any zone.");

        var territory = plugin.CurrentTerritory;
        var zoneName = ZoneName(territory);
        var encounter = plugin.Sources.Latest?.Encounter.Title ?? string.Empty;

        using (ImRaii.Disabled(territory == 0))
        {
            if (ImGui.Button("Add current zone"))
            {
                c.Thresholds.Add(new PartyDpsThreshold { TerritoryId = territory, Label = zoneName });
                changed = true;
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(encounter.Length == 0))
        {
            if (ImGui.Button(encounter.Length > 0 ? $"Add last encounter ({encounter})" : "Add last encounter"))
            {
                c.Thresholds.Add(new PartyDpsThreshold
                {
                    TerritoryId = territory, EncounterName = encounter, Label = encounter, BossHp = boss?.MaxHp ?? 0,
                });
                changed = true;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Add empty row"))
        {
            c.Thresholds.Add(new PartyDpsThreshold());
            changed = true;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp;
        using var table = ImRaii.Table("##thresholds", 7, flags);
        if (!table)
            return changed;

        ImGui.TableSetupColumn("Zone ID", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Encounter (optional)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Boss HP", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableSetupColumn("Enrage (m:s)", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Min pDPS", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24);
        ImGui.TableHeadersRow();

        for (var i = 0; i < c.Thresholds.Count; i++)
        {
            var t = c.Thresholds[i];
            if (IsFightCheck(t))
                continue;

            using var id = ImRaii.PushId(i);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var tid = (int)t.TerritoryId;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt("##tid", ref tid, 0))
            {
                t.TerritoryId = (uint)Math.Max(0, tid);
                changed = true;
            }

            ImGui.TableNextColumn();
            var name = t.EncounterName;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##enc", ref name, 128))
            {
                t.EncounterName = name.Trim();
                changed = true;
            }

            ImGui.TableNextColumn();
            var label = t.Label;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##label", ref label, 128))
            {
                t.Label = label;
                changed = true;
            }

            ImGui.TableNextColumn();
            if (DrawHpInput(t.BossHp, boss, out var hp))
            {
                t.BossHp = hp;
                changed = true;
            }

            ImGui.TableNextColumn();
            if (DrawEnrageInput(t.EnrageSeconds, out var enrage))
            {
                t.EnrageSeconds = enrage;
                changed = true;
            }

            ImGui.TableNextColumn();
            if (t.UsesBossHp)
            {
                ImGui.TextUnformatted(t.EffectiveMinPartyDps.ToString("N0"));
            }
            else
            {
                var min = t.MinPartyDps;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputDouble("##minpdps", ref min, 0, 0, "%.0f"))
                {
                    t.MinPartyDps = Math.Max(0, min);
                    changed = true;
                }
            }

            ImGui.TableNextColumn();
            if (ImGui.Button("X"))
            {
                c.Thresholds.RemoveAt(i);
                changed = true;
                break;
            }
        }

        return changed;
    }

    // A listed fight's own check: its whole zone, no encounter name.
    private bool IsFightCheck(PartyDpsThreshold t) => t.EncounterName.Length == 0 && plugin.Fights.Contains(t.TerritoryId);

    private PartyDpsThreshold? FightCheck(FightEntry fight) =>
        plugin.Configuration.Thresholds.FirstOrDefault(t => t.TerritoryId == fight.TerritoryId && t.EncounterName.Length == 0);

    /// <summary>Edits the fight's check, creating it on the first value and dropping it once everything is cleared.</summary>
    private bool EditFightCheck(FightEntry fight, System.Action<PartyDpsThreshold> edit)
    {
        var thresholds = plugin.Configuration.Thresholds;
        var check = FightCheck(fight);
        if (check is null)
        {
            check = new PartyDpsThreshold { TerritoryId = fight.TerritoryId, Label = fight.Name };
            thresholds.Add(check);
        }

        edit(check);
        if (check is { BossHp: 0, EnrageSeconds: 0, MinPartyDps: 0 })
            thresholds.Remove(check);
        return true;
    }

    /// <summary>HP field, with a button taking the max HP of the boss being fought.</summary>
    private static bool DrawHpInput(double value, BossInfo? boss, out double result)
    {
        result = value;
        var changed = false;
        ImGui.SetNextItemWidth(-40);
        if (ImGui.InputDouble("##hp", ref result, 0, 0, "%.0f"))
        {
            result = Math.Max(0, result);
            changed = true;
        }

        ImGui.SameLine(0, 2);
        using (ImRaii.Disabled(boss is null))
        {
            if (ImGui.SmallButton("Get"))
            {
                result = boss!.MaxHp;
                changed = true;
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Use the max HP of the boss of the current fight (only in this fight's zone).\n"
                             + "For a fight with several bosses or HP pools, add them up by hand.");
        }

        return changed;
    }

    /// <summary>Minutes : seconds fields.</summary>
    private static bool DrawEnrageInput(double value, out double result)
    {
        var minutes = (int)(value / 60);
        var seconds = (int)(value % 60);
        ImGui.SetNextItemWidth(40);
        var changed = ImGui.InputInt("##min", ref minutes, 0);
        ImGui.SameLine(0, 2);
        ImGui.TextUnformatted(":");
        ImGui.SameLine(0, 2);
        ImGui.SetNextItemWidth(-1);
        changed |= ImGui.InputInt("##sec", ref seconds, 0);
        result = changed ? Math.Clamp(minutes, 0, 120) * 60 + Math.Clamp(seconds, 0, 59) : value;
        return changed;
    }

    private void DrawChatSummaryTab()
    {
        var c = plugin.Configuration;
        var changed = false;

        Theme.SectionHeader("When");
        changed |= Checkbox("Print a summary in chat after each pull", c.ChatSummary, v => c.ChatSummary = v);
        using (ImRaii.Disabled(!c.ChatSummary))
        {
            ImGui.Indent();
            changed |= Checkbox("Also in dungeons", c.ChatSummaryInDungeons, v => c.ChatSummaryInDungeons = v);
            changed |= Checkbox("Also at the end of each ultimate phase", c.ChatSummaryPhases, v => c.ChatSummaryPhases = v);

            ImGui.Unindent();
            Theme.SectionHeader("Values shown");
            foreach (var (element, label) in SummaryLabels)
            {
                // Same line as the previous checkbox when it fits.
                if (element != SummaryLabels[0].Element)
                {
                    ImGui.SameLine();
                    var width = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;
                    if (ImGui.GetContentRegionAvail().X < width)
                        ImGui.NewLine();
                }
                var on = c.ChatSummaryElements.HasFlag(element);
                if (ImGui.Checkbox($"{label}##summary", ref on))
                {
                    c.ChatSummaryElements = on ? c.ChatSummaryElements | element : c.ChatSummaryElements & ~element;
                    changed = true;
                }
            }

            Theme.SectionHeader("Comparison");
            changed |= Checkbox("Compare DPS, rDPS, GCD and pDPS with your best kill", c.ChatSummaryCompareBest,
                                v => c.ChatSummaryCompareBest = v);
            Help("After each value: NEW BEST with the gain, or the gap to your best kill of the same fight. Wipes are never compared.");
            Theme.Hint("e.g. [Recap] Brute Bomber (4:32) Kill | DRK | DPS 21,234 #2/8 NEW BEST (+2.1%) | rDPS 20,511 #3/8 (best -1.2%) | GCD 98.7%");
        }

        Theme.SectionHeader("Preview");
        if (Theme.PrimaryButton("Preview the chat summary"))
            plugin.Pulls.PrintPreview();
        Help("Prints a summary with made-up values in your chat (only you see it), showing the values ticked above. "
             + "With ultimate phase summaries on, a phase summary too.");

        if (changed)
            plugin.OnConfigChanged();
    }

    private void DrawPullsTab()
    {
        var c = plugin.Configuration;
        var changed = false;

        Theme.SectionHeader("Recording");
        changed |= Checkbox("Only record pulls inside duties", c.RecordOnlyInDuties, v => c.RecordOnlyInDuties = v);

        var min = c.MinPullSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Ignore pulls shorter than (s)", ref min))
        {
            c.MinPullSeconds = Math.Clamp(min, 0, 600);
            changed = true;
        }

        var max = c.MaxHistory;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Pulls kept in history", ref max, 50))
        {
            c.MaxHistory = Math.Clamp(max, 10, 5000);
            changed = true;
        }

        var window = c.GraphWindowSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderInt("Rolling DPS window (s)", ref window, 3, 60))
        {
            c.GraphWindowSeconds = window;
            changed = true;
        }

        Theme.SectionHeader("History");
        Theme.Badge($"{plugin.History.Pulls.Count} pulls", Theme.Gold);
        ImGui.SameLine();
        Theme.Hint("recorded. /dpsbar history to browse them.");

        if (changed)
            plugin.OnConfigChanged();
    }

    /// <summary>
    /// Combined HP of the phase's bosses: detected in game (all of them must die, so they add up), or entered by hand.
    /// </summary>
    private void DrawPhaseHp(UltimatePhase phase)
    {
        var c = plugin.Configuration;
        var detected = c.UltimatePhaseHpDetected.GetValueOrDefault(phase.Key);
        var manual = c.UltimatePhaseHp.GetValueOrDefault(phase.Key);

        ImGui.SetNextItemWidth(-1);
        var hint = detected > 0 ? NumberFormat.Format(detected, NumberStyle.Grouped) : "not seen yet";
        var text = manual > 0 ? manual.ToString("0") : string.Empty;
        if (ImGui.InputTextWithHint("##hp", hint, ref text, 16))
        {
            if (double.TryParse(text.Replace(",", string.Empty).Replace(" ", string.Empty), out var value) && value > 0)
                c.UltimatePhaseHp[phase.Key] = value;
            else
                c.UltimatePhaseHp.Remove(phase.Key);
            plugin.OnConfigChanged();
        }

        if (ImGui.IsItemHovered())
        {
            var bosses = phase.TrackedBosses.Count > 1 ? $" ({string.Join(" + ", phase.TrackedBosses)})" : string.Empty;
            ImGui.SetTooltip($"Combined max HP of the phase's bosses{bosses}.\n"
                             + (detected > 0 ? $"Read in game: {detected:N0}.\n" : "Read automatically the first time the phase is reached.\n")
                             + "Type a value to override it, clear the field to go back to the detected HP.");
        }
    }

    private void SetPhaseEnrage(string key, double seconds)
    {
        var c = plugin.Configuration;
        if (seconds > 0)
            c.UltimateEnrageSeconds[key] = seconds;
        else
            c.UltimateEnrageSeconds.Remove(key);
        plugin.OnConfigChanged();
    }

    private void DrawFflogsTab()
    {
        var c = plugin.Configuration;
        var percentiles = plugin.Percentiles;

        Theme.SectionHeader("Percentile");
        if (Checkbox("Estimate the FFLogs percentile of your rDPS", c.ShowPercentile, v => c.ShowPercentile = v))
            plugin.OnConfigChanged();

        Theme.Hint("Uses the FFLogs API v2. On fflogs.com, open your profile > API Clients (fflogs.com/api/clients), "
                          + "create a client (any name, redirect URL http://localhost) and paste its Client ID and "
                          + "Client Secret below. The old \"V1 Client Key\" does not work with this API.");
        Theme.SectionHeader("API credentials");

        var credentialsChanged = false;
        var clientId = c.FflogsClientId;
        if (ImGui.InputText("Client ID", ref clientId, 128))
        {
            c.FflogsClientId = clientId.Trim();
            credentialsChanged = true;
        }

        var secret = c.FflogsClientSecret;
        if (ImGui.InputText("Client Secret", ref secret, 128, ImGuiInputTextFlags.Password))
        {
            c.FflogsClientSecret = secret.Trim();
            credentialsChanged = true;
        }

        if (credentialsChanged)
        {
            percentiles.Reset();
            plugin.OnConfigChanged();
        }

        Theme.SectionHeader("Current fight");
        var territory = plugin.CurrentTerritory;
        var boss = plugin.Boss.Current;
        ImGui.TextDisabled($"Current fight: {percentiles.Describe(territory, boss?.NameId ?? 0)}");
        if (boss is null)
            ImGui.TextDisabled("(the boss is detected during a fight)");

        var forced = c.FflogsEncounterOverrides.GetValueOrDefault(territory);
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt($"FFLogs encounter id for this zone ({territory})", ref forced, 0))
        {
            if (forced > 0)
                c.FflogsEncounterOverrides[territory] = forced;
            else
                c.FflogsEncounterOverrides.Remove(territory);
            plugin.OnConfigChanged();
        }

        Help("Only needed if the automatic match by boss name fails. The id is in the FFLogs URL of the fight "
             + "(fflogs.com/zone/rankings/...?boss=ID). 0 = automatic.");

        if (percentiles.LastError is { } err)
            ImGui.TextColored(Theme.Bad, err);

        Theme.SectionHeader("How it works");
        Theme.Hint("The estimate compares your rDPS to the best parse of each character for your job "
                          + "(current partition). During the fight it is only a trend: rDPS jumps around burst windows.");
    }

    private void DrawSourceTab()
    {
        var c = plugin.Configuration;

        Theme.SectionHeader("Source");
        var source = (int)c.Source;
        if (ImGui.Combo("Source", ref source, "Auto (ACT if available, else built-in)\0ACT only\0Built-in only\0"))
        {
            c.Source = (CombatSourceKind)source;
            plugin.OnConfigChanged();
        }

        ImGui.TextDisabled($"In use: {plugin.Sources.Current.Name}");
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("ACT / OverlayPlugin", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Theme.Hint("In ACT: Plugins > OverlayPlugin WSServer, then start the server. "
                              + "Default address is 127.0.0.1:10501.");

            ImGui.InputText("Host", ref hostInput, 128);
            ImGui.InputInt("Port", ref portInput);
            portInput = Math.Clamp(portInput, 1, 65535);

            var dirty = hostInput != c.Host || portInput != c.Port;
            using (ImRaii.Disabled(!dirty))
            {
                if (ImGui.Button("Apply"))
                {
                    c.Host = hostInput.Trim();
                    c.Port = portInput;
                    plugin.OnConfigChanged(reconnect: true);
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Reconnect now"))
                plugin.OnConfigChanged(reconnect: true);

            ImGui.SameLine();
            if (ImGui.Button("Reset"))
            {
                hostInput = "127.0.0.1";
                portInput = 10501;
            }

            ImGui.TextDisabled($"Status: {plugin.Sources.Act.StatusText}");
        }

        if (ImGui.CollapsingHeader("Built-in tracker", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Theme.Hint("Reads the damage of your party directly from the game, without ACT. "
                              + "Pets are merged into their owner and the Limit Break is counted separately. "
                              + "DoTs whose owner cannot be determined are ignored, so values can be slightly "
                              + "lower than ACT's.");
            ImGui.TextDisabled($"Status: {plugin.Sources.Native.StatusText}");
        }
    }

    private static void Help(string text) => Theme.HelpMarker(text);

    private static bool Checkbox(string label, bool value, System.Action<bool> set)
    {
        if (!ImGui.Checkbox(label, ref value))
            return false;
        set(value);
        return true;
    }

    private static bool ColorEdit(string label, Vector4 value, System.Action<Vector4> set)
    {
        if (!ImGui.ColorEdit4(label, ref value, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
            return false;
        set(value);
        return true;
    }

    private static string ZoneName(uint territory) =>
        Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var row)
            ? row.PlaceName.Value.Name.ExtractText()
            : "Unknown";
}
