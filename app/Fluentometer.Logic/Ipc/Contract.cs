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

public sealed record ClientCommand(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("seconds")] long? Seconds = null)
{
    public static ClientCommand GetSnapshot() => new("getSnapshot");
    public static ClientCommand RefreshNow() => new("refreshNow");
    public static ClientCommand SetPollInterval(long seconds) => new("setPollInterval", seconds);
}
