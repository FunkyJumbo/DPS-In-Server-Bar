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
public sealed record GcdGap(double AtSeconds, double Seconds, GcdGapKind Kind, double LockedSeconds = 0, uint ActionId = 0)
{
    /// <summary>Counted as a clip: held at least 0.1 s by an animation lock (<see cref="ActionId"/>: the oGCD used then, 0 if unknown).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsClip => Kind != GcdGapKind.NoGcd && (LockedSeconds >= 0.1 || (LockedSeconds == 0 && Kind == GcdGapKind.Clipping));
}

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
