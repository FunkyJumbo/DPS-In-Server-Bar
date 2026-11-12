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
        : base(Loc.T("DPS meter###DpsInServerBarCompact"),
               ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
               | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize)
    {
        Loc.Changed += () => WindowName = Loc.T("DPS meter###DpsInServerBarCompact");
        this.plugin = plugin;
        AllowBackgroundBlur = false;
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
        if (snapshot is not null)
            DrawTable(snapshot, config);
        else
            Theme.Hint(Loc.T("No combat data yet."));
    }

    /// <summary>Right click menu: metric, opacity, main window, history, settings, close.</summary>
    private void DrawMenu(Configuration config)
    {
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##compactmenu");

        using var popup = ImRaii.Popup("##compactmenu");
        if (!popup)
            return;

        if (ImGui.MenuItem(Loc.T("DPS"), string.Empty, !config.SortByRdps))
        {
            config.SortByRdps = false;
            config.Save();
        }

        if (ImGui.MenuItem(Loc.T("rDPS"), string.Empty, config.SortByRdps))
        {
            config.SortByRdps = true;
            config.Save();
        }

        if (ImGui.MenuItem(Loc.T("Header"), string.Empty, config.CompactShowHeader))
        {
            config.CompactShowHeader = !config.CompactShowHeader;
            config.Save();
        }

        var percent = (int)Math.Round(config.CompactOpacity * 100);
        ImGui.SetNextItemWidth(Theme.Px(140));
        if (ImGui.SliderInt(Loc.T("Opacity"), ref percent, 0, 100, "%d%%"))
            config.CompactOpacity = percent / 100f;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();

        ImGui.Separator();
        if (ImGui.MenuItem(Loc.T("Main window")))
            plugin.ShowMainWindow();
        if (ImGui.MenuItem(Loc.T("History")))
            plugin.ShowMainWindow(history: true);
        if (ImGui.MenuItem(Loc.T("Settings")))
            plugin.ToggleConfigUi();
        if (ImGui.MenuItem(Loc.T("Close")))
            plugin.SetCompact(false);
    }

    /// <summary>Draws the meter.</summary>
    private static void DrawTable(CombatSnapshot snapshot, Configuration config)
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
            extras.Add((Loc.T("Crit"), "100%", c => ($"{c.CritPercent:0}%", Theme.TextMuted)));
        if (shown.HasFlag(CompactColumn.DirectHit))
            extras.Add((Loc.T("DH"), "100%", c => ($"{c.DirectHitPercent:0}%", Theme.TextMuted)));
        if (shown.HasFlag(CompactColumn.Deaths))
            extras.Add((Loc.T("Deaths"), Loc.T("Deaths"), c => (c.Deaths.ToString(), c.Deaths > 0 ? Theme.Bad : Theme.TextFaint)));

        // Fixed column widths, sized for the widest possible value, so the window does not twitch as numbers change.
        // The name column leaves a little room for the bar past the longest name.
        var nameWidth = Math.Max(ImGui.CalcTextSize(Loc.T("Player")).X,
                                 rows.Count > 0 ? rows.Max(c => ImGui.CalcTextSize(Names.Show(c.Name)).X) + iconSize + Theme.Px(13) : 0);
        var valueWidth = ImGui.CalcTextSize("000,000").X;
        var widths = extras.Select(e => Math.Max(ImGui.CalcTextSize(e.Widest).X, ImGui.CalcTextSize(e.Header).X)).ToList();
        var columns = 2 + extras.Count;

        // Header background with rounded top corners, following the window's corners (the table's own is square).
        var cellPadding = ImGui.GetStyle().CellPadding;
        var headerHeight = ImGui.GetTextLineHeight() + cellPadding.Y * 2;
        var tableWidth = nameWidth + valueWidth + widths.Sum() + cellPadding.X * 2 * columns;
        var origin = ImGui.GetCursorScreenPos();
        if (config.CompactShowHeader)
            ImGui.GetWindowDrawList().AddRectFilled(origin, origin + new Vector2(tableWidth, headerHeight),
                                                Theme.U32(Theme.Gold, 0.45f), Theme.Px(6), ImDrawFlags.RoundCornersTop);

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.PadOuterX;
        using var table = ImRaii.Table("##compactmeter", columns, flags, new Vector2(tableWidth, 0));
        if (!table)
            return;

        ImGui.TableSetupColumn(Loc.T("Player"), ImGuiTableColumnFlags.WidthFixed, nameWidth);
        ImGui.TableSetupColumn(byRdps ? Loc.T("rDPS") : Loc.T("DPS"), ImGuiTableColumnFlags.WidthFixed, valueWidth);
        for (var i = 0; i < extras.Count; i++)
            ImGui.TableSetupColumn(extras[i].Header, ImGuiTableColumnFlags.WidthFixed, widths[i]);

        // Headers drawn one by one so the metric header can be clicked.
        var clear = Vector4.Zero;
        if (config.CompactShowHeader)
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
                    ImGui.SetTooltip(Loc.T("Click: switch DPS / rDPS\nRight click anywhere: menu"));
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

            // The bar spans the name and the value columns: drawn in two parts, one per cell, split on the column edge.
            var start = ImGui.GetCursorScreenPos();
            var span = nameWidth + cellPadding.X * 2 + valueWidth;
            var endX = start.X + (top > 0 ? span * (float)Math.Clamp(value / top, 0, 1) : 0);
            var splitX = start.X + nameWidth + cellPadding.X;
            var barColor = Theme.U32(JobInfo.RoleColor(c.Job), c.IsYou ? 0.34f : 0.2f);
            var drawList = ImGui.GetWindowDrawList();
            if (endX > start.X)
                drawList.AddRectFilled(start, new Vector2(Math.Min(endX, splitX), start.Y + iconSize), barColor, Theme.Px(3),
                                       endX > splitX ? ImDrawFlags.RoundCornersLeft : ImDrawFlags.RoundCornersAll);

            JobInfo.DrawIcon(c.Job, iconSize);
            ImGui.SameLine(0, Theme.Px(5));
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(color, Names.Show(c.Name));

            ImGui.TableNextColumn();
            if (endX > splitX)
                drawList.AddRectFilled(new Vector2(splitX, start.Y), new Vector2(endX, start.Y + iconSize), barColor, Theme.Px(3),
                                       ImDrawFlags.RoundCornersRight);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(color, byRdps && !c.HasRdps ? "-" : NumberFormat.Format(value, NumberStyle.Grouped));

            foreach (var extra in extras)
            {
                var (text, textColor) = extra.Cell(c);
                Cell(text, textColor);
            }
        }
    }
}
