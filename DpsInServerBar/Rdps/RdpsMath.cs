using System;
using System.Collections.Generic;

namespace DpsInServerBar.Rdps;

/// <summary>An external buff active on a hit: a damage multiplier and/or a crit / direct hit rate bonus.</summary>
public readonly record struct BuffEffect(uint SourceId, double DamageMultiplier, double CritRate, double DirectHitRate);

/// <summary>Everything needed to split one damage event between the attacker and the players who buffed it.</summary>
public sealed class HitContext
{
    public double Damage { get; init; }
    public bool Crit { get; init; }
    public bool DirectHit { get; init; }
    public bool AutoCrit { get; init; }
    public bool AutoDirectHit { get; init; }

    /// <summary>DoT ticks are an average of every outcome and are allocated probabilistically.</summary>
    public bool IsDot { get; init; }

    /// <summary>The attacker's own crit / direct hit rates (stats + self buffs), 0-1.</summary>
    public double BaseCritRate { get; init; }

    public double BaseDirectHitRate { get; init; }
    public required IReadOnlyList<BuffEffect> Buffs { get; init; }
}

/// <summary>
/// rDPS buff allocation following FFLogs' published method (joncho's "FFLogs Buff Allocation Math", Endwalker+):
/// <list type="bullet">
/// <item>% buffs: the gain N - N/M is split between buffs in proportion to ln(multiplier).</item>
/// <item>crit / DH buffs: a crit (DH) is split by the probability it came from buffs, (Cb - Cu) / Cb; the buffed part
/// treats the crit (DH) multiplier as an extra external buff, credited to rate buffers in proportion to their rate.</item>
/// <item>auto crit / DH hits ignore the outcome, but rate buffs act as a damage multiplier (patch 6.2).</item>
/// <item>DoT ticks: probability-weighted sum over the four outcomes.</item>
/// </list>
/// </summary>
public static class RdpsMath
{
    public const double DirectHitMultiplier = 1.25;

    /// <summary>Crit damage multiplier for a given base crit rate (1.35 + rate, see FFXIV's crit formulas).</summary>
    public static double CritMultiplier(double baseCritRate) => 1.35 + baseCritRate;

    /// <summary>Calls <paramref name="credit"/> once per buff source with the damage attributed to it.</summary>
    public static void Allocate(HitContext hit, Action<uint, double> credit)
    {
        if (hit.Damage <= 0 || hit.Buffs.Count == 0)
            return;

        var ctx = new Context(hit);
        if (!ctx.HasAnything)
            return;

        if (!hit.IsDot)
        {
            ctx.AllocateOutcome(hit.Damage, hit.Crit && !hit.AutoCrit, hit.DirectHit && !hit.AutoDirectHit, 1, credit);
            return;
        }

        // A DoT tick is an expected value; recover the "no crit no DH" base and weight each outcome by its probability.
        var c = ctx.BuffedCrit;
        var d = ctx.BuffedDirectHit;
        var mc = ctx.CritMult;
        var md = DirectHitMultiplier;
        var expected = (1 - c) * (1 - d) + c * (1 - d) * mc + (1 - c) * d * md + c * d * mc * md;
        var baseDamage = hit.Damage / expected;

        ctx.AllocateOutcome(baseDamage, false, false, (1 - c) * (1 - d), credit);
        ctx.AllocateOutcome(baseDamage * mc, true, false, c * (1 - d), credit);
        ctx.AllocateOutcome(baseDamage * md, false, true, (1 - c) * d, credit);
        ctx.AllocateOutcome(baseDamage * mc * md, true, true, c * d, credit);
    }

    private sealed class Context
    {
        private readonly List<(uint Source, double Mult)> percentBuffs = [];
        private readonly List<(uint Source, double Rate)> critBuffs = [];
        private readonly List<(uint Source, double Rate)> dhBuffs = [];
        private readonly double totalCritBuff;
        private readonly double totalDhBuff;

        // Extra multipliers applying to the whole hit (auto crit / auto DH under rate buffs).
        private readonly List<Extra> fixedExtras = [];

