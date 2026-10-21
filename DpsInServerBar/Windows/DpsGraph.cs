using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImPlot;
using DpsInServerBar.Tracking;

namespace DpsInServerBar.Windows;

/// <summary>Draws DPS curves (one point per second) with ImPlot, plus the settings shared by every graph.</summary>
public sealed class DpsGraph
{
    public readonly record struct Series(string Label, float[] Values);

    // Window of the uptime curve, in seconds: short enough to see when uptime was lost.
    private const int UptimeWindow = 3;

    public bool ShowUptime { get; set; }
    public bool ShowParty { get; set; }
    public bool ShowRolling { get; set; } = true;
    public bool ShowAverage { get; set; } = true;

    public void DrawOptions(int window)
    {
        var kind = ShowUptime ? 1 : 0;
        ImGui.SetNextItemWidth(90);
        if (ImGui.Combo("##graphkind", ref kind, "DPS\0Uptime\0"))
            ShowUptime = kind == 1;

        if (ShowUptime)
            return;

        ImGui.SameLine();
        var party = ShowParty ? 1 : 0;
        ImGui.SetNextItemWidth(110);
        if (ImGui.Combo("##graphmetric", ref party, "Personal\0Party\0"))
            ShowParty = party == 1;

        ImGui.SameLine();
        var rolling = ShowRolling;
        if (ImGui.Checkbox($"Rolling ({window}s)", ref rolling))
            ShowRolling = rolling;

        ImGui.SameLine();
        var average = ShowAverage;
        if (ImGui.Checkbox("Encounter average", ref average))
            ShowAverage = average;
    }

    /// <summary>Adds the curves of one cumulative-damage timeline according to the current options.</summary>
    public void AddCurves(List<Series> series, string prefix, double[] cumulative, int window)
    {
        if (cumulative.Length < 2)
            return;
        if (ShowRolling)
            series.Add(new Series($"{prefix} rolling", Timeline.Rolling(cumulative, window)));
        if (ShowAverage)
            series.Add(new Series($"{prefix} average", Timeline.Average(cumulative)));
    }

    /// <summary>
    /// GCD uptime over time: one curve per pull (gaps where nothing was targetable), plus the downtime of the first
    /// pull shaded, so that drops outside the shaded areas are real uptime losses.
    /// </summary>
    public static void DrawUptime(
        string id, IReadOnlyList<(string Label, float[] Available, float[] Active)> pulls, float height)
    {
        if (pulls.Count == 0 || pulls[0].Available.Length < 2)
        {
            ImGui.TextDisabled("No uptime data for this fight.");
            return;
        }

        if (!ImPlot.BeginPlot(id, new Vector2(-1, height), ImPlotFlags.NoMenus | ImPlotFlags.NoBoxSelect))
            return;

        ImPlot.SetupAxes("Time (s)", "Uptime %", ImPlotAxisFlags.AutoFit, ImPlotAxisFlags.None);
        ImPlot.SetupAxisLimits(ImAxis.Y1, 0, 105, ImPlotCond.Always);

        var downtime = Timeline.Downtime(pulls[0].Available);
        ImPlot.PlotShaded("No target", ref downtime[0], downtime.Length, 0, 1, 0, ImPlotShadedFlags.None, 0, sizeof(float));

        foreach (var (label, available, active) in pulls)
        {
            var curve = Timeline.Uptime(available, active, UptimeWindow);
            if (curve.Length > 0)
                ImPlot.PlotLine(label, ref curve[0], curve.Length, 1, 0, ImPlotLineFlags.SkipNaN, 0, sizeof(float));
        }

        ImPlot.EndPlot();
    }

    public static void Draw(string id, IReadOnlyList<Series> series, float height, double? threshold = null)
    {
        if (!ImPlot.BeginPlot(id, new Vector2(-1, height), ImPlotFlags.NoMenus | ImPlotFlags.NoBoxSelect))
            return;

        ImPlot.SetupAxes("Time (s)", "DPS", ImPlotAxisFlags.AutoFit, ImPlotAxisFlags.AutoFit);

        var longest = 0;
        foreach (var s in series)
        {
            if (s.Values.Length == 0)
                continue;
            longest = System.Math.Max(longest, s.Values.Length);
            ImPlot.PlotLine(s.Label, ref s.Values[0], s.Values.Length, 1, 0, ImPlotLineFlags.None, 0, sizeof(float));
        }

        if (threshold is { } t && longest > 1)
        {
            var line = new[] { (float)t, (float)t };
            ImPlot.PlotLine("Check", ref line[0], 2, longest - 1, 0, ImPlotLineFlags.None, 0, sizeof(float));
        }

        ImPlot.EndPlot();
    }
}
