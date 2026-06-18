using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Capture;
using Fluentometer.Logic.Ipc;
using Xunit;

namespace Fluentometer.Tests.Capture;

// ── Fakes ──────────────────────────────────────────────────────────────────────

internal sealed class FakeCredentialReader(CredentialResult result) : IClaudeCredentialReader
{
    public CredentialResult Read() => result;
}

internal sealed class FakeOauthClient(UsageResult result) : IOauthUsageClient
{
    public string? LastAccessToken { get; private set; }

    public Task<UsageResult> FetchAsync(string baseUrl, string accessToken, CancellationToken ct)
    {
        LastAccessToken = accessToken;
        return Task.FromResult(result);
    }
}

internal sealed class FakeJsonlReader(IReadOnlyList<UsageEvent>? events = null) : IJsonlReader
{
    public IReadOnlyList<UsageEvent> CollectEvents(string projectsDir)
        => events ?? Array.Empty<UsageEvent>();
}

// ── Helpers ────────────────────────────────────────────────────────────────────

internal static class ProviderTestHelpers
{
    private const long FarFutureMs = 9_999_999_999_999L; // never expires

    public static ClaudeCredential MakeCred(
        string accessToken = "tok",
        string? subscriptionType = "max",
        long? expiresAtMs = null)
        => new ClaudeCredential(
            new RedactedString(accessToken),
            null,
            expiresAtMs ?? FarFutureMs,
            subscriptionType);

    public static CredentialResult Ok(ClaudeCredential cred) =>
        new CredentialResult(CredentialStatus.Ok, cred);

    public static readonly CredentialResult NotFound =
        new CredentialResult(CredentialStatus.NotFound, null);

    public static readonly CredentialResult ParseError =
        new CredentialResult(CredentialStatus.ParseError, null);

    public static UsageResult.Ok EmptyOk() =>
        new UsageResult.Ok(Array.Empty<Gauge>());

    public static ClaudeProvider MakeProvider(
        CredentialResult credResult,
        UsageResult oauthResult,
        IReadOnlyList<UsageEvent>? jsonlEvents = null)
    {
        return new ClaudeProvider(
            "https://api.anthropic.com",
            new FakeCredentialReader(credResult),
            new FakeOauthClient(oauthResult),
            new FakeJsonlReader(jsonlEvents));
    }
}

// ── Tests ──────────────────────────────────────────────────────────────────────

public class ClaudeProviderTests
{
    private const long Now = 1_700_000_000L;

    // --- Health: credential not found → needs-signin ---

    [Fact]
    public async Task CredentialNotFound_ReturnsNeedsSignin()
    {
        var provider = ProviderTestHelpers.MakeProvider(
            ProviderTestHelpers.NotFound,
            ProviderTestHelpers.EmptyOk());

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("oauth", snap.Source);
        Assert.Equal("needs-signin", snap.Health);
        Assert.Equal("Not signed in", snap.Plan);
        Assert.Empty(snap.Gauges);
        Assert.Equal("claude", snap.Provider);
        Assert.Equal(Now, snap.CapturedAt);
    }

    // --- Health: credential parse error → error ---

    [Fact]
    public async Task CredentialParseError_ReturnsError()
    {
        var provider = ProviderTestHelpers.MakeProvider(
            ProviderTestHelpers.ParseError,
            ProviderTestHelpers.EmptyOk());

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("oauth", snap.Source);
        Assert.Equal("error", snap.Health);
        Assert.Equal("Unknown plan", snap.Plan);
        Assert.Empty(snap.Gauges);
    }

    // --- Health: expired credential → needs-signin ---

    [Fact]
    public async Task ExpiredCredential_ReturnsNeedsSignin()
    {
        // expiresAtMs = 1 (already expired for nowUnix = 1_700_000_000)
        var cred = ProviderTestHelpers.MakeCred(expiresAtMs: 1L);
        var provider = ProviderTestHelpers.MakeProvider(
            ProviderTestHelpers.Ok(cred),
            ProviderTestHelpers.EmptyOk());

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("oauth", snap.Source);
        Assert.Equal("needs-signin", snap.Health);
        Assert.Equal("Session expired", snap.Plan);
        Assert.Empty(snap.Gauges);
    }

    // --- Health: oauth returns Unauthorized → needs-signin ---

    [Fact]
    public async Task OauthUnauthorized_ReturnsNeedsSignin()
    {
        var cred = ProviderTestHelpers.MakeCred();
        var provider = ProviderTestHelpers.MakeProvider(
            ProviderTestHelpers.Ok(cred),
            new UsageResult.Unauthorized());

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("oauth", snap.Source);
        Assert.Equal("needs-signin", snap.Health);
        Assert.Equal("Session expired", snap.Plan);
        Assert.Empty(snap.Gauges);
    }

    // --- Health: oauth ok → health "ok", source "oauth" ---