        public Context(HitContext hit)
        {
            foreach (var b in hit.Buffs)
            {
                if (b.DamageMultiplier > 1)
                    percentBuffs.Add((b.SourceId, b.DamageMultiplier));
                if (b.CritRate > 0)
                    critBuffs.Add((b.SourceId, b.CritRate));
                if (b.DirectHitRate > 0)
                    dhBuffs.Add((b.SourceId, b.DirectHitRate));
            }

            foreach (var c in critBuffs)
                totalCritBuff += c.Rate;
            foreach (var d in dhBuffs)
                totalDhBuff += d.Rate;

            BaseCrit = Math.Clamp(hit.BaseCritRate, 0, 1);
            BaseDirectHit = Math.Clamp(hit.BaseDirectHitRate, 0, 1);
            BuffedCrit = Math.Min(1, BaseCrit + totalCritBuff);
            BuffedDirectHit = Math.Min(1, BaseDirectHit + totalDhBuff);
            CritMult = CritMultiplier(BaseCrit);

            // Since 6.2, rate buffs boost guaranteed crits / DHs as a damage multiplier, split by each buff's rate.
            if (hit.AutoCrit && totalCritBuff > 0)
                fixedExtras.Add(new Extra(1 + totalCritBuff * (CritMult - 1), critBuffs, totalCritBuff));
            if (hit.AutoDirectHit && totalDhBuff > 0)
                fixedExtras.Add(new Extra(1 + totalDhBuff * (DirectHitMultiplier - 1), dhBuffs, totalDhBuff));
        }

        public double BaseCrit { get; }
        public double BaseDirectHit { get; }
        public double BuffedCrit { get; }
        public double BuffedDirectHit { get; }
        public double CritMult { get; }
        public bool HasAnything => percentBuffs.Count > 0 || critBuffs.Count > 0 || dhBuffs.Count > 0;

        public void AllocateOutcome(double damage, bool crit, bool directHit, double weight, Action<uint, double> credit)
        {
            if (weight <= 0 || damage <= 0)
                return;

            // Probability that the crit / DH was caused by buffs rather than the player's own rate.
            var critBuffed = crit && BuffedCrit > 0 ? (BuffedCrit - BaseCrit) / BuffedCrit : 0;
            var dhBuffed = directHit && BuffedDirectHit > 0 ? (BuffedDirectHit - BaseDirectHit) / BuffedDirectHit : 0;

            var critExtra = new Extra(CritMult, critBuffs, totalCritBuff);
            var dhExtra = new Extra(DirectHitMultiplier, dhBuffs, totalDhBuff);

            // Up to four parts: (crit from buffs or not) x (DH from buffs or not).
            Part(damage * weight * critBuffed * dhBuffed, critExtra, dhExtra, credit);
            Part(damage * weight * critBuffed * (1 - dhBuffed), critExtra, null, credit);
            Part(damage * weight * (1 - critBuffed) * dhBuffed, null, dhExtra, credit);
            Part(damage * weight * (1 - critBuffed) * (1 - dhBuffed), null, null, credit);
        }

        /// <summary>Log-ratio split of the gain of one part between % buffs and the optional crit / DH "buffs".</summary>
        private void Part(double damage, Extra? critExtra, Extra? dhExtra, Action<uint, double> credit)
        {
            if (damage <= 0)
                return;

            var logTotal = 0.0;
            foreach (var p in percentBuffs)
                logTotal += Math.Log(p.Mult);
            foreach (var e in fixedExtras)
                logTotal += Math.Log(e.Multiplier);
            if (critExtra is { } ce)
                logTotal += Math.Log(ce.Multiplier);
            if (dhExtra is { } de)
                logTotal += Math.Log(de.Multiplier);

            if (logTotal <= 0)
                return;

            var gain = damage * (1 - Math.Exp(-logTotal));
            foreach (var p in percentBuffs)
                credit(p.Source, gain * Math.Log(p.Mult) / logTotal);
            foreach (var e in fixedExtras)
                e.Distribute(gain * Math.Log(e.Multiplier) / logTotal, credit);
            critExtra?.Distribute(gain * Math.Log(critExtra.Value.Multiplier) / logTotal, credit);
            dhExtra?.Distribute(gain * Math.Log(dhExtra.Value.Multiplier) / logTotal, credit);
        }
    }

    /// <summary>A multiplier credited to several rate buffers, in proportion to the rate each one gave.</summary>
    private readonly record struct Extra(double Multiplier, List<(uint Source, double Rate)> Buffers, double TotalRate)
    {
        public void Distribute(double amount, Action<uint, double> credit)
        {
            if (TotalRate <= 0)
                return;
            foreach (var b in Buffers)
                credit(b.Source, amount * b.Rate / TotalRate);
        }
    }
}
