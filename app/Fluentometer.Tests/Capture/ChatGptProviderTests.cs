using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Capture;
using Fluentometer.Logic.Ipc;
using Xunit;

namespace Fluentometer.Tests.Capture;

// ── Fakes ─────────────────────────────────────────────────────────────────────

internal sealed class FakeCodexCredentialReader(CodexCredentialResult result) : ICodexCredentialReader
{
    public CodexCredentialResult Read() => result;
}

internal sealed class FakeWhamClient(WhamResult result) : IWhamUsageClient
{
    public string? LastAccessToken { get; private set; }
    public string? LastAccountId { get; private set; }

    public Task<WhamResult> FetchAsync(string baseUrl, string accessToken, string accountId, CancellationToken ct)
    {
        LastAccessToken = accessToken;
        LastAccountId = accountId;
        return Task.FromResult(result);
    }
}

// ── Helpers ────────────────────────────────────────────────────────────────────

internal static class CodexTestHelpers
{
    public static CodexCredential MakeCred(
        string accessToken = "tok",
        string accountId = "acct",
        string? planType = "plus")
        => new CodexCredential(
            new RedactedString(accessToken),
            new RedactedString(accountId),
            planType);

    public static CodexCredentialResult Ok(CodexCredential cred) =>
        new CodexCredentialResult(CodexCredentialStatus.Ok, cred);

    public static readonly CodexCredentialResult NotFound =
        new CodexCredentialResult(CodexCredentialStatus.NotFound, null);

    public static readonly CodexCredentialResult ParseError =
        new CodexCredentialResult(CodexCredentialStatus.ParseError, null);

    public static WhamResult.Ok TwoGauges() => new WhamResult.Ok(new List<Gauge>
    {
        new("chatgpt_primary", "ChatGPT 5-hour", 0.42, "42%", null, "subscription limit"),
        new("chatgpt_secondary", "ChatGPT Weekly", 0.61, "61%", null, "subscription limit"),
    });
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public class ChatGptProviderTests
{
    private const long Now = 1_700_000_000L;

    // 1. Ok credentials + Ok wham → ok snapshot with two gauges
    [Fact]
    public async Task SnapshotAsync_OkCredentialsAndOkWham_ReturnsOkWithGauges()
    {
        var cred = CodexTestHelpers.MakeCred();
        var provider = new ChatGptProvider(
            new FakeCodexCredentialReader(CodexTestHelpers.Ok(cred)),
            new FakeWhamClient(CodexTestHelpers.TwoGauges()));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("ok", snap.Health);
        Assert.Equal("oauth", snap.Source);
        Assert.Equal("chatgpt", snap.Provider);
        Assert.Equal(2, snap.Gauges.Count);
    }

    // 2. Plan label: "plus" → "ChatGPT Plus"
    [Fact]
    public async Task SnapshotAsync_PlusPlan_ReturnsCorrectLabel()
    {
        var cred = CodexTestHelpers.MakeCred(planType: "plus");
        var provider = new ChatGptProvider(
            new FakeCodexCredentialReader(CodexTestHelpers.Ok(cred)),
            new FakeWhamClient(CodexTestHelpers.TwoGauges()));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("ChatGPT Plus", snap.Plan);
    }

    // 3. Credential NotFound → needs-signin, empty gauges
    [Fact]
    public async Task SnapshotAsync_CredentialNotFound_ReturnsNeedsSignin()
    {
        var provider = new ChatGptProvider(
            new FakeCodexCredentialReader(CodexTestHelpers.NotFound),
            new FakeWhamClient(CodexTestHelpers.TwoGauges()));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("needs-signin", snap.Health);
        Assert.Empty(snap.Gauges);
    }

    // 4. Credential ParseError → error, empty gauges
    [Fact]
    public async Task SnapshotAsync_CredentialParseError_ReturnsError()
    {
        var provider = new ChatGptProvider(
            new FakeCodexCredentialReader(CodexTestHelpers.ParseError),
            new FakeWhamClient(CodexTestHelpers.TwoGauges()));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("error", snap.Health);
        Assert.Empty(snap.Gauges);
    }

    // 5. Wham returns Unauthorized → needs-signin (P3-A: stale JWT)
    [Fact]
    public async Task SnapshotAsync_WhamUnauthorized_ReturnsNeedsSignin()
    {
        var cred = CodexTestHelpers.MakeCred();
        var provider = new ChatGptProvider(
            new FakeCodexCredentialReader(CodexTestHelpers.Ok(cred)),
            new FakeWhamClient(new WhamResult.Unauthorized()));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("needs-signin", snap.Health);
        Assert.Empty(snap.Gauges);
    }

    // 6. Wham returns RateLimited → degraded, empty gauges (P2-B: no fallback)
    [Fact]
    public async Task SnapshotAsync_WhamRateLimited_ReturnsDegraded_EmptyGauges()
    {
        var cred = CodexTestHelpers.MakeCred();
        var provider = new ChatGptProvider(
            new FakeCodexCredentialReader(CodexTestHelpers.Ok(cred)),
            new FakeWhamClient(new WhamResult.RateLimited(300)));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("degraded", snap.Health);
        Assert.NotNull(snap.Gauges); // must be empty, NOT null (P2-B)
        Assert.Empty(snap.Gauges);
    }

    // 7. Wham returns Failed → degraded, empty gauges (P2-B: no fallback)
    [Fact]
    public async Task SnapshotAsync_WhamFailed_ReturnsDegraded_EmptyGauges()
    {
        var cred = CodexTestHelpers.MakeCred();
        var provider = new ChatGptProvider(
            new FakeCodexCredentialReader(CodexTestHelpers.Ok(cred)),
            new FakeWhamClient(new WhamResult.Failed("HTTP 503")));

        var snap = await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("degraded", snap.Health);
        Assert.Empty(snap.Gauges);
    }

    // 8. P1-C: access_token Expose() is called — credential is passed to wham client
    [Fact]
    public async Task SnapshotAsync_PassesExposedTokenToWhamClient()
    {
        var cred = CodexTestHelpers.MakeCred(accessToken: "my-jwt", accountId: "my-acct");
        var whamClient = new FakeWhamClient(CodexTestHelpers.TwoGauges());
        var provider = new ChatGptProvider(
            new FakeCodexCredentialReader(CodexTestHelpers.Ok(cred)),
            whamClient);

        await provider.SnapshotAsync(Now, CancellationToken.None);

        Assert.Equal("my-jwt", whamClient.LastAccessToken);
        Assert.Equal("my-acct", whamClient.LastAccountId);
    }

    // 9. MinPollInterval is at least 180 seconds
    [Fact]
    public void MinPollInterval_IsAtLeast180Seconds()
    {
        var provider = new ChatGptProvider(
            new FakeCodexCredentialReader(CodexTestHelpers.NotFound),
            new FakeWhamClient(new WhamResult.Failed("n/a")));
        Assert.True(provider.MinPollInterval >= TimeSpan.FromSeconds(180));
    }

    // 10. ProviderId is "chatgpt"
    [Fact]
    public void ProviderId_IsExactlyLowercaseChatgpt()
    {
        var provider = new ChatGptProvider(
            new FakeCodexCredentialReader(CodexTestHelpers.NotFound),
            new FakeWhamClient(new WhamResult.Failed("n/a")));
        Assert.Equal("chatgpt", provider.ProviderId);
    }

}
