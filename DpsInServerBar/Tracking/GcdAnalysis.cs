using System.Collections.Generic;

namespace DpsInServerBar.Tracking;

/// <summary>Why the GCD sat ready without being used.</summary>
public enum GcdGapKind
{
    /// <summary>Stuck in an oGCD's animation lock while the GCD was ready.</summary>
    Clipping,

    /// <summary>Free to act, the next GCD just came late (reaction, latency, a short move).</summary>
    Late,

    /// <summary>A whole GCD or more without one, the boss targetable (movement, mechanics, forgotten).</summary>
    NoGcd,
}

/// <summary>One stretch where the GCD was ready and unused. <see cref="AtSeconds"/> counts from the pull start.</summary>
public sealed record GcdGap(double AtSeconds, double Seconds, GcdGapKind Kind);

/// <summary>
/// Where the GCD uptime went: the time lost, split by cause. Only time with an enemy targetable counts, so the
/// four causes add up to the uptime's lost seconds.
/// </summary>
public sealed record GcdAnalysis(
    int GcdCount,
    double AverageRecast,
    double ClipSeconds,
    int ClipCount,
    double LateSeconds,
    double NoGcdSeconds,
    int NoGcdCount,
    double DeadSeconds,
    List<GcdGap> Gaps)
{
    public double LostSeconds => ClipSeconds + LateSeconds + NoGcdSeconds + DeadSeconds;

    /// <summary>GCDs that would fit in the lost time.</summary>
    public double LostGcds => AverageRecast > 0 ? LostSeconds / AverageRecast : 0;
}
