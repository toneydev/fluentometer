using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Capture;
using Fluentometer.Logic.Ipc;
using Xunit;

namespace Fluentometer.Tests.Capture;

internal sealed class FakeGeminiCredentialReader(GeminiCredentialResult result) : IGeminiCredentialReader
{
    public GeminiCredentialResult Read() => result;
}

internal sealed class FakeCloudCodeClient(CloudCodeResult result) : ICloudCodeUsageClient
{
    public string? LastAccessToken { get; private set; }
    public Task<CloudCodeResult> FetchAsync(string accessToken, CancellationToken ct)
    {
        LastAccessToken = accessToken;
        return Task.FromResult(result);
    }
}

public class GeminiProviderTests
{
    private const long Now = 1_700_000_000; // seconds
    private static GeminiCredentialResult OkCred(long expiresMs) =>
        new(GeminiCredentialStatus.Ok, new GeminiCredential(new RedactedString("tok"), expiresMs));

    private static readonly IReadOnlyList<Gauge> OneGauge =
        new[] { new Gauge("gemini_requests", "Gemini Requests", 0.24, "24%", Now + 3600, "daily limit") };

    // 1. Credential present + Ok quota → ok snapshot with gauges + plan
    [Fact]
    public async Task Snapshot_OkCredAndQuota_ReturnsOk()
    {
        var provider = new GeminiProvider(
            new FakeGeminiCredentialReader(OkCred(long.MaxValue)),
            new FakeCloudCodeClient(new CloudCodeResult.Ok(OneGauge, "Gemini (Free)")));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("gemini", snap.Provider);
        Assert.Equal("ok", snap.Health);
        Assert.Equal("oauth", snap.Source);
        Assert.Equal("Gemini (Free)", snap.Plan);
        Assert.Single(snap.Gauges);
    }

    // 2. Missing credential → needs-signin, empty gauges
    [Fact]
    public async Task Snapshot_NoCredential_ReturnsNeedsSignin()
    {
        var provider = new GeminiProvider(
            new FakeGeminiCredentialReader(new GeminiCredentialResult(GeminiCredentialStatus.NotFound, null)),
            new FakeCloudCodeClient(new CloudCodeResult.Ok(OneGauge, "Gemini")));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);
        Assert.Equal("needs-signin", snap.Health);
        Assert.Empty(snap.Gauges);
    }

    // 3. Parse error → error
    [Fact]
    public async Task Snapshot_ParseError_ReturnsError()
    {
        var provider = new GeminiProvider(
            new FakeGeminiCredentialReader(new GeminiCredentialResult(GeminiCredentialStatus.ParseError, null)),
            new FakeCloudCodeClient(new CloudCodeResult.Ok(OneGauge, "Gemini")));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);
        Assert.Equal("error", snap.Health);
        Assert.Empty(snap.Gauges);
    }

    // 4. Expired token → needs-signin, endpoint NOT called
    [Fact]
    public async Task Snapshot_ExpiredToken_ReturnsNeedsSignin_DoesNotCallClient()
    {
        var client = new FakeCloudCodeClient(new CloudCodeResult.Ok(OneGauge, "Gemini"));
        // expiry in the past relative to Now*1000
        var provider = new GeminiProvider(new FakeGeminiCredentialReader(OkCred(1L)), client);

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);
        Assert.Equal("needs-signin", snap.Health);
        Assert.Null(client.LastAccessToken); // never reached the client
    }

    // 5. Unauthorized from endpoint → needs-signin
    [Fact]
    public async Task Snapshot_Unauthorized_ReturnsNeedsSignin()
    {
        var provider = new GeminiProvider(
            new FakeGeminiCredentialReader(OkCred(long.MaxValue)),
            new FakeCloudCodeClient(new CloudCodeResult.Unauthorized()));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);
        Assert.Equal("needs-signin", snap.Health);
        Assert.Empty(snap.Gauges);
    }

    // 6. RateLimited → degraded, empty gauges (no fallback)
    [Fact]
    public async Task Snapshot_RateLimited_ReturnsDegradedEmpty()
    {
        var provider = new GeminiProvider(
            new FakeGeminiCredentialReader(OkCred(long.MaxValue)),
            new FakeCloudCodeClient(new CloudCodeResult.RateLimited(300)));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);
        Assert.Equal("degraded", snap.Health);
        Assert.Empty(snap.Gauges);
    }

    // 7. Failed → degraded, empty gauges
    [Fact]
    public async Task Snapshot_Failed_ReturnsDegradedEmpty()
    {
        var provider = new GeminiProvider(
            new FakeGeminiCredentialReader(OkCred(long.MaxValue)),
            new FakeCloudCodeClient(new CloudCodeResult.Failed("boom")));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);
        Assert.Equal("degraded", snap.Health);
        Assert.Empty(snap.Gauges);
    }

    // 8. MinPollInterval is 180s
    [Fact]
    public void MinPollInterval_Is180Seconds()
    {
        var provider = new GeminiProvider(
            new FakeGeminiCredentialReader(OkCred(long.MaxValue)),
            new FakeCloudCodeClient(new CloudCodeResult.Failed("x")));
        Assert.Equal(TimeSpan.FromSeconds(180), provider.MinPollInterval);
    }
}
