// SECURITY: https://chatgpt.com/backend-api/wham/usage is an internal OpenAI endpoint
// with no public API contract or SLA — it can change without notice.  This is the same
// posture as ClaudeProvider calling /api/oauth/usage: we read credentials the user already
// holds, call that vendor's endpoint on behalf of that user, and display the result only to
// that user.  TLS is always verified.  Declared in SECURITY.md §Endpoint Transparency.
// CTO acknowledgment required before shipping (E-6 from 2026-06-19 security review).

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Ipc;

namespace Fluentometer.Logic.Capture;

// ── Version constant ──────────────────────────────────────────────────────────

/// <summary>
/// Constants shared by the Wham usage client and its tests.
/// </summary>
public static class CodexConstants
{
    /// <summary>
    /// Codex CLI version used as the originator segment of the User-Agent header.
    /// <para>
    /// The real Codex CLI sends: <c>codex_cli_rs/{version} ({os_type} {os_version}; {arch}) {terminal_info}</c>.
    /// We send a simplified prefix form that identifies the originator correctly.
    /// </para>
    /// <para>
    /// Source: <c>codex-rs/login/src/auth/default_client.rs</c> — <c>DEFAULT_ORIGINATOR = "codex_cli_rs"</c>
    /// and workspace version 0.141.0 (latest tag <c>rust-v0.141.0</c> as of 2026-06-18).
    /// See: https://github.com/openai/codex/blob/main/codex-rs/login/src/auth/default_client.rs
    /// Update when the Codex CLI workspace version changes.
    /// </para>
    /// <para>
    /// NOTE: The plan assumed <c>codex/&lt;ver&gt;</c> as the User-Agent format, but empirical
    /// inspection of the Codex CLI Rust source reveals the originator prefix is
    /// <c>codex_cli_rs</c> (underscore-delimited, not slash-delimited).
    /// The header sent is therefore <c>codex_cli_rs/0.141.0</c>.
    /// </para>
    /// </summary>
    public const string CodexVersion = "0.141.0";
}

// ── Result discriminated union ────────────────────────────────────────────────

/// <summary>
/// The result of a single call to the <c>/wham/usage</c> endpoint.
/// </summary>
public abstract record WhamResult
{
    public sealed record Ok(IReadOnlyList<Gauge> Gauges) : WhamResult;
    public sealed record Unauthorized : WhamResult;
    public sealed record RateLimited(long RetryAfterSecs) : WhamResult;
    public sealed record Failed(string Reason) : WhamResult;

    // Private constructor — prevents external subclassing.
    // Nested sealed records in C# can inherit from a base with a private constructor
    // because they are declared in the same type scope.
    private WhamResult() { }
}

// ── Interface ─────────────────────────────────────────────────────────────────

public interface IWhamUsageClient
{
    Task<WhamResult> FetchAsync(string baseUrl, string accessToken, string accountId, CancellationToken ct);
}

// ── Implementation ────────────────────────────────────────────────────────────

/// <summary>
/// Fetches ChatGPT usage data from the internal <c>/wham/usage</c> endpoint.
/// <para>
/// TLS verification is always enabled — the <see cref="HttpClient"/> is injected
/// by the caller, which must NOT configure a custom
/// <c>ServerCertificateCustomValidationCallback</c>.
/// </para>
/// <para>
/// Tokens are never logged. The <paramref name="accessToken"/> and
/// <paramref name="accountId"/> parameters are used only in headers and are
/// not stored as fields.
/// </para>
/// </summary>
public sealed class WhamUsageClient : IWhamUsageClient
{
    private readonly HttpClient _http;

    public WhamUsageClient(HttpClient httpClient) => _http = httpClient;

