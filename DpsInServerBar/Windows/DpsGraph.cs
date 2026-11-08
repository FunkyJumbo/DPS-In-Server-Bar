using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImPlot;
using DpsInServerBar.Display;
using DpsInServerBar.Tracking;
using DpsInServerBar.UI;

namespace DpsInServerBar.Windows;

/// <summary>Draws DPS curves (one point per second) with ImPlot, plus the settings shared by every graph.</summary>
public sealed class DpsGraph
{
    public readonly record struct Series(string Label, float[] Values);

    // Below this the graph is unreadable: the window scrolls instead.
    public const float MinHeight = 150;

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
        string id, IReadOnlyList<(string Label, float[] Available, float[] Active)> pulls, float height,
        IReadOnlyList<GcdGap>? gaps = null, IReadOnlyList<PartyDeath>? deaths = null)
    {
        if (pulls.Count == 0 || pulls[0].Available.Length < 2)
        {
            ImGui.TextDisabled("No uptime data for this fight.");
            return;
        }

        using var style = PushStyle();
        if (!ImPlot.BeginPlot(id, new Vector2(-1, Math.Max(height, MinHeight)), ImPlotFlags.NoMenus | ImPlotFlags.NoBoxSelect))
            return;

        ImPlot.SetupAxes("Time (s)", "Uptime %", ImPlotAxisFlags.AutoFit, ImPlotAxisFlags.None);
        ImPlot.SetupAxisLimits(ImAxis.Y1, 0, 105, ImPlotCond.Always);

        var downtime = Timeline.Downtime(pulls[0].Available);
        ImPlot.SetNextFillStyle(Theme.Alpha(Theme.TextFaint, 0.35f));
        ImPlot.PlotShaded("No target", ref downtime[0], downtime.Length, 0, 1, 0, ImPlotShadedFlags.None, 0, sizeof(float));

        var index = 0;
        foreach (var (label, available, active) in pulls)
        {
            ImPlot.SetNextLineStyle(Theme.Series[index++ % Theme.Series.Length], 2f);
            var curve = Timeline.Uptime(available, active, UptimeWindow);
            if (curve.Length > 0)
                ImPlot.PlotLine(label, ref curve[0], curve.Length, 1, 0, ImPlotLineFlags.SkipNaN, 0, sizeof(float));
        }

        if (gaps is { Count: > 0 })
            DrawGaps(gaps);

        DrawDeaths(deaths);

        ImPlot.EndPlot();
    }

    // Height of the GCD gap marks, near the bottom of the uptime graph.
    private const float GapLevel = 4;

    /// <summary>
    /// The GCD gaps of the first pull, as thick marks at the bottom of the uptime graph (one legend entry per cause,
    /// so each can be hidden), with the gap under the mouse in a tooltip.
    /// </summary>
    private static void DrawGaps(IReadOnlyList<GcdGap> gaps)
    {
        foreach (var (kind, label, color) in new[]
                 {
                     (GcdGapKind.NoGcd, "No GCD", Theme.Bad),
                     (GcdGapKind.Clipping, "Clipping", Theme.Warn),
                     (GcdGapKind.Late, "Late", Theme.TextMuted),
                 })
        {
            var ofKind = gaps.Where(g => g.Kind == kind).ToList();
            if (ofKind.Count == 0)
                continue;

            // Each gap is a segment from its start to its end; a short gap still gets a visible width.
            var xs = new float[ofKind.Count * 2];
            var ys = new float[ofKind.Count * 2];
            for (var i = 0; i < ofKind.Count; i++)
            {
                xs[i * 2] = (float)ofKind[i].AtSeconds;
                xs[i * 2 + 1] = (float)(ofKind[i].AtSeconds + Math.Max(ofKind[i].Seconds, 0.6));
                ys[i * 2] = ys[i * 2 + 1] = GapLevel;
            }

            ImPlot.SetNextLineStyle(color, 7f);
            ImPlot.PlotLine(label, ref xs[0], ref ys[0], xs.Length, ImPlotLineFlags.Segments, 0, sizeof(float));
        }

        if (!ImPlot.IsPlotHovered())
            return;

        var mouse = ImPlot.GetPlotMousePos();
        if (mouse.Y > 15)
            return;

        var hovered = gaps.Where(g => mouse.X >= g.AtSeconds - 1 && mouse.X <= g.AtSeconds + Math.Max(g.Seconds, 0.6) + 1)
                          .OrderByDescending(g => g.Seconds)
                          .FirstOrDefault();
        if (hovered is not null)
            ImGui.SetTooltip($"{NumberFormat.Duration(TimeSpan.FromSeconds(hovered.AtSeconds))}  {GapLabel(hovered.Kind)} {hovered.Seconds:0.0}s");
    }

    /// <summary>A vertical red line at each death (one legend entry, so they can be hidden), named in a tooltip on hover.</summary>
    private static void DrawDeaths(IReadOnlyList<PartyDeath>? deaths)
    {
        if (deaths is not { Count: > 0 })
            return;

        var xs = deaths.Select(d => (float)d.AtSeconds).ToArray();
        ImPlot.SetNextLineStyle(Theme.Alpha(Theme.Bad, 0.8f), 1.5f);
        ImPlot.PlotInfLines("Deaths", ref xs[0], xs.Length, ImPlotInfLinesFlags.None, 0, sizeof(float));

        if (!ImPlot.IsPlotHovered())
            return;

        var mouse = ImPlot.GetPlotMousePos();
        var near = deaths.Where(d => Math.Abs(d.AtSeconds - mouse.X) <= 1.5).ToList();
        if (near.Count > 0)
            ImGui.SetTooltip(string.Join("\n", near.Select(d => $"{NumberFormat.Duration(TimeSpan.FromSeconds(d.AtSeconds))}  {d.Player} died: {d.Cause}")));
    }

    /// <summary>
    /// How far each pull got (one point per pull, kills in green) and the best so far, against the pull number or the
    /// time spent in the instance; in an ultimate, one band per phase with its name. Returns the pull clicked, if any.
    /// </summary>
    public static int? DrawProgression(
        string id, IReadOnlyList<double> xs, string xLabel, IReadOnlyList<double?> scores, IReadOnlyList<bool> kills,
        double max, IReadOnlyList<string>? phases, float height, Func<int, string> describe)
    {
        if (scores.Count == 0)
            return null;

        using var style = PushStyle();
        if (!ImPlot.BeginPlot(id, new Vector2(-1, Math.Max(height, MinHeight)), ImPlotFlags.NoMenus | ImPlotFlags.NoBoxSelect | ImPlotFlags.NoMouseText))
            return null;

        var ultimate = phases is { Count: > 0 };
        ImPlot.SetupAxes(xLabel, ultimate ? "Phase (boss HP taken)" : "Boss HP taken %", ImPlotAxisFlags.None,
                         ImPlotAxisFlags.NoLabel | (ultimate ? ImPlotAxisFlags.NoTickLabels : ImPlotAxisFlags.None));
        var xMin = 0.0; // the graph starts from nothing done, at 0
        var xMax = Math.Max(xs.Max(), xMin + 1);
        var margin = (xMax - xMin) * 0.02 + (xLabel == "Pull" ? 0.3 : 0);
        ImPlot.SetupAxisLimits(ImAxis.X1, xMin, xMax + margin, ImPlotCond.Always);
        ImPlot.SetupAxisLimits(ImAxis.Y1, 0, max * 1.05, ImPlotCond.Always);

        // Ultimate: a line between phases, each phase named in its band.
        if (ultimate)
        {
            var bounds = Enumerable.Range(1, phases!.Count - 1).Select(i => (float)i).ToArray();
            if (bounds.Length > 0)
            {
                ImPlot.SetNextLineStyle(Theme.Alpha(Theme.Line, 0.9f), 1f);
                ImPlot.PlotInfLines("##phases", ref bounds[0], bounds.Length, ImPlotInfLinesFlags.Horizontal, 0, sizeof(float));
            }

            ImPlot.PushStyleColor(ImPlotCol.InlayText, Theme.TextMuted);
                for (var i = 0; i < phases.Count; i++)
                    ImPlot.PlotText(phases[i], xMin, i + 0.5, new Vector2(ImGui.CalcTextSize(phases[i]).X / 2 + 6, 0));
            ImPlot.PopStyleColor();
        }

        var x = xs.Select(v => (float)v).ToArray();
        var ys = scores.Select(s => s is { } v ? (float)v : float.NaN).ToArray();
        var bestSoFar = new float[ys.Length];
        var best = float.NaN;
        for (var i = 0; i < ys.Length; i++)
        {
            if (!float.IsNaN(ys[i]) && (float.IsNaN(best) || ys[i] > best))
                best = ys[i];
            bestSoFar[i] = best;
        }

        // The first pull starts from 0 (no marker there): a segment from the origin to it.
        var first = Array.FindIndex(ys, y => !float.IsNaN(y));
        if (first >= 0)
        {
            var startX = new[] { 0f, x[first] };
            var startY = new[] { 0f, ys[first] };
            ImPlot.SetNextLineStyle(Theme.Series[0], 2f);
            ImPlot.PlotLine("Pull", ref startX[0], ref startY[0], 2, ImPlotLineFlags.None, 0, sizeof(float));
        }

        ImPlot.SetNextLineStyle(Theme.Alpha(Theme.Good, 0.7f), 1.5f);
        ImPlot.PlotLine("Best so far", ref x[0], ref bestSoFar[0], x.Length, ImPlotLineFlags.SkipNaN, 0, sizeof(float));

        ImPlot.SetNextLineStyle(Theme.Series[0], 2f);
        ImPlot.SetNextMarkerStyle(ImPlotMarker.Circle, 4f);
        ImPlot.PlotLine("Pull", ref x[0], ref ys[0], x.Length, ImPlotLineFlags.SkipNaN, 0, sizeof(float));

        var killXs = x.Where((_, i) => kills[i] && !float.IsNaN(ys[i])).ToArray();
        var killYs = ys.Where((y, i) => kills[i] && !float.IsNaN(y)).ToArray();
        if (killXs.Length > 0)
        {
            ImPlot.SetNextMarkerStyle(ImPlotMarker.Diamond, 7f, Theme.Good, 1f, Theme.Good);
            ImPlot.PlotScatter("Kill", ref killXs[0], ref killYs[0], killXs.Length, ImPlotScatterFlags.None, 0, sizeof(float));
        }

        int? clicked = null;
        if (ImPlot.IsPlotHovered())
        {
            // The pull nearest to the mouse, within a few pixels.
            var mouse = ImPlot.GetPlotMousePos().X;
            var index = Enumerable.Range(0, x.Length).MinBy(i => Math.Abs(x[i] - mouse));
            var perPixel = (xMax - xMin + margin) / Math.Max(ImPlot.GetPlotSize().X, 1);
            if (Math.Abs(x[index] - mouse) <= Math.Max(perPixel * 12, xLabel == "Pull" ? 0.5 : 0))
            {
                ImGui.SetTooltip(describe(index));
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    clicked = index;
            }
        }

        ImPlot.EndPlot();
        return clicked;
    }

    public static string GapLabel(GcdGapKind kind) => kind switch
    {
        GcdGapKind.Clipping => "clipping",
        GcdGapKind.Late => "late",
        _ => "no GCD",
    };

    /// <summary>Plot colors matching the theme: dark frame, muted grid and text.</summary>
    private static IDisposable PushStyle()
    {
        ImPlot.PushStyleColor(ImPlotCol.FrameBg, Theme.Alpha(Theme.Panel, 0.8f));
        ImPlot.PushStyleColor(ImPlotCol.LegendBg, Theme.Alpha(Theme.Panel, 0.9f));
        ImPlot.PushStyleColor(ImPlotCol.LegendBorder, Theme.Line);
        ImPlot.PushStyleColor(ImPlotCol.AxisGrid, Theme.Alpha(Theme.Line, 0.8f));
        ImPlot.PushStyleColor(ImPlotCol.AxisText, Theme.TextMuted);
        return new PopStyle(5);
    }

    private sealed class PopStyle(int count) : IDisposable
    {
        public void Dispose() => ImPlot.PopStyleColor(count);
    }

    public static void Draw(
        string id, IReadOnlyList<Series> series, float height, double? threshold = null, IReadOnlyList<PartyDeath>? deaths = null)
    {
        using var style = PushStyle();
        if (!ImPlot.BeginPlot(id, new Vector2(-1, Math.Max(height, MinHeight)), ImPlotFlags.NoMenus | ImPlotFlags.NoBoxSelect))
            return;

        ImPlot.SetupAxes("Time (s)", "DPS", ImPlotAxisFlags.AutoFit, ImPlotAxisFlags.AutoFit);

        var longest = 0;
        var index = 0;
        foreach (var s in series)
        {
            if (s.Values.Length == 0)
                continue;
            longest = System.Math.Max(longest, s.Values.Length);
            ImPlot.SetNextLineStyle(Theme.Series[index++ % Theme.Series.Length], 2f);
            ImPlot.PlotLine(s.Label, ref s.Values[0], s.Values.Length, 1, 0, ImPlotLineFlags.None, 0, sizeof(float));
        }

        if (threshold is { } t && longest > 1)
        {
            var line = new[] { (float)t, (float)t };
            ImPlot.SetNextLineStyle(Theme.Bad, 1.5f);
            ImPlot.PlotLine("Check", ref line[0], 2, longest - 1, 0, ImPlotLineFlags.None, 0, sizeof(float));
        }

        DrawDeaths(deaths);

        ImPlot.EndPlot();
    }
}
