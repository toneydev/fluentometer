// app/Fluentometer.Logic/Demo/DemoUsageSimulator.cs
using System;
using System.Collections.Generic;
using Fluentometer.Logic.Ipc;

namespace Fluentometer.Logic.Demo;

/// <summary>
/// Pure, deterministic generator of synthetic usage snapshots for Demonstration Mode.
/// Returns one <see cref="UsageSnapshot"/> per demo-supported provider, Claude first, then ChatGPT, then Gemini.
///
/// <para>
/// Provider catalog (explicit table — Option B by design): each supported provider has a
/// dedicated sampler method here.  When a real provider is added to the product, add its
/// demo sample below and update <see cref="DemoProviderIds"/>.  This keeps the demo
/// decoupled from runtime infrastructure (credentials, file-system detectors, etc.) while
/// making gaps visible — the <c>DemoProviderIdSetIsExactlyClaudeGeminiAndChatGpt</c> test will
/// fail until the table is updated.
/// </para>
///
/// <para>
/// All three demo providers (Claude, ChatGPT, Gemini) are now server-truth providers with
/// animated percent gauges (<c>Utilization != null</c>).  Each is phase-shifted by 1/3 of a
/// cycle so their bars move visually out of step with one another: Claude at phase 0,
/// ChatGPT at 1/3 cycle, Gemini at 2/3 cycle.
/// </para>
///
/// <para>
/// No timers, randomness, or <see cref="DateTime.Now"/> — it is a pure function of
/// (elapsedSeconds, nowUnix) so it can be unit-tested directly.
/// </para>
/// </summary>
public static class DemoUsageSimulator
{
    public const double CycleSeconds = 30.0;
    public const int SessionWindows = 5;

    /// <summary>
    /// The ordered set of provider IDs produced by this simulator.
    /// A test asserts this equals the actual set returned by <see cref="Sample"/>;
    /// update both when adding a new provider demo sample.
    /// </summary>
    public static readonly IReadOnlyList<string> DemoProviderIds = new[] { "claude", "chatgpt", "gemini" };

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

    /// <summary>
    /// Returns one synthetic <see cref="UsageSnapshot"/> per demo-supported provider,
    /// in order: Claude (server-truth), ChatGPT (server-truth), Gemini (server-truth).
    /// The list is deterministic — identical inputs always produce identical outputs.
    /// </summary>
    public static IReadOnlyList<UsageSnapshot> Sample(double elapsedSeconds, long nowUnix)
    {
        return new[]
        {
            SampleClaude(elapsedSeconds, nowUnix),
            SampleChatGpt(elapsedSeconds, nowUnix),
            SampleGemini(elapsedSeconds, nowUnix),
        };
    }

    // ── Per-provider samplers ───────────────────────────────────────────────────

    private static UsageSnapshot SampleClaude(double elapsedSeconds, long nowUnix)
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

    private static UsageSnapshot SampleChatGpt(double elapsedSeconds, long nowUnix)
    {
        // Phase-shifted version of SampleClaude: shift by CycleSeconds/3 (10s) so ChatGPT
        // bars are visually distinct from Claude's at any given moment — not in sync.
        if (elapsedSeconds < 0) elapsedSeconds = 0;
        var shifted = elapsedSeconds + CycleSeconds / 3.0;

        var cycle = (int)Math.Floor(shifted / CycleSeconds);
        var t = shifted - cycle * CycleSeconds; // 0 .. CycleSeconds

        var peaks = SessionPeaks[cycle % SessionPeaks.Length];
        var weeklyPeak = WeeklyPeaks[cycle % WeeklyPeaks.Length];

        var session = SessionValue(t, peaks);
        var weeklyAll = weeklyPeak * WeeklyFraction(t, peaks);

        var gauges = new List<Gauge>
        {
            new("chatgpt_primary", "ChatGPT 5-hour", session, Pct(session), nowUnix + 5 * 3600, "subscription limit"),
            new("chatgpt_secondary", "ChatGPT Weekly", weeklyAll, Pct(weeklyAll), nowUnix + 7 * 86400, "subscription limit"),
        };

        return new UsageSnapshot("chatgpt", nowUnix, "demo", "ok", "Demo", gauges);
    }

    private static UsageSnapshot SampleGemini(double elapsedSeconds, long nowUnix)
    {
        // Server-truth now: a real animated percent gauge (Utilization != null), matching the
        // GeminiProvider gauge shape ("Gemini Requests", daily limit). Phase-shifted by
        // 2/3 of a cycle so its bar is visually distinct from Claude (0) and ChatGPT (1/3).
        if (elapsedSeconds < 0) elapsedSeconds = 0;
        var shifted = elapsedSeconds + 2.0 * CycleSeconds / 3.0;

        var cycle = (int)Math.Floor(shifted / CycleSeconds);
        var t = shifted - cycle * CycleSeconds;

        var peaks = SessionPeaks[cycle % SessionPeaks.Length];
        var daily = SessionValue(t, peaks);

        var gauges = new List<Gauge>
        {
            new("gemini_requests", "Gemini Requests", daily, Pct(daily), nowUnix + 24 * 3600, "daily limit"),
        };

        return new UsageSnapshot("gemini", nowUnix, "demo", "ok", "Gemini (Free)", gauges);
    }

    // ── Claude math helpers (unchanged) ────────────────────────────────────────

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