    /// <inheritdoc/>
    public async Task<WhamResult> FetchAsync(
        string baseUrl, string accessToken, string accountId, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/wham/usage";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // User-Agent identifies the Codex CLI originator prefix.
        // The real Codex CLI UA is "codex_cli_rs/{version} ({os} {ver}; {arch}) ..."
        // We use the simplified originator/version form here.
        // SECURITY: do not log accessToken or accountId.
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd($"codex_cli_rs/{CodexConstants.CodexVersion}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // propagate cancellation to the caller
        }
        catch (Exception ex)
        {
            return new WhamResult.Failed($"Network error: {ex.Message}");
        }

        using var _ = response;

        switch ((int)response.StatusCode)
        {
            case 401:
            case 403:
                return new WhamResult.Unauthorized();

            case 429:
                var retryAfter = ParseRetryAfter(response.Headers);
                return new WhamResult.RateLimited(retryAfter);

            case >= 200 and < 300:
                return await ParseSuccessAsync(response, ct);

            default:
                return new WhamResult.Failed($"HTTP {(int)response.StatusCode}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirror of <see cref="OauthUsageClient"/>'s ParseRetryAfter.
    /// Tries <c>long.TryParse</c> on the <c>Retry-After</c> header;
    /// defaults to 180 s when the header is absent or unparseable.
    /// </summary>
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

    private static async Task<WhamResult> ParseSuccessAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            return new WhamResult.Failed($"Failed to read response body: {ex.Message}");
        }

        WhamBody? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(body, WhamBodyJsonContext.Default.WhamBody);
        }
        catch (JsonException ex)
        {
            return new WhamResult.Failed($"JSON decode error: {ex.Message}");
        }

        if (parsed is null)
            return new WhamResult.Failed("Empty or null response body");

        var gauges = new List<Gauge>(2);

        if (parsed.Primary is not null)
            gauges.Add(WindowToGauge("chatgpt_primary", parsed.Primary));

        if (parsed.Secondary is not null)
            gauges.Add(WindowToGauge("chatgpt_secondary", parsed.Secondary));

        return new WhamResult.Ok(gauges);
    }

    /// <summary>
    /// Convert a <see cref="RateLimitWindowDto"/> to a <see cref="Gauge"/>.
    /// <para>
    /// CRITICAL: <c>used_percent</c> is 0–100, NOT 0–1.
    /// Divide by 100.0 BEFORE clamping so that 42 → 0.42, not 1.0.
    /// </para>
    /// <para>
    /// Labels are provider-prefixed ("ChatGPT …") so a multi-provider UI can show
    /// gauges from several sources side by side without ambiguity.
    /// Derived from <c>window_minutes</c> — not hard-coded by struct position.
    /// </para>
    /// </summary>
    private static Gauge WindowToGauge(string id, RateLimitWindowDto w)
    {
        // CRITICAL: used_percent is 0–100; convert to 0.0–1.0
        var utilization = Math.Clamp(w.UsedPercent / 100.0, 0.0, 1.0);
        var usedLabel = $"{w.UsedPercent}%";

        // Derive label from window_minutes — do NOT hard-code by position.
        // primary (~5h) has window_minutes ≤ 360; secondary (~weekly) > 360.
        // window_minutes is a non-nullable int with no sentinel, so two branches cover all values.
        var label = w.WindowMinutes <= 360 ? "ChatGPT 5-hour" : "ChatGPT Weekly";

        return new Gauge(
            Id: id,
            Label: label,
            Utilization: utilization,
            UsedLabel: usedLabel,
            ResetsAt: w.ResetsAt,
            LimitLabel: "subscription limit");
    }
}

// ── JSON deserialization shapes ───────────────────────────────────────────────
// Internal — callers see only Gauge (from Ipc.Contract) and WhamResult.

internal sealed class RateLimitWindowDto
{
    /// <summary>0–100 (percent), NOT 0–1.</summary>
    [JsonPropertyName("used_percent")]
    public int UsedPercent { get; set; }

    [JsonPropertyName("window_minutes")]
    public int WindowMinutes { get; set; }

    [JsonPropertyName("resets_at")]
    public long? ResetsAt { get; set; }
}

internal sealed class WhamBody
{
    [JsonPropertyName("primary")]
    public RateLimitWindowDto? Primary { get; set; }

    [JsonPropertyName("secondary")]
    public RateLimitWindowDto? Secondary { get; set; }
}

[JsonSerializable(typeof(WhamBody))]
internal sealed partial class WhamBodyJsonContext : JsonSerializerContext { }
