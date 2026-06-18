using System;
using System.Collections.Generic;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Result of summarizing usage events over a time window.
/// </summary>
/// <param name="TokensInWindow">Total tokens consumed within the window.</param>
/// <param name="ResetsAt">
/// Unix timestamp (seconds) at which the window resets, based on the oldest
/// in-window event plus the window duration. <c>null</c> if no events are in window.
/// </param>
public readonly record struct WindowSummary(long TokensInWindow, long? ResetsAt);

/// <summary>
/// Computes rolling-window usage summaries from a list of <see cref="UsageEvent"/> values.
/// </summary>
public static class UsageWindow
{
    /// <summary>Five hours expressed in seconds (5 × 3 600 = 18 000).</summary>
    public const long FiveHourSecs = 5 * 3600;

    /// <summary>Seven days expressed in seconds (7 × 24 × 3 600 = 604 800).</summary>
    public const long SevenDaySecs = 7 * 24 * 3600;

    /// <summary>
    /// Summarizes all events whose <see cref="UsageEvent.TsUnix"/> falls within
    /// <c>[nowUnix - windowSecs, nowUnix]</c> (both endpoints inclusive).
    /// </summary>
    /// <param name="events">All available usage events (may include out-of-window ones).</param>
    /// <param name="windowSecs">Width of the rolling window in seconds.</param>
    /// <param name="nowUnix">Current time as a Unix timestamp in seconds.</param>
    /// <returns>
    /// A <see cref="WindowSummary"/> with the sum of in-window tokens and the
    /// reset time derived from the oldest in-window event.
    /// </returns>
    public static WindowSummary Summarize(
        IReadOnlyList<UsageEvent> events,
        long windowSecs,
        long nowUnix)
    {
        long start = nowUnix - windowSecs;

        long tokens = 0L;
        long? oldest = null;

        foreach (var e in events)
        {
            if (e.TsUnix < start || e.TsUnix > nowUnix)
                continue;

            tokens += e.TotalTokens;
            oldest = oldest.HasValue ? Math.Min(oldest.Value, e.TsUnix) : e.TsUnix;
        }

        return new WindowSummary(
            TokensInWindow: tokens,
            ResetsAt: oldest.HasValue ? oldest.Value + windowSecs : null);
    }
}
