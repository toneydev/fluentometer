using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Fluentometer.Logic.Ipc;

public sealed record Gauge(
    string Id,
    string Label,
    double? Utilization,
    string UsedLabel,
    long? ResetsAt,
    string LimitLabel);

/// <summary>
/// A point-in-time capture of one provider's usage data, emitted by
/// <see cref="Fluentometer.Logic.Capture.LiveUsageClient"/> via
/// <see cref="IUsageClient.SnapshotReceived"/> once per provider per poll cycle.
/// </summary>
/// <param name="Provider">Stable provider id (e.g. "claude", "gemini").</param>
/// <param name="CapturedAt">Unix timestamp (seconds) when the snapshot was taken.</param>
/// <param name="Source">
/// Data provenance. Known values:
/// <list type="bullet">
///   <item><c>oauth</c> — live data from the provider's OAuth usage endpoint.</item>
///   <item><c>jsonl</c> — local estimate from Claude's session JSONL files (degraded fallback).</item>
///   <item><c>demo</c> — synthetic data injected by the demo mode driver.</item>
///   <item><c>local</c> — local estimate with no network call (e.g. Gemini provider).</item>
/// </list>
/// This field is NOT switched on by the dashboard (only <see cref="Health"/> is);
/// it is informational / for tooltips.
/// </param>
/// <param name="Health">
/// Provider health signal. Hard UI contract — emit exactly one of:
/// <c>ok</c> / <c>degraded</c> / <c>needs-signin</c> / <c>error</c> (kebab-case).
/// </param>
/// <param name="Plan">Human-readable plan/tier name (e.g. "Max", "Gemini").</param>
/// <param name="Gauges">Usage gauge list. May be empty on error states.</param>
public sealed record UsageSnapshot(
    string Provider,
    long CapturedAt,
    string Source,
    string Health,
    string Plan,
    IReadOnlyList<Gauge> Gauges);

/// <summary>
/// Per-provider outcome of one refresh cycle, raised by
/// <see cref="Fluentometer.Logic.Capture.LiveUsageClient"/> via
/// <see cref="IUsageClient.StatusChanged"/> once per provider per cycle — even when no
/// snapshot was emitted (e.g. the provider threw). This is the channel that makes silent
/// failures observable; it never carries gauge data, so it cannot blank the dashboard.
/// </summary>
/// <param name="ProviderId">Stable provider id (e.g. "claude").</param>
/// <param name="LastSuccessUtc">
/// Time of the last successful refresh, or <c>null</c> if this provider has never succeeded.
/// A "success" is a snapshot with Health ok / degraded / needs-signin.
/// </param>
/// <param name="ConsecutiveFailures">Failed cycles since the last success (0 right after a success).</param>
/// <param name="IntervalSecs">The engine's current effective poll interval, in seconds.</param>
public sealed record RefreshStatus(
    string ProviderId,
    DateTimeOffset? LastSuccessUtc,
    int ConsecutiveFailures,
    long IntervalSecs);

public sealed record ClientCommand(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("seconds")] long? Seconds = null)
{
    public static ClientCommand GetSnapshot() => new("getSnapshot");
    public static ClientCommand RefreshNow() => new("refreshNow");
    public static ClientCommand SetPollInterval(long seconds) => new("setPollInterval", seconds);
}
