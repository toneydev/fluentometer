using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Ipc;

namespace Fluentometer.Logic.Capture;

// ── Version constant ──────────────────────────────────────────────────────────

/// <summary>
/// Constants shared by the OAuth usage client and its tests.
/// </summary>
public static class OauthConstants
{
    /// <summary>
    /// Installed Claude Code version — used as the User-Agent on all api.anthropic.com calls.
    /// REQUIRED by the endpoint; empirically tied to the installed claude-code binary version.
    /// Verified: <c>claude --version</c> returns 2.1.179 on the development machine.
    /// Update when the Claude Code binary is upgraded if the endpoint begins rejecting older values.
    /// </summary>
    public const string ClaudeCodeVersion = "2.1.179";
}

// ── Result discriminated union ────────────────────────────────────────────────

/// <summary>
/// The result of a single call to the <c>/api/oauth/usage</c> endpoint.
/// <para>
/// <c>plan</c> is deliberately absent: it is derived from the credential's
/// <c>subscriptionType</c>, not from the usage response body (the API no longer
/// returns a top-level <c>plan</c> field).
/// </para>
/// </summary>
public abstract record UsageResult
{
    public sealed record Ok(IReadOnlyList<Gauge> Gauges) : UsageResult;
    public sealed record Unauthorized : UsageResult;
    public sealed record RateLimited(long RetryAfterSecs) : UsageResult;
    public sealed record Failed(string Reason) : UsageResult;

    // Private constructor — prevents external code from subclassing UsageResult.
    // Nested sealed records in C# can inherit from a base with a private constructor
    // because they are declared in the same type scope.
    private UsageResult() { }
}

// ── Interface ─────────────────────────────────────────────────────────────────

public interface IOauthUsageClient
{
    Task<UsageResult> FetchAsync(string baseUrl, string accessToken, CancellationToken ct);
}

// ── Implementation ────────────────────────────────────────────────────────────

/// <summary>
/// Fetches Claude usage data from the OAuth <c>/api/oauth/usage</c> endpoint.
/// <para>
/// TLS verification is always enabled — the <see cref="HttpClient"/> is injected
/// by the caller, which must NOT configure a custom
/// <c>ServerCertificateCustomValidationCallback</c>.
/// </para>
/// <para>
/// Tokens are never logged. The <paramref name="accessToken"/> parameter is used
/// only for the <c>Authorization: Bearer</c> header and is not stored as a field.
/// </para>
/// </summary>
public sealed class OauthUsageClient : IOauthUsageClient
{
    private readonly HttpClient _http;

    public OauthUsageClient(HttpClient httpClient) => _http = httpClient;

    /// <inheritdoc/>
    public async Task<UsageResult> FetchAsync(string baseUrl, string accessToken, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/api/oauth/usage";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // User-Agent is REQUIRED by the endpoint — see OauthConstants.ClaudeCodeVersion.
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd($"claude-code/{OauthConstants.ClaudeCodeVersion}");
        // SECURITY: do not log accessToken; it is used only in the Authorization header.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            // Timeout must be enforced on the HttpClient itself (or by the ct) — we rely on
            // the injected client's DefaultRequestTimeout (caller should set 30s) or the ct.
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // propagate cancellation to the caller
        }
        catch (Exception ex)
        {
            // Network-level failure — return Failed, never throw for control flow.
            return new UsageResult.Failed($"Network error: {ex.Message}");
        }

        using var _ = response;

        switch ((int)response.StatusCode)
        {
            case 401:
            case 403:
                return new UsageResult.Unauthorized();

            case 429:
                var retryAfter = ParseRetryAfter(response.Headers);
                return new UsageResult.RateLimited(retryAfter);

            case >= 200 and < 300:
                return await ParseSuccessAsync(response, ct);

            default:
                return new UsageResult.Failed($"HTTP {(int)response.StatusCode}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static long ParseRetryAfter(HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("Retry-After", out var values))
        {
            foreach (var v in values)
            {
                if (long.TryParse(v, out var secs))
                    return secs;
            }
        }
        return 180; // default when header is absent or unparseable
    }

    private static async Task<UsageResult> ParseSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            return new UsageResult.Failed($"Failed to read response body: {ex.Message}");
        }

