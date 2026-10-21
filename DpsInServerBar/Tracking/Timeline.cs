using System;
using System.Collections.Generic;
using System.Linq;

namespace DpsInServerBar.Tracking;

/// <summary>Helpers turning cumulative-damage timelines into DPS curves.</summary>
public static class Timeline
{
    /// <summary>Linearly resamples (time, cumulative damage) points onto whole seconds 0..duration.</summary>
    public static double[] Resample(IReadOnlyList<(double T, double Value)> points, double durationSeconds)
    {
        var length = Math.Max(0, (int)Math.Floor(durationSeconds)) + 1;
        var result = new double[length];
        if (points.Count == 0)
            return result;

        var j = 0;
        for (var s = 0; s < length; s++)
        {
            while (j < points.Count - 1 && points[j + 1].T <= s)
                j++;

            var a = points[j];
            if (s <= a.T || j == points.Count - 1)
            {
                // Before the first point: interpolate from (0, 0); after the last one: hold the last value.
                result[s] = s <= a.T && a.T > 0 ? a.Value * s / a.T : a.Value;
                continue;
            }

            var b = points[j + 1];
            var f = (s - a.T) / Math.Max(1e-6, b.T - a.T);
            result[s] = a.Value + (b.Value - a.Value) * f;
        }

        return result;
    }

    /// <summary>DPS over the last <paramref name="window"/> seconds at each second.</summary>
    public static float[] Rolling(double[] cumulative, int window)
    {
        var result = new float[cumulative.Length];
        for (var s = 1; s < cumulative.Length; s++)
        {
            var from = Math.Max(0, s - window);
            result[s] = (float)((cumulative[s] - cumulative[from]) / (s - from));
        }

        if (result.Length > 1)
            result[0] = result[1];
        return result;
    }

    /// <summary>
    /// GCD uptime in % over the last <paramref name="window"/> seconds, at each second; NaN (a gap in the graph)
    /// where nothing could be attacked.
    /// </summary>
    public static float[] Uptime(float[] availableBySecond, float[] activeBySecond, int window)
    {
        var result = new float[availableBySecond.Length];
        for (var s = 0; s < result.Length; s++)
        {
            float available = 0, active = 0;
            for (var i = Math.Max(0, s - window + 1); i <= s; i++)
            {
                available += availableBySecond[i];
                active += activeBySecond[i];
            }

            result[s] = available < 0.25f ? float.NaN : 100 * active / available;
        }

        return result;
    }

    /// <summary>100 for the seconds where no enemy was targetable (mostly), 0 otherwise.</summary>
    public static float[] Downtime(float[] availableBySecond) =>
        availableBySecond.Select(a => a < 0.5f ? 100f : 0f).ToArray();

    /// <summary>Encounter DPS (cumulative damage / elapsed) at each second.</summary>
    public static float[] Average(double[] cumulative)
    {
        var result = new float[cumulative.Length];
        for (var s = 1; s < cumulative.Length; s++)
            result[s] = (float)(cumulative[s] / s);

        if (result.Length > 1)
            result[0] = result[1];
        return result;
    }
}
