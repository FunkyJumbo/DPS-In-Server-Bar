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

    public bool ShowParty { get; set; }
    public bool ShowRolling { get; set; } = true;
    public bool ShowAverage { get; set; } = true;

    public void DrawOptions(int window)
    {
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
