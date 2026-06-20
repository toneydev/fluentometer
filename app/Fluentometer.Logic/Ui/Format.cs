using System;

namespace Fluentometer.Logic.Ui;

public static class Format
{
    public static string ResetCountdown(long? resetsAtUnix, long nowUnix)
    {
        if (resetsAtUnix is null) return "—";
        var secs = resetsAtUnix.Value - nowUnix;
        if (secs <= 0) return "resets now";
        var ts = TimeSpan.FromSeconds(secs);
        if (ts.TotalHours >= 1) return $"resets in {(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"resets in {ts.Minutes}m";
        return $"resets in {ts.Seconds}s";
    }

    /// <summary>
    /// Compact reset countdown for the slim Mini card, where the countdown rides
    /// beside the (ellipsised) label in a ~160px card and has no room for the
    /// "resets in " prefix. Mirrors <see cref="ResetCountdown"/>'s buckets but
    /// drops the prefix: "2h 14m" / "5m" / "47s" / "now" / "—".
    /// </summary>
    public static string ResetCountdownShort(long? resetsAtUnix, long nowUnix)
    {
        if (resetsAtUnix is null) return "—";
        var secs = resetsAtUnix.Value - nowUnix;
        if (secs <= 0) return "now";
        var ts = TimeSpan.FromSeconds(secs);
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m";
        return $"{ts.Seconds}s";
    }

    public static string PercentOrEstimate(double? utilization, string usedLabel) =>
        utilization is { } u ? $"{(int)Math.Round(u * 100)}%" : usedLabel;

    public static double BarValue(double? utilization) =>
        utilization is { } u ? Math.Clamp(u, 0.0, 1.0) : 0.0;
}
