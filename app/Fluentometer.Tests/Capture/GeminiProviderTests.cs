using System;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Capture;
using Xunit;

namespace Fluentometer.Tests.Capture;

/// <summary>
/// Tests for <see cref="GeminiProvider"/> — the local-estimate-only provider.
///
/// GeminiProvider makes no network calls and always returns a snapshot with:
///   Provider = "gemini"
///   Source   = "local"
///   Health   = "ok"
///   Gauges   with Utilization = null (local estimate only)
///   Labels starting with "Gemini …"
/// </summary>
public sealed class GeminiProviderTests
{
    private const long Now = 1_750_000_000L;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static GeminiProvider BuildProvider(string authType = "oauth-personal") =>
        new(authType);

    // ────────────────────────────────────────────────────────────────────────
    // 1. Provider identity
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProviderId_IsGemini()
    {
        var provider = BuildProvider();
        Assert.Equal("gemini", provider.ProviderId);
    }

    [Fact]
    public void MinPollInterval_Is60Seconds()
    {
        var provider = BuildProvider();
        Assert.Equal(TimeSpan.FromSeconds(60), provider.MinPollInterval);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 2. Snapshot: Provider field = "gemini"
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SnapshotAsync_ReturnsProviderGemini()
    {
        var provider = BuildProvider();

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("gemini", snap.Provider);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 3. Snapshot: Source = "local" (not oauth / jsonl / demo)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SnapshotAsync_ReturnsSourceLocal()
    {
        var provider = BuildProvider();

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("local", snap.Source);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 4. Snapshot: Health = "ok" (local provider is always ok)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SnapshotAsync_ReturnsHealthOk()
    {
        var provider = BuildProvider();

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("ok", snap.Health);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 5. Snapshot: All gauges have Utilization = null (local estimate)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SnapshotAsync_AllGaugesHaveNullUtilization()
    {
        var provider = BuildProvider();

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.NotEmpty(snap.Gauges);
        foreach (var gauge in snap.Gauges)
        {
            Assert.Null(gauge.Utilization);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 6. Snapshot: Gauge labels start with "Gemini"
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SnapshotAsync_GaugeLabelsStartWithGemini()
    {
        var provider = BuildProvider();

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.NotEmpty(snap.Gauges);
        foreach (var gauge in snap.Gauges)
        {
            Assert.StartsWith("Gemini", gauge.Label, StringComparison.Ordinal);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 7. Snapshot: CapturedAt matches the nowUnix argument
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SnapshotAsync_CapturedAtMatchesNowUnix()
    {
        var provider = BuildProvider();
        var specificNow = 1_700_123_456L;

        var snap = await provider.SnapshotAsync(specificNow, CancellationToken.None);

        Assert.Equal(specificNow, snap.CapturedAt);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 8. PlanFromAuthType: auth-type to plan label mapping
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("oauth-personal", "Gemini (Personal)")]
    [InlineData("oauth-workspace", "Gemini (Workspace)")]
    [InlineData("api-key", "Gemini (API Key)")]
    [InlineData("vertex-ai", "Gemini (Vertex AI)")]
    [InlineData("unknown-type", "Gemini (unknown-type)")]
    [InlineData("", "Gemini")]
    [InlineData(null, "Gemini")]
    public void PlanFromAuthType_MapsAuthTypeToExpectedLabel(string? authType, string expected)
    {
        Assert.Equal(expected, GeminiProvider.PlanFromAuthType(authType));
    }

    // ────────────────────────────────────────────────────────────────────────
    // 9. Snapshot plan matches PlanFromAuthType(authType)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SnapshotAsync_PlanDerivedFromAuthType()
    {
        var provider = BuildProvider("oauth-workspace");

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("Gemini (Workspace)", snap.Plan);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 10. SnapshotAsync never throws — returns a valid snapshot regardless
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SnapshotAsync_NeverThrows()
    {
        var provider = BuildProvider("some-auth-type");

        var ex = await Record.ExceptionAsync(
            () => provider.SnapshotAsync(Now, CancellationToken.None));

        Assert.Null(ex);
    }
}
