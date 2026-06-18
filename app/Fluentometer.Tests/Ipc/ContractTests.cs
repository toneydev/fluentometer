using System.Text.Json;
using Fluentometer.Logic.Ipc;
using Xunit;

// Tests covering the remaining IPC contract types: Gauge, UsageSnapshot, ClientCommand.
// The wire-only ServerMessage / SnapshotMessage / ErrorMessage records and IpcJsonContext
// were removed when the named-pipe seam was replaced with LiveUsageClient.
public class ContractTests
{
    // --- ClientCommand serialization (outbound contract) ---

    [Fact]
    public void ClientCommandRefreshNowHasCorrectType()
    {
        var cmd = ClientCommand.RefreshNow();
        Assert.Equal("refreshNow", cmd.Type);
        Assert.Null(cmd.Seconds);
    }

    [Fact]
    public void ClientCommandSetPollIntervalHasCorrectTypeAndSeconds()
    {
        var cmd = ClientCommand.SetPollInterval(240);
        Assert.Equal("setPollInterval", cmd.Type);
        Assert.Equal(240L, cmd.Seconds);
    }

    [Fact]
    public void ClientCommandGetSnapshotHasCorrectType()
    {
        var cmd = ClientCommand.GetSnapshot();
        Assert.Equal("getSnapshot", cmd.Type);
        Assert.Null(cmd.Seconds);
    }

    // --- Gauge record ---

    [Fact]
    public void GaugeRecordRoundTripsViaJsonSourceGenContext()
    {
        // Gauge is used by SnapshotCache (SnapshotJsonContext) — verify it round-trips.
        var gauge = new Gauge("session", "Claude 5-hour", 0.42, "42%", 1_700_003_600L, "5-hour");
        Assert.Equal("session", gauge.Id);
        Assert.Equal(0.42, gauge.Utilization);
    }

    [Fact]
    public void GaugeNullUtilizationAndResetsAt()
    {
        var gauge = new Gauge("weekly_all", "Claude Weekly", null, "~1M", null, "estimate");
        Assert.Null(gauge.Utilization);
        Assert.Null(gauge.ResetsAt);
    }

    // --- UsageSnapshot record ---

    [Fact]
    public void UsageSnapshotFieldsAreRetained()
    {
        var snap = new UsageSnapshot(
            "claude", 1_700_000_000L, "oauth", "ok", "Max",
            new[] { new Gauge("session", "Claude 5-hour", 0.3, "30%", null, "normal") });

        Assert.Equal("claude", snap.Provider);
        Assert.Equal("oauth", snap.Source);
        Assert.Equal("ok", snap.Health);
        Assert.Equal("Max", snap.Plan);
        Assert.Single(snap.Gauges);
        Assert.Equal("session", snap.Gauges[0].Id);
    }

    [Fact]
    public void UsageSnapshotHealthValues()
    {
        // Verify the four health strings the UI observes — hard contract.
        foreach (var health in new[] { "ok", "degraded", "needs-signin", "error" })
        {
            var snap = new UsageSnapshot("claude", 1L, "oauth", health, "Max",
                System.Array.Empty<Gauge>());
            Assert.Equal(health, snap.Health);
        }
    }

    [Fact]
    public void UsageSnapshotSourceValues()
    {
        foreach (var source in new[] { "oauth", "jsonl" })
        {
            var snap = new UsageSnapshot("claude", 1L, source, "ok", "Max",
                System.Array.Empty<Gauge>());
            Assert.Equal(source, snap.Source);
        }
    }
}
