using System;
using System.Collections.Generic;
using System.Linq;
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.ViewModels;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Result of a staleness evaluation across all known providers.
/// </summary>
public sealed record StalenessResult(
    bool IsStale,
    IReadOnlyList<string> StaleProviders,
    string Detail);

/// <summary>
/// Pure, time-injected staleness evaluator. Fed per-provider <see cref="RefreshStatus"/>
/// updates from the capture engine; evaluated against an externally supplied clock so the
/// UI's 1-second tick can drive it (catching a wedged poll loop that emits no events).
///
/// <para>A provider is stale at <c>now</c> when EITHER:</para>
/// <list type="bullet">
///   <item><see cref="FailureThreshold"/> consecutive failures have accrued, OR</item>
///   <item>it has succeeded before AND the age of that success exceeds
///         <see cref="AgeIntervalMultiplier"/> × its poll interval.</item>
/// </list>
/// The age path requires a prior success so a never-yet-succeeded provider at startup
/// cannot false-positive purely on elapsed time.
/// </summary>
public sealed class StalenessWatcher
{
    /// <summary>Consecutive failed refreshes that trip the stale state.</summary>
    public const int FailureThreshold = 2;

    /// <summary>Age multiple of the poll interval that trips the stale state.</summary>
    public const double AgeIntervalMultiplier = 2.5;

    private readonly Dictionary<string, RefreshStatus> _latest = new();

    /// <summary>Records the most recent outcome for a provider. Last write wins per provider.</summary>
    public void Update(RefreshStatus status) => _latest[status.ProviderId] = status;

    /// <summary>Evaluates staleness across all known providers at <paramref name="now"/>.</summary>
    public StalenessResult Evaluate(DateTimeOffset now)
    {
        var stale = new List<string>();
        foreach (var (id, s) in _latest)
            if (IsProviderStale(s, now))
                stale.Add(id);

        if (stale.Count == 0)
            return new StalenessResult(false, Array.Empty<string>(), "");

        return new StalenessResult(true, stale, BuildDetail(stale, now));
    }

    private static bool IsProviderStale(RefreshStatus s, DateTimeOffset now)
    {
        if (s.ConsecutiveFailures >= FailureThreshold)
            return true;

        if (s.LastSuccessUtc is { } last)
        {
            var ageSecs = (now - last).TotalSeconds;
            if (ageSecs > AgeIntervalMultiplier * s.IntervalSecs)
                return true;
        }

        return false;
    }

    private string BuildDetail(IReadOnlyList<string> staleIds, DateTimeOffset now)
    {
        var names = staleIds
            .Select(ProviderGroupViewModel.DisplayNameFor)
            .ToList();
        var joined = names.Count == 1
            ? names[0]
            : string.Join(" and ", new[] { string.Join(", ", names.Take(names.Count - 1)), names[^1] }
                .Where(p => p.Length > 0));

        // Pick the single worst provider for the explanatory clause:
        // prefer the one with the oldest last success (or never succeeded, which sorts highest).
        var worst = staleIds
            .Select(id => _latest[id])
            .OrderByDescending(s => s.LastSuccessUtc is { } l ? (now - l).TotalSeconds : double.MaxValue)
            .First();

        var verb = staleIds.Count == 1 ? "is" : "are";

        if (worst.LastSuccessUtc is { } lastOk)
        {
            var ago = FormatAgo(now - lastOk);
            var rate = FormatAgo(TimeSpan.FromSeconds(worst.IntervalSecs));
            return $"{joined} usage data {verb} stale — last successful update {ago} ago "
                 + $"(expected ~every {rate}).";
        }

        return $"{joined} usage data {verb} unreachable — "
             + $"{worst.ConsecutiveFailures} failed refresh attempts.";
    }

    private static string FormatAgo(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds}s";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
        return $"{(int)span.TotalHours}h {span.Minutes}m";
    }
}
