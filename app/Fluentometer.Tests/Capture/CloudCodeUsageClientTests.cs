// app/Fluentometer.Tests/Capture/CloudCodeUsageClientTests.cs
// Reuses FakeHttpHandler from the shared Fluentometer.Tests.Capture namespace
// (internal to the test project — same one WhamUsageClientTests uses).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Capture;
using Fluentometer.Logic.Ipc;
using Xunit;

namespace Fluentometer.Tests.Capture;

public class CloudCodeUsageClientTests
{
    private const string LoadJson = """
        { "currentTier": { "id": "free-tier" }, "cloudaicompanionProject": "proj-123" }
        """;

    private const string QuotaJson = """
        {
          "buckets": [
            { "remainingFraction": 0.76175, "resetTime": "2025-12-10T22:19:52Z",
              "modelId": "gemini-3-pro-preview", "tokenType": "REQUESTS" }
          ]
        }
        """;

    // Routes by URL: loadCodeAssist vs retrieveUserQuota.
    private static FakeHttpHandler RoutingHandler(
        HttpStatusCode loadStatus, string loadBody,
        HttpStatusCode quotaStatus, string quotaBody,
        Action<HttpRequestMessage>? capture = null)
    {
        return new FakeHttpHandler(req =>
        {
            capture?.Invoke(req);
            var url = req.RequestUri!.ToString();
            if (url.EndsWith(":loadCodeAssist", StringComparison.Ordinal))
                return new HttpResponseMessage(loadStatus)
                { Content = new StringContent(loadBody, Encoding.UTF8, "application/json") };
            if (url.EndsWith(":retrieveUserQuota", StringComparison.Ordinal))
                return new HttpResponseMessage(quotaStatus)
                { Content = new StringContent(quotaBody, Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static CloudCodeUsageClient Build(FakeHttpHandler handler) =>
        new CloudCodeUsageClient(new HttpClient(handler));

    // 1. Happy path → Ok with one gauge, prefixed label, plan from tier
    [Fact]
    public async Task Fetch_Valid_ReturnsOkGaugeAndPlan()
    {
        var client = Build(RoutingHandler(HttpStatusCode.OK, LoadJson, HttpStatusCode.OK, QuotaJson));
        var result = await client.FetchAsync("tok", CancellationToken.None);

        var ok = Assert.IsType<CloudCodeResult.Ok>(result);
        Assert.Single(ok.Gauges);
        Assert.Equal("Gemini Requests", ok.Gauges[0].Label);
        Assert.Equal("Gemini (Free)", ok.Plan);
    }

    // 2. CRITICAL — remainingFraction is REMAINING; Utilization is USED (inverted)
    // $100-rule guard: Gemini gauge math is INVERTED vs Claude/ChatGPT.
    // remainingFraction 0.76175 → used 0.23825 (NOT 0.76, NOT 76.175)
    // DO NOT merge this into a cross-provider Theory — the inversion is provider-specific.
    [Fact]
    public async Task Fetch_RemainingFraction_IsInvertedToUsedUtilization()
    {
        var client = Build(RoutingHandler(HttpStatusCode.OK, LoadJson, HttpStatusCode.OK, QuotaJson));
        var result = await client.FetchAsync("tok", CancellationToken.None);

        var ok = Assert.IsType<CloudCodeResult.Ok>(result);
        // remainingFraction 0.76175 → used 0.23825 (NOT 0.76, NOT 76.175)
        Assert.InRange(ok.Gauges[0].Utilization!.Value, 0.23, 0.25);
    }

    // 3. Bucket with null remainingFraction is skipped (issue #27363)
    [Fact]
    public async Task Fetch_BucketWithoutRemainingFraction_IsSkipped()
    {
        const string q = """{"buckets":[{"resetTime":"2025-12-10T22:19:52Z","tokenType":"REQUESTS"}]}""";
        var client = Build(RoutingHandler(HttpStatusCode.OK, LoadJson, HttpStatusCode.OK, q));
        var result = await client.FetchAsync("tok", CancellationToken.None);

        var ok = Assert.IsType<CloudCodeResult.Ok>(result);
        Assert.Empty(ok.Gauges);
    }

    // 4. loadCodeAssist failure is non-fatal — quota still drives the result; plan falls back
    [Fact]
    public async Task Fetch_LoadCodeAssistFails_QuotaStillSucceeds_PlanDefault()
    {
        var client = Build(RoutingHandler(
            HttpStatusCode.InternalServerError, "", HttpStatusCode.OK, QuotaJson));
        var result = await client.FetchAsync("tok", CancellationToken.None);

        var ok = Assert.IsType<CloudCodeResult.Ok>(result);
        Assert.Single(ok.Gauges);
        Assert.Equal("Gemini", ok.Plan); // tier unknown → default
    }

    // 5+6. quota 401 / 403 → Unauthorized
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Fetch_Quota401Or403_ReturnsUnauthorized(HttpStatusCode status)
    {
        var client = Build(RoutingHandler(HttpStatusCode.OK, LoadJson, status, ""));
        var result = await client.FetchAsync("tok", CancellationToken.None);
        Assert.IsType<CloudCodeResult.Unauthorized>(result);
    }

    // 7. quota 429 with Retry-After → RateLimited(parsed)
    // HARD GUARDRAIL: this test uses the URL-routing handler (Gemini's two-RPC design)
    // and MUST remain a separate Fact — do NOT fold into a Theory with test 8.
    [Fact]
    public async Task Fetch_Quota429_HonoursRetryAfter()
    {
        var handler = new FakeHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith(":loadCodeAssist", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(LoadJson, Encoding.UTF8, "application/json") };
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.Add("Retry-After", "300");
            return resp;
        });
        var result = await Build(handler).FetchAsync("tok", CancellationToken.None);
        var rl = Assert.IsType<CloudCodeResult.RateLimited>(result);
        Assert.Equal(300L, rl.RetryAfterSecs);
    }

    // 8. quota 429 without Retry-After → defaults 180
    [Fact]
    public async Task Fetch_Quota429_NoHeader_Defaults180()
    {
        var client = Build(RoutingHandler(HttpStatusCode.OK, LoadJson, HttpStatusCode.TooManyRequests, ""));
        var result = await client.FetchAsync("tok", CancellationToken.None);
        var rl = Assert.IsType<CloudCodeResult.RateLimited>(result);
        Assert.Equal(180L, rl.RetryAfterSecs);
    }

    // 9. quota 500 → Failed
    [Fact]
    public async Task Fetch_Quota500_ReturnsFailed()
    {
        var client = Build(RoutingHandler(HttpStatusCode.OK, LoadJson, HttpStatusCode.InternalServerError, ""));
        var result = await client.FetchAsync("tok", CancellationToken.None);
        Assert.IsType<CloudCodeResult.Failed>(result);
    }

    // 10. quota malformed JSON → Failed (defensive)
    [Fact]
    public async Task Fetch_QuotaMalformed_ReturnsFailed()
    {
        var client = Build(RoutingHandler(HttpStatusCode.OK, LoadJson, HttpStatusCode.OK, "not-json{{"));
        var result = await client.FetchAsync("tok", CancellationToken.None);
        Assert.IsType<CloudCodeResult.Failed>(result);
    }

    // 11. Bearer + User-Agent headers present on the quota request
    [Fact]
    public async Task Fetch_SetsBearerAndUserAgent()
    {
        HttpRequestMessage? quotaReq = null;
        var handler = RoutingHandler(
            HttpStatusCode.OK, LoadJson, HttpStatusCode.OK, QuotaJson,
            capture: req =>
            {
                if (req.RequestUri!.ToString().EndsWith(":retrieveUserQuota", StringComparison.Ordinal))
                    quotaReq = req;
            });
        await Build(handler).FetchAsync("mytoken", CancellationToken.None);

        Assert.NotNull(quotaReq);
        Assert.Equal("Bearer mytoken", quotaReq!.Headers.Authorization?.ToString());
        Assert.Contains("GeminiCLI", quotaReq.Headers.UserAgent.ToString());
    }

    // 12. project id from loadCodeAssist is sent in the quota body; absent → {} fallback still works
    [Fact]
    public async Task Fetch_NoProject_StillSucceedsWithEmptyBody()
    {
        const string loadNoProject = """{ "currentTier": { "id": "free-tier" } }""";
        var client = Build(RoutingHandler(HttpStatusCode.OK, loadNoProject, HttpStatusCode.OK, QuotaJson));
        var result = await client.FetchAsync("tok", CancellationToken.None);
        var ok = Assert.IsType<CloudCodeResult.Ok>(result);
        Assert.Single(ok.Gauges);
    }

    // 13. Cancellation propagates
    [Fact]
    public async Task Fetch_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = Build(RoutingHandler(HttpStatusCode.OK, LoadJson, HttpStatusCode.OK, QuotaJson));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.FetchAsync("tok", cts.Token));
    }

    // 14. PlanFromTier is case-insensitive on currentTier ($100 rule — locks the fix)
    [Fact]
    public void PlanFromTier_IsCaseInsensitiveOnCurrentTier()
    {
        Assert.Equal("Gemini (Free)", CloudCodeUsageClient.PlanFromTier("Free-Tier", null));
        Assert.Equal("Gemini (Standard)", CloudCodeUsageClient.PlanFromTier("STANDARD-TIER", null));
        Assert.Equal("Gemini (Paid)", CloudCodeUsageClient.PlanFromTier("free-tier", "Standard-Tier"));
    }
}
