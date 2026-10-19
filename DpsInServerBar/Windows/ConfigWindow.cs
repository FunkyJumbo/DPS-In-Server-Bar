using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Combat;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;

    // Connection fields are edited locally and only applied on "Apply", to avoid reconnecting on every keystroke.
    private string hostInput;
    private int portInput;

    public ConfigWindow(Plugin plugin) : base("DPS In Server Bar - Settings###DpsInServerBarConfig")
    {
        this.plugin = plugin;
        hostInput = plugin.Configuration.Host;
        portInput = plugin.Configuration.Port;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 400) };
        Size = new Vector2(620, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("##tabs");
        if (!tabs)
            return;

        DrawTab("Display", DrawDisplayTab);
        DrawTab("Party checks", DrawThresholdsTab);
        DrawTab("Pulls", DrawPullsTab);
        DrawTab("FFLogs", DrawFflogsTab);
        DrawTab("Data source", DrawSourceTab);
    }

    private static void DrawTab(string label, System.Action draw)
    {
        using var tab = ImRaii.TabItem(label);
        if (tab)
            draw();
    }

    private void DrawDisplayTab()
    {
        var c = plugin.Configuration;
        var changed = false;

        var metric = (int)c.Metric;
        if (ImGui.Combo("Metric", ref metric, "Personal DPS\0Party DPS (pDPS)\0Raid-contributed DPS (rDPS)\0"))
        {
            c.Metric = (DisplayMetric)metric;
            changed = true;
        }

        var style = (int)c.NumberStyle;
        if (ImGui.Combo("Number format", ref style, "12345\012,345\012.3k\0"))
        {
            c.NumberStyle = (NumberStyle)style;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Server bar elements");
        Help("Tick what to show and use the arrows to change the order.");
        changed |= DrawBarLayout(c);
        ImGui.Spacing();

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

        ImGui.Separator();
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

    private static bool DrawBarLayout(Configuration c)
    {
        var changed = false;
        var layout = c.BarLayout;

        using var table = ImRaii.Table("##barlayout", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
        if (!table)
            return false;

        ImGui.TableSetupColumn("##order", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableSetupColumn("##element", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##example", ImGuiTableColumnFlags.WidthFixed, 110);

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
        BarElement.FinalMarker => ("Final value marker", "●", "Shown once the fight is over."),
        BarElement.JobIcon => ("Job icon", "(icon)", null),
        BarElement.JobName => ("Job name", "DRK", null),
        BarElement.Rank => ("Rank in the party", "#2", "Personal DPS and rDPS only, when in a party."),
        BarElement.RoleGap => ("Rank in your role and gap", "#1 (+8%)",
                               "Compared with players of your role only (tanks, healers or DPS): with the first one, "
                               + "or with the second one when you are first. Uses rDPS when the metric is rDPS."),
        BarElement.Value => ("Value", "21,234 DPS", null),
        BarElement.Pace => ("Pace vs your best pull", "+4%",
                            "Damage done so far compared with your best pull of the same encounter, at the same second."),
        BarElement.BossHp => ("Boss HP", "54%", "HP of the enemy with the most HP in the fight, read from the game."),
        BarElement.Percentile => ("FFLogs percentile (estimate)", "~92",
                                  "Your rDPS placed among FFLogs' rankings for your job on this fight. "
                                  + "Needs FFLogs API credentials (FFLogs tab)."),
        BarElement.KillTime => ("Projected kill time", "ETA 9:12",
                                "Boss HP left / current party DPS. Red if after the enrage time set in Party checks."),
        _ => (element.ToString(), string.Empty, null),
    };

    private void DrawThresholdsTab()
    {
        var c = plugin.Configuration;
        var changed = false;

        ImGui.TextWrapped("When the metric is pDPS, the value is colored depending on whether the party is above "
                          + "or below the minimum. With the boss HP and the enrage time, the minimum is HP / enrage; "
                          + "otherwise the manual value is used. An entry with an encounter name only applies to that "
                          + "fight and wins over a zone-wide entry. Zone 0 = any zone.");
        ImGui.Spacing();

        var boss = plugin.Boss.Current;
        if (boss is not null)
            ImGui.TextDisabled($"Boss detected: {boss.Name} - {boss.MaxHp:N0} HP");
        else
            ImGui.TextDisabled("Boss HP is read from the game during a fight (enemy with the most HP).");

        var territory = plugin.CurrentTerritory;
        var zoneName = ZoneName(territory);
        var encounter = plugin.Sources.Latest?.Encounter.Title ?? string.Empty;
        ImGui.TextDisabled($"Current zone: {zoneName} ({territory})");
        if (encounter.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"- last encounter: {encounter}");
        }

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
            if (ImGui.Button("Add last encounter"))
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

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp
                                      | ImGuiTableFlags.ScrollY;
        using (var table = ImRaii.Table("##thresholds", 7, flags, new Vector2(-1, ImGui.GetContentRegionAvail().Y)))
        {
            if (table)
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Zone ID", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("Encounter (optional)", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Boss HP", ImGuiTableColumnFlags.WidthFixed, 150);
                ImGui.TableSetupColumn("Enrage (m:s)", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("Min pDPS", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24);
                ImGui.TableHeadersRow();

                for (var i = 0; i < c.Thresholds.Count; i++)
                {
                    var t = c.Thresholds[i];
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
                    var hp = t.BossHp;
                    ImGui.SetNextItemWidth(-40);
                    if (ImGui.InputDouble("##hp", ref hp, 0, 0, "%.0f"))
                    {
                        t.BossHp = Math.Max(0, hp);
                        changed = true;
                    }

                    ImGui.SameLine(0, 2);
                    using (ImRaii.Disabled(boss is null))
                    {
                        if (ImGui.SmallButton("Get"))
                        {
                            t.BossHp = boss!.MaxHp;
                            changed = true;
                        }
                    }

                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("Use the max HP of the boss of the current fight.\n"
                                         + "For a fight with several bosses or HP pools, add them up by hand.");
                    }

                    ImGui.TableNextColumn();
                    var minutes = (int)(t.EnrageSeconds / 60);
                    var seconds = (int)(t.EnrageSeconds % 60);
                    ImGui.SetNextItemWidth(40);
                    var timeChanged = ImGui.InputInt("##min", ref minutes, 0);
                    ImGui.SameLine(0, 2);
                    ImGui.TextUnformatted(":");
                    ImGui.SameLine(0, 2);
                    ImGui.SetNextItemWidth(-1);
                    timeChanged |= ImGui.InputInt("##sec", ref seconds, 0);
                    if (timeChanged)
                    {
                        t.EnrageSeconds = Math.Clamp(minutes, 0, 120) * 60 + Math.Clamp(seconds, 0, 59);
                        changed = true;
                    }

                    ImGui.TableNextColumn();
                    if (t.UsesBossHp)
                    {
                        // Computed from HP and enrage: shown, not editable.
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
            }
        }

        if (changed)
            plugin.OnConfigChanged();
    }

    private void DrawPullsTab()
    {
        var c = plugin.Configuration;
        var changed = false;

        changed |= Checkbox("Print a summary in chat after each pull", c.ChatSummary, v => c.ChatSummary = v);
        using (ImRaii.Disabled(!c.ChatSummary))
        {
            ImGui.Indent();
            changed |= Checkbox("Also in dungeons", c.ChatSummaryInDungeons, v => c.ChatSummaryInDungeons = v);
            ImGui.Unindent();
        }

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

        ImGui.Spacing();
        ImGui.TextDisabled($"{plugin.History.Pulls.Count} pull(s) recorded. /dpsbar history to browse them.");

        if (changed)
            plugin.OnConfigChanged();
    }

    private void DrawFflogsTab()
    {
        var c = plugin.Configuration;
        var percentiles = plugin.Percentiles;

        if (Checkbox("Estimate the FFLogs percentile of your rDPS", c.ShowPercentile, v => c.ShowPercentile = v))
            plugin.OnConfigChanged();

        ImGui.TextWrapped("Uses the FFLogs API v2. On fflogs.com, open your profile > API Clients (fflogs.com/api/clients), "
                          + "create a client (any name, redirect URL http://localhost) and paste its Client ID and "
                          + "Client Secret below. The old \"V1 Client Key\" does not work with this API.");
        ImGui.Spacing();

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

        ImGui.Spacing();
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
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), err);

        ImGui.Spacing();
        ImGui.TextWrapped("The estimate compares your rDPS to the best parse of each character for your job "
                          + "(current partition). During the fight it is only a trend: rDPS jumps around burst windows.");
    }

    private void DrawSourceTab()
    {
        var c = plugin.Configuration;

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
            ImGui.TextWrapped("In ACT: Plugins > OverlayPlugin WSServer, then start the server. "
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
            ImGui.TextWrapped("Reads the damage of your party directly from the game, without ACT. "
                              + "Pets are merged into their owner and the Limit Break is counted separately. "
                              + "DoTs whose owner cannot be determined are ignored, so values can be slightly "
                              + "lower than ACT's.");
            ImGui.TextDisabled($"Status: {plugin.Sources.Native.StatusText}");
        }
    }

    private static void Help(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

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
