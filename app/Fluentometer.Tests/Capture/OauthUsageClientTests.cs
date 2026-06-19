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

// ── Fake HTTP handler ─────────────────────────────────────────────────────────

/// <summary>
/// In-memory HttpMessageHandler for unit tests. No real network calls are made.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _respond = respond;

    public FakeHttpHandler(HttpStatusCode status, string body = "")
        : this(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        })
    { }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_respond(request));
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public class OauthUsageClientTests
{
    // Inlined from core/tests/fixtures/usage.sample.json — the real API schema with
    // sanitised values (42/61/30 — not the actual numbers). See:
    // summaries/2026-06-16-usage-api-schema-and-percent-scaling-bug.md
    private const string UsageSampleJson = """
        {
          "five_hour": {"utilization": 42.0, "resets_at": "2026-06-17T02:00:00.123456+00:00"},
          "seven_day": {"utilization": 61.0, "resets_at": "2026-06-18T04:00:00.654321+00:00"},
          "seven_day_opus": null,
          "seven_day_sonnet": {"utilization": 30.0, "resets_at": "2026-06-18T04:00:00.999000+00:00"},
          "seven_day_oauth_apps": null,
          "seven_day_cowork": null,
          "extra_usage": {
            "is_enabled": false,
            "monthly_limit": null,
            "used_credits": null,
            "utilization": null,
            "currency": null,
            "decimal_places": null,
            "disabled_reason": null,
            "daily": null,
            "weekly": null
          },
          "limits": [
            {
              "kind": "session",
              "group": "session",
              "percent": 42,
              "severity": "normal",
              "resets_at": "2026-06-17T02:00:00.123456+00:00",
              "scope": null,
              "is_active": false
            },
            {
              "kind": "weekly_all",
              "group": "weekly",
              "percent": 61,
              "severity": "normal",
              "resets_at": "2026-06-18T04:00:00.654321+00:00",
              "scope": null,
              "is_active": true
            },
            {
              "kind": "weekly_scoped",
              "group": "weekly",
              "percent": 30,
              "severity": "normal",
              "resets_at": "2026-06-18T04:00:00.999000+00:00",
              "scope": {"model": {"id": null, "display_name": "Sonnet"}, "surface": null},
              "is_active": false
            }
          ],
          "spend": {
            "used": {"amount_minor": 0, "currency": "USD", "exponent": 2},
            "limit": null,
            "percent": 0,
            "severity": "normal",
            "enabled": false,
            "disabled_reason": null
          }
        }
        """;

    private static (OauthUsageClient Client, List<HttpRequestMessage> Requests) MakeClient(
        HttpStatusCode status, string body = "", IDictionary<string, string>? responseHeaders = null)
    {
        var captured = new List<HttpRequestMessage>();
        var handler = new FakeHttpHandler(req =>
        {
            captured.Add(req);
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (responseHeaders is not null)
                foreach (var kv in responseHeaders)
                    resp.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            return resp;
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com") };
        return (new OauthUsageClient(httpClient), captured);
    }

    // ── Representative usage body → gauge list ────────────────────────────────

    [Fact]
    public async Task ParsesSampleBodyIntoThreeGaugesInOrder()
    {
        var (client, _) = MakeClient(HttpStatusCode.OK, UsageSampleJson);

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        var ok = Assert.IsType<UsageResult.Ok>(result);
        Assert.Equal(3, ok.Gauges.Count);

        var ids = ok.Gauges.Select(g => g.Id).ToList();
        Assert.Equal(new[] { "session", "weekly_all", "weekly_scoped" }, ids);
    }

    [Fact]
    public async Task AllUtilizationsAreInZeroToOneRange()
    {
        var (client, _) = MakeClient(HttpStatusCode.OK, UsageSampleJson);

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        var ok = Assert.IsType<UsageResult.Ok>(result);
        foreach (var g in ok.Gauges)
        {
            Assert.NotNull(g.Utilization);
            Assert.True(g.Utilization >= 0.0 && g.Utilization <= 1.0,
                $"Utilization out of range for gauge '{g.Id}': {g.Utilization}");
        }
    }

    [Fact]
    public async Task ScopedGaugeLabelIncludesModelDisplayName()
    {
        var (client, _) = MakeClient(HttpStatusCode.OK, UsageSampleJson);

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        var ok = Assert.IsType<UsageResult.Ok>(result);
        var scoped = ok.Gauges.Single(g => g.Id == "weekly_scoped");
        Assert.Equal("Claude Weekly (Sonnet)", scoped.Label);
    }

    // ── Percent-scaling war-story guard ───────────────────────────────────────
    //
    // percent is 0–100, NOT 0–1. A value of 51 must produce utilization ≈ 0.51,
    // NOT 1.0. See: summaries/2026-06-16-usage-api-schema-and-percent-scaling-bug.md

    [Fact]
    public async Task Percent51YieldsUtilizationApprox051NotOneDotZero()
    {
        const string body = """
            {
                "limits": [
                    {
                        "kind": "session",
                        "group": "session",
                        "percent": 51,
                        "severity": "normal",
                        "resets_at": "2026-06-17T02:00:00.993768+00:00",
                        "scope": null,
                        "is_active": false
                    }
                ]
            }
            """;
        var (client, _) = MakeClient(HttpStatusCode.OK, body);

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        var ok = Assert.IsType<UsageResult.Ok>(result);
        Assert.Single(ok.Gauges);
        var utilization = ok.Gauges[0].Utilization!.Value;
        Assert.True(Math.Abs(utilization - 0.51) < 1e-9,
            $"Expected utilization ~0.51 but got {utilization} — check percent/100 division");
        Assert.True(utilization < 1.0,
            $"Utilization must NOT be clamped to 1.0 for a 51% input; got {utilization}");
    }

    // ── RFC 3339 timestamp parsing ────────────────────────────────────────────

    [Fact]
    public async Task Rfc3339WithFractionalSecondsAndOffsetParsesCorrectly()
    {
        // The real API returns timestamps like "2026-06-17T01:49:59.993768+00:00"
        // (fractional seconds, explicit +00:00 offset). This must parse to the correct
        // Unix seconds, truncating fractional seconds.
        // 2026-06-17T01:49:59+00:00 = Unix 1781660999
        const string body = """
            {
                "limits": [
                    {
                        "kind": "session",
                        "group": "session",
                        "percent": 42,
                        "severity": "normal",
                        "resets_at": "2026-06-17T01:49:59.993768+00:00",
                        "scope": null
                    }
                ]
            }
            """;
        var (client, _) = MakeClient(HttpStatusCode.OK, body);

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        var ok = Assert.IsType<UsageResult.Ok>(result);
        Assert.Single(ok.Gauges);
        Assert.Equal(1_781_660_999L, ok.Gauges[0].ResetsAt);
    }

    [Fact]
    public async Task NullResetsAtProducesNullResetsAt()
    {
        const string body = """
            {"limits":[{"kind":"session","group":"session","percent":10,"severity":"normal","resets_at":null,"scope":null}]}
            """;
        var (client, _) = MakeClient(HttpStatusCode.OK, body);

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        var ok = Assert.IsType<UsageResult.Ok>(result);
        Assert.Null(ok.Gauges[0].ResetsAt);
    }

    // ── HTTP status mapping ───────────────────────────────────────────────────

    [Fact]
    public async Task Returns429WithRetryAfterFromHeader()
    {
        var (client, _) = MakeClient(HttpStatusCode.TooManyRequests, "",
            new Dictionary<string, string> { ["Retry-After"] = "190" });

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        var rateLimited = Assert.IsType<UsageResult.RateLimited>(result);
        Assert.Equal(190L, rateLimited.RetryAfterSecs);
    }

    [Fact]
    public async Task Returns429WithDefault180WhenRetryAfterAbsent()
    {
        var (client, _) = MakeClient(HttpStatusCode.TooManyRequests);

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        var rateLimited = Assert.IsType<UsageResult.RateLimited>(result);
        Assert.Equal(180L, rateLimited.RetryAfterSecs);
    }

    [Fact]
    public async Task Returns401AsUnauthorized()
    {
        var (client, _) = MakeClient(HttpStatusCode.Unauthorized);

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        Assert.IsType<UsageResult.Unauthorized>(result);
    }

    [Fact]
    public async Task Returns403AsUnauthorized()
    {
        var (client, _) = MakeClient(HttpStatusCode.Forbidden);

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        Assert.IsType<UsageResult.Unauthorized>(result);
    }

    [Fact]
    public async Task Returns500AsFailed()
    {
        var (client, _) = MakeClient(HttpStatusCode.InternalServerError, "Internal Server Error");

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        Assert.IsType<UsageResult.Failed>(result);
    }

    // ── Label rendering ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("session", "session", null, null, "Claude 5-hour")]
    [InlineData("weekly_all", "weekly", null, null, "Claude Weekly")]
    [InlineData("weekly_scoped", "weekly", "Sonnet", null, "Claude Weekly (Sonnet)")]
    [InlineData("weekly_scoped", "weekly", null, "has-scope", "Claude Weekly (scoped)")]
    public async Task LabelRenderingMatchesProviderRules(
        string kind, string group, string? modelDisplayName, string? scopePresenceMarker, string expectedLabel)
    {
        // Build the scope JSON fragment based on test parameters.
        string scopeJson;
        if (modelDisplayName is not null)
            scopeJson = $$$"""{"model":{"display_name":"{{{modelDisplayName}}}"}}""";
        else if (scopePresenceMarker is not null)
            scopeJson = """{"model":null}"""; // scope exists but no display_name
        else
            scopeJson = "null";

        var body = $$"""
            {
                "limits": [
                    {
                        "kind": "{{kind}}",
                        "group": "{{group}}",
                        "percent": 30,
                        "severity": "normal",
                        "resets_at": null,
                        "scope": {{scopeJson}}
                    }
                ]
            }
            """;
        var (client, _) = MakeClient(HttpStatusCode.OK, body);

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        var ok = Assert.IsType<UsageResult.Ok>(result);
        Assert.Single(ok.Gauges);
        Assert.Equal(expectedLabel, ok.Gauges[0].Label);
    }

    [Fact]
    public async Task UnknownGroupIsRenderedWithHumanizedFallbackLabel()
    {
        const string body = """
            {"limits":[{"kind":"some_future_kind","group":"some_future_group","percent":10,"severity":"normal","resets_at":null,"scope":null}]}
            """;
        // Unknown groups are no longer dropped: every limits[] entry becomes a gauge so a
        // future plan/limit type shows up automatically. The kind is humanized and
        // provider-prefixed: "some_future_kind" → "Claude Some Future Kind".
        var (client, _) = MakeClient(HttpStatusCode.OK, body);

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        var ok = Assert.IsType<UsageResult.Ok>(result);
        var gauge = Assert.Single(ok.Gauges);
        Assert.Equal("Claude Some Future Kind", gauge.Label);
    }

    [Fact]
    public async Task UnknownGroupWithScopedModelAppendsDisplayName()
    {
        const string body = """
            {"limits":[{"kind":"monthly_opus","group":"monthly","percent":10,"severity":"normal","resets_at":null,"scope":{"model":{"display_name":"Opus"}}}]}
            """;
        var (client, _) = MakeClient(HttpStatusCode.OK, body);

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        var ok = Assert.IsType<UsageResult.Ok>(result);
        var gauge = Assert.Single(ok.Gauges);
        Assert.Equal("Claude Monthly Opus (Opus)", gauge.Label);
    }

    // ── User-Agent header ─────────────────────────────────────────────────────

    [Fact]
    public async Task RequestCarriesCorrectUserAgentHeader()
    {
        var (client, captured) = MakeClient(HttpStatusCode.OK,
            """{"limits":[]}""");

        await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        Assert.Single(captured);
        var userAgent = captured[0].Headers.UserAgent.ToString();
        Assert.Equal($"claude-code/{OauthConstants.ClaudeCodeVersion}", userAgent);
    }

    // ── URL construction ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://api.anthropic.com")]
    [InlineData("https://api.anthropic.com/")]
    public async Task ConstructsCorrectUrlFromBaseWithOrWithoutTrailingSlash(string baseUrl)
    {
        Uri? actualRequestUri = null;
        var handler = new FakeHttpHandler(req =>
        {
            actualRequestUri = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"limits":[]}""", Encoding.UTF8, "application/json")
            };
        });
        var httpClient = new HttpClient(handler);
        var c = new OauthUsageClient(httpClient);

        await c.FetchAsync(baseUrl, "tok", CancellationToken.None);

        Assert.NotNull(actualRequestUri);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", actualRequestUri!.ToString());
    }

    // ── UsedLabel formatting ──────────────────────────────────────────────────

    [Theory]
    [InlineData(51.0, "51%")]
    [InlineData(51.4, "51%")]
    [InlineData(51.6, "52%")]
    [InlineData(0.0, "0%")]
    [InlineData(100.0, "100%")]
    public async Task UsedLabelIsRoundedPercent(double percent, string expectedUsedLabel)
    {
        var body = $$"""
            {"limits":[{"kind":"session","group":"session","percent":{{percent}},"severity":"normal","resets_at":null,"scope":null}]}
            """;
        var (client, _) = MakeClient(HttpStatusCode.OK, body);

        var result = await client.FetchAsync("https://api.anthropic.com", "tok", CancellationToken.None);

        var ok = Assert.IsType<UsageResult.Ok>(result);
        Assert.Equal(expectedUsedLabel, ok.Gauges[0].UsedLabel);
    }
}
