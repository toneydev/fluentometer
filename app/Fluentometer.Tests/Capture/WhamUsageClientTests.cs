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

public class WhamUsageClientTests
{
    private const string ValidWhamJson = """
        {
          "primary":   { "used_percent": 42, "window_minutes": 300, "resets_at": 1700010000 },
          "secondary": { "used_percent": 61, "window_minutes": 10080, "resets_at": 1700100000 }
        }
        """;

    private static WhamUsageClient Build(HttpStatusCode status, string body = "")
    {
        var handler = new FakeHttpHandler(status, body);
        return new WhamUsageClient(new HttpClient(handler));
    }

    private static WhamUsageClient BuildWithHandler(FakeHttpHandler handler)
        => new WhamUsageClient(new HttpClient(handler));

    // 1. 200 with valid body → Ok with two gauges, labels provider-prefixed
    [Fact]
    public async Task Fetch_200Valid_ReturnsTwoGaugesWithPrefixedLabels()
    {
        var client = Build(HttpStatusCode.OK, ValidWhamJson);
        var result = await client.FetchAsync(
            "https://chatgpt.com/backend-api", "tok", "acct", CancellationToken.None);

        var ok = Assert.IsType<WhamResult.Ok>(result);
        Assert.Equal(2, ok.Gauges.Count);
        Assert.Contains(ok.Gauges, g => g.Label == "ChatGPT 5-hour");
        Assert.Contains(ok.Gauges, g => g.Label == "ChatGPT Weekly");
    }

    // 2. used_percent 0-100 → Utilization 0.0-1.0 (NOT 0-100 raw)
    [Fact]
    public async Task Fetch_200_UsedPercentDividedBy100()
    {
        var client = Build(HttpStatusCode.OK, ValidWhamJson);
        var result = await client.FetchAsync(
            "https://chatgpt.com/backend-api", "tok", "acct", CancellationToken.None);

        var ok = Assert.IsType<WhamResult.Ok>(result);
        var fiveHour = ok.Gauges.First(g => g.Label == "ChatGPT 5-hour");
        // 42 / 100.0 = 0.42, not 42.0
        Assert.InRange(fiveHour.Utilization!.Value, 0.41, 0.43);
    }

    // 3. 200 with only primary (secondary null) → one gauge only
    [Fact]
    public async Task Fetch_200_NullSecondary_YieldsOneGauge()
    {
        const string json = """{"primary": {"used_percent": 10, "window_minutes": 300, "resets_at": 0}, "secondary": null}""";
        var client = Build(HttpStatusCode.OK, json);
        var result = await client.FetchAsync(
            "https://chatgpt.com/backend-api", "tok", "acct", CancellationToken.None);

        var ok = Assert.IsType<WhamResult.Ok>(result);
        Assert.Single(ok.Gauges);
        Assert.Equal("ChatGPT 5-hour", ok.Gauges[0].Label);
    }

    // 4. 401 → Unauthorized
    [Fact]
    public async Task Fetch_401_ReturnsUnauthorized()
    {
        var client = Build(HttpStatusCode.Unauthorized);
        var result = await client.FetchAsync(
            "https://chatgpt.com/backend-api", "tok", "acct", CancellationToken.None);
        Assert.IsType<WhamResult.Unauthorized>(result);
    }

    // 5. 403 → Unauthorized (same treatment — needs-signin)
    [Fact]
    public async Task Fetch_403_ReturnsUnauthorized()
    {
        var client = Build(HttpStatusCode.Forbidden);
        var result = await client.FetchAsync(
            "https://chatgpt.com/backend-api", "tok", "acct", CancellationToken.None);
        Assert.IsType<WhamResult.Unauthorized>(result);
    }

    // 6. 429 with Retry-After → RateLimited with parsed value
    [Fact]
    public async Task Fetch_429_HonoursRetryAfterHeader()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.Add("Retry-After", "300");
            return resp;
        });
        var client = BuildWithHandler(handler);
        var result = await client.FetchAsync(
            "https://chatgpt.com/backend-api", "tok", "acct", CancellationToken.None);

        var rl = Assert.IsType<WhamResult.RateLimited>(result);
        Assert.Equal(300L, rl.RetryAfterSecs);
    }

    // 7. 429 without Retry-After → RateLimited defaulting to 180
    [Fact]
    public async Task Fetch_429_NoRetryAfterHeader_Defaults180()
    {
        var client = Build(HttpStatusCode.TooManyRequests);
        var result = await client.FetchAsync(
            "https://chatgpt.com/backend-api", "tok", "acct", CancellationToken.None);

        var rl = Assert.IsType<WhamResult.RateLimited>(result);
        Assert.Equal(180L, rl.RetryAfterSecs);
    }

    // 8. 500 → Failed
    [Fact]
    public async Task Fetch_500_ReturnsFailed()
    {
        var client = Build(HttpStatusCode.InternalServerError);
        var result = await client.FetchAsync(
            "https://chatgpt.com/backend-api", "tok", "acct", CancellationToken.None);
        Assert.IsType<WhamResult.Failed>(result);
    }

    // 9. Empty/malformed body → Failed (defensive parse)
    [Fact]
    public async Task Fetch_200_MalformedJson_ReturnsFailed()
    {
        var client = Build(HttpStatusCode.OK, "not-json{{");
        var result = await client.FetchAsync(
            "https://chatgpt.com/backend-api", "tok", "acct", CancellationToken.None);
        Assert.IsType<WhamResult.Failed>(result);
    }

    // 10. Request carries correct headers
    [Fact]
    public async Task Fetch_SetsAuthorizationAndAccountIdHeaders()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidWhamJson, Encoding.UTF8, "application/json"),
            };
        });
        var client = BuildWithHandler(handler);
        await client.FetchAsync("https://chatgpt.com/backend-api", "mytoken", "myacct", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("Bearer mytoken", captured!.Headers.Authorization?.ToString());
        Assert.True(captured.Headers.Contains("ChatGPT-Account-Id"));
        Assert.Contains("myacct", captured.Headers.GetValues("ChatGPT-Account-Id"));
        Assert.Contains("codex_cli_rs", captured!.Headers.UserAgent.ToString());
    }

    // 11. Cancellation propagates
    [Fact]
    public async Task Fetch_Cancelled_ThrowsOperationCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = Build(HttpStatusCode.OK, ValidWhamJson);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.FetchAsync("https://chatgpt.com/backend-api", "tok", "acct", cts.Token));
    }
}