    [Fact]
    public async Task OauthOk_ReturnsOkSnapshot()
    {
        var gauges = new[]
        {
            new Gauge("session", "Claude 5-hour", 0.42, "42%", null, "normal"),
        };
        var cred = ProviderTestHelpers.MakeCred(subscriptionType: "max");
        var provider = ProviderTestHelpers.MakeProvider(
            ProviderTestHelpers.Ok(cred),
            new UsageResult.Ok(gauges));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("oauth", snap.Source);
        Assert.Equal("ok", snap.Health);
        Assert.Single(snap.Gauges);
        Assert.Equal("session", snap.Gauges[0].Id);
    }

    // --- Plan derivation: "max" (case-insensitive) → "Max" ---

    [Theory]
    [InlineData("max", "Max")]
    [InlineData("MAX", "Max")]
    [InlineData("Max", "Max")]
    [InlineData("pro", "pro")]
    [InlineData("custom-plan", "custom-plan")]
    [InlineData(null, "Claude")]
    [InlineData("", "Claude")]
    public async Task PlanFromSubscription_MapsCorrectly(string? subscriptionType, string expectedPlan)
    {
        var cred = ProviderTestHelpers.MakeCred(subscriptionType: subscriptionType);
        var provider = ProviderTestHelpers.MakeProvider(
            ProviderTestHelpers.Ok(cred),
            ProviderTestHelpers.EmptyOk());

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal(expectedPlan, snap.Plan);
    }

    // --- PlanFromSubscription static method ---

    [Theory]
    [InlineData("max", "Max")]
    [InlineData("MAX", "Max")]
    [InlineData("Max", "Max")]
    [InlineData(null, "Claude")]
    [InlineData("", "Claude")]
    [InlineData("pro", "pro")]
    public void PlanFromSubscriptionStaticMethod(string? input, string expected)
    {
        Assert.Equal(expected, ClaudeProvider.PlanFromSubscription(input));
    }

    // --- RateLimited degrades to jsonl with two local-estimate gauges ---

    [Fact]
    public async Task OauthRateLimited_DegradesToJsonlWithTwoGauges()
    {
        var cred = ProviderTestHelpers.MakeCred(subscriptionType: "max");
        var provider = ProviderTestHelpers.MakeProvider(
            ProviderTestHelpers.Ok(cred),
            new UsageResult.RateLimited(190L));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("jsonl", snap.Source);
        Assert.Equal("degraded", snap.Health);
        Assert.Equal("Max", snap.Plan);  // plan still derived from credential
        Assert.Equal(2, snap.Gauges.Count);
        Assert.Equal("session", snap.Gauges[0].Id);
        Assert.Equal("Claude 5-hour", snap.Gauges[0].Label);
        Assert.Null(snap.Gauges[0].Utilization);
        Assert.Equal("local estimate", snap.Gauges[0].LimitLabel);
        Assert.Equal("weekly_all", snap.Gauges[1].Id);
        Assert.Equal("Claude Weekly", snap.Gauges[1].Label);
        Assert.Null(snap.Gauges[1].Utilization);
        Assert.Equal("local estimate", snap.Gauges[1].LimitLabel);
    }

    // --- Failed also degrades to jsonl ---

    [Fact]
    public async Task OauthFailed_DegradesToJsonl()
    {
        var cred = ProviderTestHelpers.MakeCred();
        var provider = ProviderTestHelpers.MakeProvider(
            ProviderTestHelpers.Ok(cred),
            new UsageResult.Failed("Network error"));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("jsonl", snap.Source);
        Assert.Equal("degraded", snap.Health);
    }

    // --- JSONL estimate: token counts show up in UsedLabel ---

    [Fact]
    public async Task OauthRateLimited_JsonlGaugesCarryTokenCounts()
    {
        // One JSONL event at exactly Now with 500 tokens — both windows include it.
        var events = new[] { new UsageEvent(Now, 500L) };
        var cred = ProviderTestHelpers.MakeCred();
        var provider = ProviderTestHelpers.MakeProvider(
            ProviderTestHelpers.Ok(cred),
            new UsageResult.RateLimited(180L),
            events);

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("jsonl", snap.Source);
        Assert.Equal("~500 tokens", snap.Gauges[0].UsedLabel);
        Assert.Equal("~500 tokens", snap.Gauges[1].UsedLabel);
    }

    // --- AccessToken is passed to OAuth client (not logged, but verifiable by fake) ---

    [Fact]
    public async Task AccessToken_IsPassedToOauthClient()
    {
        var cred = ProviderTestHelpers.MakeCred(accessToken: "my-secret-token");
        var fakeOauth = new FakeOauthClient(ProviderTestHelpers.EmptyOk());
        var provider = new ClaudeProvider(
            "https://api.anthropic.com",
            new FakeCredentialReader(ProviderTestHelpers.Ok(cred)),
            fakeOauth,
            new FakeJsonlReader());

        await provider.SnapshotAsync(Now, CancellationToken.None);

        // The raw token must reach the OAuth client exactly as-is.
        Assert.Equal("my-secret-token", fakeOauth.LastAccessToken);
    }
}
