// app/Fluentometer.Logic/Demo/DemoUsageSimulator.cs
using System;
using System.Collections.Generic;
using Fluentometer.Logic.Ipc;

namespace Fluentometer.Logic.Demo;

/// <summary>
/// Pure, deterministic generator of synthetic usage snapshots for Demonstration Mode.
/// A 30-second cycle fills the weekly gauge to 90-100% while the 5-hour gauge bursts
/// up and resets across five windows; the cycle loops with per-cycle peak variation.
/// No timers, randomness, or DateTime.Now — it is a pure function of (elapsed, now)
/// so it can be unit-tested directly.
/// </summary>
public static class DemoUsageSimulator
{
    public const double CycleSeconds = 30.0;
    public const int SessionWindows = 5;

    // Per-cycle 5-hour peak heights, one row per window. Cycles index modulo the
    // table length, so successive loops differ. Every row contains at least one
    // ~1.0 peak and at least one in [0.30, 0.70].
    private static readonly double[][] SessionPeaks =
    {
        new[] { 0.55, 1.00, 0.40, 0.85, 0.65 },
        new[] { 1.00, 0.45, 0.70, 0.35, 0.95 },
        new[] { 0.60, 0.50, 1.00, 0.45, 0.80 },
    };

    // Per-cycle weekly completion target (0.90-1.00).
    private static readonly double[] WeeklyPeaks = { 0.94, 1.00, 0.97 };

    // Per-cycle Sonnet scaling factor (0.70-0.85) — "just more slowly".
    private static readonly double[] SonnetFactors = { 0.78, 0.72, 0.84 };

    public static UsageSnapshot Sample(double elapsedSeconds, long nowUnix)
    {
        if (elapsedSeconds < 0) elapsedSeconds = 0;

        var cycle = (int)Math.Floor(elapsedSeconds / CycleSeconds);
        var t = elapsedSeconds - cycle * CycleSeconds; // 0 .. CycleSeconds

        var peaks = SessionPeaks[cycle % SessionPeaks.Length];
        var weeklyPeak = WeeklyPeaks[cycle % WeeklyPeaks.Length];
        var sonnetFactor = SonnetFactors[cycle % SonnetFactors.Length];

        var session = SessionValue(t, peaks);
        var weeklyAll = weeklyPeak * WeeklyFraction(t, peaks);
        var weeklySonnet = weeklyAll * sonnetFactor;

        var gauges = new List<Gauge>
        {
            new("session", "Claude 5-hour", session, Pct(session), nowUnix + 5 * 3600, "5-hour limit"),
            new("weekly_all", "Claude Weekly", weeklyAll, Pct(weeklyAll), nowUnix + 7 * 86400, "weekly limit"),
            new("weekly_scoped", "Claude Weekly (Sonnet)", weeklySonnet, Pct(weeklySonnet),
                nowUnix + 7 * 86400, "weekly limit (Sonnet)"),
        };

        return new UsageSnapshot("claude", nowUnix, "demo", "ok", "Demo", gauges);
    }

    // 5-hour gauge: ramps 0 -> peak across its window (eased), snapping back at each
    // boundary — a sawtooth.
    private static double SessionValue(double t, double[] peaks)
    {
        var windowLen = CycleSeconds / SessionWindows;
        var idx = Math.Min((int)(t / windowLen), SessionWindows - 1);
        var localFrac = (t - idx * windowLen) / windowLen; // 0 .. 1
        return peaks[idx] * EaseOut(localFrac);
    }

    // Weekly fraction (0..1): the normalized cumulative of session activity, so the
    // weekly rises *because* the 5-hour burned. Continuous and monotonic: at each
    // window boundary the completed sum gains peaks[idx] exactly as the partial term
    // resets to 0. Reaches 1.0 as t -> CycleSeconds.
    private static double WeeklyFraction(double t, double[] peaks)
    {
        var windowLen = CycleSeconds / SessionWindows;
        var idx = Math.Min((int)(t / windowLen), SessionWindows - 1);
        var localFrac = (t - idx * windowLen) / windowLen; // 0 .. 1

        double completed = 0, total = 0;
        for (var j = 0; j < SessionWindows; j++)
        {
            total += peaks[j];
            if (j < idx) completed += peaks[j];
        }
        var partial = peaks[idx] * localFrac;
        return (completed + partial) / total;
    }

    private static double EaseOut(double x)
    {
        x = Math.Clamp(x, 0.0, 1.0);
        return 1.0 - (1.0 - x) * (1.0 - x);
    }

    private static string Pct(double v) => $"{(int)Math.Round(v * 100)}%";
}
