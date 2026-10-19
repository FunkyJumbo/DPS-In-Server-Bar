using System;
using System.Collections.Generic;

namespace DpsInServerBar.Rdps;

/// <summary>
/// Estimates a player's own crit (or direct hit) rate from observed hits, including hits made under rate buffs:
/// a hit with an external bonus b crits with probability min(1, rate + b). The rate is the maximum likelihood
/// estimate, with a few pseudo-observations at a typical rate so early estimates stay sensible.
/// </summary>
public sealed class RateEstimator
{
    private readonly double priorRate;
    private readonly double priorWeight;

    // Hits grouped by external bonus (there are only a handful of distinct values in practice).
    private readonly Dictionary<int, (int Hits, int Successes)> byBonus = new();

    private double? cached;

    public RateEstimator(double priorRate, double priorWeight)
    {
        this.priorRate = priorRate;
        this.priorWeight = priorWeight;
    }

    public int Samples { get; private set; }

    public void Add(double externalBonus, bool success)
    {
        var key = (int)Math.Round(externalBonus * 1000);
        var (hits, successes) = byBonus.GetValueOrDefault(key);
        byBonus[key] = (hits + 1, successes + (success ? 1 : 0));
        Samples++;
        cached = null;
    }

    public double Estimate() => cached ??= Solve();

    private double Solve()
    {
        // The derivative of the log-likelihood is decreasing in the rate: find its zero by bisection.
        var maxBonus = 0.0;
        foreach (var key in byBonus.Keys)
            maxBonus = Math.Max(maxBonus, key / 1000.0);

        var lo = 0.0005;
        var hi = Math.Max(lo, 0.9995 - maxBonus);
        for (var i = 0; i < 50; i++)
        {
            var mid = (lo + hi) / 2;
            if (Derivative(mid) > 0)
                lo = mid;
            else
                hi = mid;
        }

        return (lo + hi) / 2;
    }

    private double Derivative(double rate)
    {
        // Prior: priorWeight hits without bonus, priorRate of which succeeded.
        var d = priorWeight * (priorRate / rate - (1 - priorRate) / (1 - rate));

        foreach (var (key, (hits, successes)) in byBonus)
        {
            var p = Math.Min(0.9999, rate + key / 1000.0);
            d += successes / p - (hits - successes) / (1 - p);
        }

        return d;
    }
}
