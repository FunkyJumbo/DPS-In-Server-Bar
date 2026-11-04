using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Combat;
using DpsInServerBar.Display;
using DpsInServerBar.UI;

namespace DpsInServerBar.Windows;

/// <summary>
/// Small pop-up meter without a title bar: one line per player with DPS or rDPS, share and deaths.
/// Dragged by its background; clicking the metric header switches DPS / rDPS, right click opens a menu.
/// </summary>
public sealed class CompactWindow : Window
{
    private readonly Plugin plugin;
    private IDisposable? themeScope;
    private IDisposable? styleScope;

    public CompactWindow(Plugin plugin)
        : base("DPS meter###DpsInServerBarCompact",
               ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
               | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        BgAlpha = plugin.Configuration.CompactOpacity;

        themeScope = Theme.Push();
        styleScope = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, Theme.Px(10))
                          .Push(ImGuiStyleVar.WindowPadding, Theme.Px(new Vector2(8, 7)));
    }

    public override void PostDraw()
    {
        styleScope?.Dispose();
        styleScope = null;
        themeScope?.Dispose();
        themeScope = null;
    }

    public override void Draw()
    {
        var config = plugin.Configuration;
        DrawMenu(config);

        // The window takes the size of its content: it cannot be resized by hand.
        var snapshot = plugin.Sources.Latest;
        var tableWidth = 0f;
        if (snapshot is not null)
            tableWidth = DrawTable(snapshot, config);
        else
            Theme.Hint("No combat data yet.");

        if (Theme.Chip("Full view", false, transparent: config.CompactOpacity < 0.5f))
            plugin.ShowFullView();

        // Fight timer at the right, when there is room for it.
        if (snapshot is not null)
        {
            var time = NumberFormat.Duration(snapshot.Encounter.Duration);
            // Aligned on the table, not on the window, so the window can shrink to the table.
            var x = ImGui.GetCursorStartPos().X + tableWidth - ImGui.CalcTextSize(time).X;
            ImGui.SameLine();
            if (x > ImGui.GetCursorPosX())
            {
                ImGui.SetCursorPosX(x);
                ImGui.TextColored(snapshot.IsActive ? Theme.Crystal : Theme.TextMuted, time);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(string.IsNullOrEmpty(snapshot.Encounter.Title) ? "Encounter" : snapshot.Encounter.Title);
            }
            else
            {
                ImGui.NewLine();
            }
        }
    }

    /// <summary>Right click menu: metric, full window, history, settings, close.</summary>
    private void DrawMenu(Configuration config)
    {
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##compactmenu");

        using var popup = ImRaii.Popup("##compactmenu");
        if (!popup)
            return;

        if (ImGui.MenuItem("DPS", string.Empty, !config.SortByRdps))
        {
            config.SortByRdps = false;
            config.Save();
        }

        if (ImGui.MenuItem("rDPS", string.Empty, config.SortByRdps))
        {
            config.SortByRdps = true;
            config.Save();
        }

        var percent = (int)Math.Round(config.CompactOpacity * 100);
        ImGui.SetNextItemWidth(Theme.Px(140));
        if (ImGui.SliderInt("Opacity", ref percent, 0, 100, "%d%%"))
            config.CompactOpacity = percent / 100f;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();

        ImGui.Separator();
        if (ImGui.MenuItem("Full view"))
            plugin.ShowFullView();
        if (ImGui.MenuItem("History"))
            plugin.ShowFullView(history: true);
        if (ImGui.MenuItem("Settings"))
            plugin.ToggleConfigUi();
        if (ImGui.MenuItem("Close"))
            plugin.SetCompact(false);
    }

    /// <summary>Draws the meter and returns its width.</summary>
    private static float DrawTable(CombatSnapshot snapshot, Configuration config)
    {
        var byRdps = config.SortByRdps && snapshot.Combatants.Any(c => c.HasRdps);
        double Value(CombatantInfo c) => byRdps && c.HasRdps ? c.RDps : c.EncDps;

        var rows = snapshot.Combatants.OrderByDescending(Value).ToList();
        var top = rows.Count > 0 ? rows.Max(Value) : 0;
        var total = rows.Sum(c => byRdps ? c.HasRdps ? c.RDps : 0 : c.EncDps);

        var iconSize = ImGui.GetTextLineHeight() + Theme.Px(2);

        // Optional columns, chosen in the settings (Display > Compact meter).
        var extras = new List<(string Header, string Widest, Func<CombatantInfo, (string Text, Vector4 Color)> Cell)>();
        var shown = config.CompactColumns;
        if (shown.HasFlag(CompactColumn.Share))
            extras.Add(("%", "100.0%", c => (total > 0 && (!byRdps || c.HasRdps) ? $"{Value(c) / total:0.0%}" : "-", Theme.TextMuted)));
        if (shown.HasFlag(CompactColumn.Crit))
            extras.Add(("Crit", "100%", c => ($"{c.CritPercent:0}%", Theme.TextMuted)));
        if (shown.HasFlag(CompactColumn.DirectHit))
            extras.Add(("DH", "100%", c => ($"{c.DirectHitPercent:0}%", Theme.TextMuted)));
        if (shown.HasFlag(CompactColumn.Deaths))
            extras.Add(("Deaths", "Deaths", c => (c.Deaths.ToString(), c.Deaths > 0 ? Theme.Bad : Theme.TextFaint)));

        // Fixed column widths, sized for the widest possible value, so the window does not twitch as numbers change.
        // The name column leaves a little room for the bar past the longest name.
        var nameWidth = Math.Max(ImGui.CalcTextSize("Player").X,
                                 rows.Count > 0 ? rows.Max(c => ImGui.CalcTextSize(c.Name).X) + iconSize + Theme.Px(13) : 0);
        var valueWidth = ImGui.CalcTextSize("000,000").X;
        var widths = extras.Select(e => Math.Max(ImGui.CalcTextSize(e.Widest).X, ImGui.CalcTextSize(e.Header).X)).ToList();
        var columns = 2 + extras.Count;

        // Header background with rounded top corners, following the window's corners (the table's own is square).
        var cellPadding = ImGui.GetStyle().CellPadding;
        var headerHeight = ImGui.GetTextLineHeight() + cellPadding.Y * 2;
        var tableWidth = nameWidth + valueWidth + widths.Sum() + cellPadding.X * 2 * columns;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(origin, origin + new Vector2(tableWidth, headerHeight),
                                                Theme.U32(Theme.Gold, 0.45f), Theme.Px(6), ImDrawFlags.RoundCornersTop);

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.PadOuterX;
        using var table = ImRaii.Table("##compactmeter", columns, flags, new Vector2(tableWidth, 0));
        if (!table)
            return tableWidth;

        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, nameWidth);
        ImGui.TableSetupColumn(byRdps ? "rDPS" : "DPS", ImGuiTableColumnFlags.WidthFixed, valueWidth);
        for (var i = 0; i < extras.Count; i++)
            ImGui.TableSetupColumn(extras[i].Header, ImGuiTableColumnFlags.WidthFixed, widths[i]);

        // Headers drawn one by one so the metric header can be clicked.
        var clear = Vector4.Zero;
        using (ImRaii.PushColor(ImGuiCol.TableHeaderBg, clear).Push(ImGuiCol.HeaderHovered, clear)
                     .Push(ImGuiCol.HeaderActive, clear).Push(ImGuiCol.Text, Vector4.One))
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            for (var i = 0; i < columns; i++)
            {
                ImGui.TableSetColumnIndex(i);
                ImGui.TableHeader(ImGui.TableGetColumnName(i));
                if (i != 1)
                    continue;

                if (ImGui.IsItemClicked())
                {
                    config.SortByRdps = !config.SortByRdps;
                    config.Save();
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Click: switch DPS / rDPS\nRight click anywhere: menu");
            }
        }

        foreach (var c in rows)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, iconSize + Theme.Px(2));
            var color = c.IsYou ? Theme.GoldBright : Theme.Text;
            void Cell(string text, Vector4 textColor)
            {
                ImGui.TableNextColumn();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2);
                ImGui.TextColored(textColor, text);
            }

            ImGui.TableNextColumn();
            var value = Value(c);
            var start = ImGui.GetCursorScreenPos();
            if (top > 0)
            {
                var end = start + new Vector2(ImGui.GetContentRegionAvail().X * (float)Math.Clamp(value / top, 0, 1), iconSize);
                ImGui.GetWindowDrawList().AddRectFilled(start, end, Theme.U32(JobInfo.RoleColor(c.Job), c.IsYou ? 0.34f : 0.2f), Theme.Px(3));
            }

            JobInfo.DrawIcon(c.Job, iconSize);
            ImGui.SameLine(0, Theme.Px(5));
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(color, c.Name);

            Cell(byRdps && !c.HasRdps ? "-" : NumberFormat.Format(value, NumberStyle.Grouped), color);
            foreach (var extra in extras)
            {
                var (text, textColor) = extra.Cell(c);
                Cell(text, textColor);
            }
        }

        return tableWidth;
    }
}