        UsageBody? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(body, UsageBodyJsonContext.Default.UsageBody);
        }
        catch (JsonException ex)
        {
            return new UsageResult.Failed($"JSON decode error: {ex.Message}");
        }

        if (parsed is null)
            return new UsageResult.Failed("Empty or null response body");

        var gauges = new List<Gauge>(parsed.Limits.Count);
        foreach (var limit in parsed.Limits)
        {
            if (limit.Group != "session" && limit.Group != "weekly")
                continue;
            gauges.Add(LimitToGauge(limit));
        }

        return new UsageResult.Ok(gauges);
    }

    /// <summary>
    /// Convert a <see cref="LimitEntry"/> to a <see cref="Gauge"/>.
    /// <para>
    /// CRITICAL: <c>percent</c> is 0–100, NOT 0–1.
    /// Divide by 100.0 BEFORE clamping so that 51 → ~0.51, not 1.0.
    /// See: summaries/2026-06-16-usage-api-schema-and-percent-scaling-bug.md
    /// </para>
    /// </summary>
    private static Gauge LimitToGauge(LimitEntry limit)
    {
        // percent is 0–100; convert to 0.0–1.0
        var utilization = Math.Clamp(limit.Percent / 100.0, 0.0, 1.0);
        var usedLabel = $"{Math.Round(limit.Percent)}%";
        var resetsAt = ParseRfc3339ToUnix(limit.ResetsAt);

        return new Gauge(
            Id: limit.Kind,
            Label: BuildLabel(limit),
            Utilization: utilization,
            UsedLabel: usedLabel,
            ResetsAt: resetsAt,
            LimitLabel: limit.Severity);
    }

    /// <summary>
    /// Build the human-readable gauge label for a limit entry.
    /// Labels are provider-prefixed ("Claude …") so a future multi-provider UI
    /// can show gauges from several sources side by side without ambiguity.
    ///
    /// Rules:
    /// <list type="bullet">
    ///   <item><c>group == "session"</c> → "Claude 5-hour"</item>
    ///   <item><c>group == "weekly"</c>, no scope → "Claude Weekly"</item>
    ///   <item><c>group == "weekly"</c>, scope with display_name → "Claude Weekly ({name})"</item>
    ///   <item><c>group == "weekly"</c>, scope without display_name → "Claude Weekly (scoped)"</item>
    ///   <item>any other group → kind</item>
    /// </list>
    /// </summary>
    private static string BuildLabel(LimitEntry limit)
    {
        switch (limit.Group)
        {
            case "session":
                return "Claude 5-hour";

            case "weekly":
                var displayName = limit.Scope?.Model?.DisplayName;
                if (displayName is not null)
                    return $"Claude Weekly ({displayName})";

                // scope exists but no display_name → "(scoped)"; no scope → plain "Claude Weekly"
                return limit.Scope is not null ? "Claude Weekly (scoped)" : "Claude Weekly";

            default:
                return limit.Kind;
        }
    }

    /// <summary>
    /// Parse an RFC 3339 timestamp (with optional fractional seconds and non-UTC offsets
    /// such as <c>+00:00</c>) to a Unix timestamp in whole seconds.
    /// Returns null if the input is null or unparseable.
    /// </summary>
    private static long? ParseRfc3339ToUnix(string? s)
    {
        if (s is null) return null;

        if (DateTimeOffset.TryParse(
            s,
            null,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var dto))
        {
            return dto.ToUnixTimeSeconds();
        }

        return null;
    }
}

// ── JSON deserialization shapes ───────────────────────────────────────────────
// Internal — callers see only Gauge (from Ipc.Contract) and UsageResult.

internal sealed class ModelRef
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

internal sealed class ScopeRef
{
    [JsonPropertyName("model")]
    public ModelRef? Model { get; set; }
}

internal sealed class LimitEntry
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("group")]
    public string Group { get; set; } = "";

    /// <summary>0–100 (percent), NOT 0–1.</summary>
    [JsonPropertyName("percent")]
    public double Percent { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";

    [JsonPropertyName("resets_at")]
    public string? ResetsAt { get; set; }

    [JsonPropertyName("scope")]
    public ScopeRef? Scope { get; set; }
}

/// <summary>
/// Only the <c>limits</c> array is required; all other fields are ignored.
/// </summary>
internal sealed class UsageBody
{
    [JsonPropertyName("limits")]
    public List<LimitEntry> Limits { get; set; } = [];
}

[JsonSerializable(typeof(UsageBody))]
internal sealed partial class UsageBodyJsonContext : JsonSerializerContext { }
