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
