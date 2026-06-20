// SECURITY: https://cloudcode-pa.googleapis.com/v1internal:* is an internal Google Code
// Assist endpoint with no public API contract or SLA — it can change without notice.  This
// is the same posture as ClaudeProvider calling /api/oauth/usage and ChatGptProvider calling
// /wham/usage: we read credentials the user already holds (Gemini CLI OAuth), call that
// vendor's endpoint on behalf of that user, and display the result only to that user.  TLS
// is always verified.  Declared in SECURITY.md §Endpoint Transparency.  CTO acknowledgment
// required before shipping (E-7 from the 2026-06-19 Gemini server-truth research).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Ipc;

namespace Fluentometer.Logic.Capture;

// ── Constants ─────────────────────────────────────────────────────────────────

public static class GeminiConstants
{
    /// <summary>Code Assist backend host. Env-overridable in the CLI (CODE_ASSIST_ENDPOINT); we ship the default.</summary>
    public const string CodeAssistEndpoint = "https://cloudcode-pa.googleapis.com";

    /// <summary>Code Assist API version segment (CLI default CODE_ASSIST_API_VERSION).</summary>
    public const string CodeAssistApiVersion = "v1internal";

    /// <summary>
    /// Gemini CLI version used in the User-Agent.
    /// UNCONFIRMED: the exact UA the CLI sends is set by google-auth-library and was not
    /// confirmable from source (the GeminiCLI/{version}/{model} guess was refuted in research).
    /// The endpoint is not known to require a specific UA. Verify empirically; update if needed.
    /// </summary>
    public const string GeminiCliVersion = "0.37.0";
}

// ── Result union ──────────────────────────────────────────────────────────────

public abstract record CloudCodeResult
{
    public sealed record Ok(IReadOnlyList<Gauge> Gauges, string Plan) : CloudCodeResult;
    public sealed record Unauthorized : CloudCodeResult;
    public sealed record RateLimited(long RetryAfterSecs) : CloudCodeResult;
    public sealed record Failed(string Reason) : CloudCodeResult;
    private CloudCodeResult() { }
}

// ── Interface ─────────────────────────────────────────────────────────────────

public interface ICloudCodeUsageClient
{
    Task<CloudCodeResult> FetchAsync(string accessToken, CancellationToken ct);
}

// ── Implementation ────────────────────────────────────────────────────────────

/// <summary>
/// Fetches Gemini CLI usage from the Code Assist backend.
/// Two RPCs: <c>:loadCodeAssist</c> (best-effort — project id + tier) then
/// <c>:retrieveUserQuota</c> (authoritative). TLS always verified (injected HttpClient).
/// The access token is used only in the Authorization header; never logged or stored.
/// </summary>
public sealed class CloudCodeUsageClient : ICloudCodeUsageClient
{
    private readonly HttpClient _http;

    public CloudCodeUsageClient(HttpClient httpClient) => _http = httpClient;

    private static string MethodUrl(string method) =>
        $"{GeminiConstants.CodeAssistEndpoint}/{GeminiConstants.CodeAssistApiVersion}:{method}";

    public async Task<CloudCodeResult> FetchAsync(string accessToken, CancellationToken ct)
    {
        // 1. Best-effort loadCodeAssist for project id + tier. Any failure → null/Gemini.
        string? project = null;
        string plan = "Gemini";
        try
        {
            var (p, tierPlan) = await LoadCodeAssistAsync(accessToken, ct);
            project = p;
            plan = tierPlan;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* non-fatal — proceed with {} body and default plan */ }

        // 2. Authoritative retrieveUserQuota. Its status drives the result.
        // GCP project ids are quote-free (lowercase letters, digits, hyphens), so direct
        // interpolation is injection-safe and avoids a reflection-based JsonSerializer.Serialize
        // call (the codebase is source-gen-only for -warnaserror cleanliness).
        var body = project is { Length: > 0 }
            ? $"{{\"project\":\"{project}\"}}"
            : "{}";

        using var request = BuildPost(MethodUrl("retrieveUserQuota"), accessToken, body);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new CloudCodeResult.Failed($"Network error: {ex.Message}");
        }

        using var _ = response;

        switch ((int)response.StatusCode)
        {
            case 401:
            case 403:
                return new CloudCodeResult.Unauthorized();
            case 429:
                return new CloudCodeResult.RateLimited(ParseRetryAfter(response.Headers));
            case >= 200 and < 300:
                return await ParseQuotaAsync(response, plan, ct);
            default:
                return new CloudCodeResult.Failed($"HTTP {(int)response.StatusCode}");
        }
    }

    // ── loadCodeAssist (best-effort) ────────────────────────────────────────────

    private async Task<(string? Project, string Plan)> LoadCodeAssistAsync(string accessToken, CancellationToken ct)
    {
        using var request = BuildPost(MethodUrl("loadCodeAssist"), accessToken, "{}");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
            return (null, "Gemini");

        var json = await response.Content.ReadAsStringAsync(ct);
        var load = JsonSerializer.Deserialize(json, CloudCodeJsonContext.Default.LoadCodeAssistDto);
        if (load is null)
            return (null, "Gemini");

        var plan = PlanFromTier(load.CurrentTier?.Id, load.PaidTier?.Id);
        return (load.CloudaicompanionProject, plan);
    }

    // ── retrieveUserQuota parsing ───────────────────────────────────────────────

    private static async Task<CloudCodeResult> ParseQuotaAsync(
        HttpResponseMessage response, string plan, CancellationToken ct)
    {
        string body;
        try { body = await response.Content.ReadAsStringAsync(ct); }
        catch (Exception ex) { return new CloudCodeResult.Failed($"Failed to read response body: {ex.Message}"); }

        QuotaDto? parsed;
        try { parsed = JsonSerializer.Deserialize(body, CloudCodeJsonContext.Default.QuotaDto); }
        catch (JsonException ex) { return new CloudCodeResult.Failed($"JSON decode error: {ex.Message}"); }

        if (parsed is null)
            return new CloudCodeResult.Failed("Empty or null response body");

        var gauges = new List<Gauge>();
        if (parsed.Buckets is not null)
        {
            foreach (var b in parsed.Buckets)
            {
                // Key on remainingFraction (always present for a real bucket); skip if absent.
                if (b.RemainingFraction is null) continue;
                gauges.Add(BucketToGauge(b));
            }
        }

        return new CloudCodeResult.Ok(gauges, plan);
    }

    /// <summary>
    /// Convert a quota bucket to a gauge.
    /// CRITICAL: <c>remainingFraction</c> is the fraction REMAINING (0–1).
    /// <c>Gauge.Utilization</c> is the fraction USED, so invert: used = 1 - remaining.
    /// This is the OPPOSITE of WhamUsageClient (where used_percent was already "used").
    /// </summary>
    private static Gauge BucketToGauge(BucketDto b)
    {
        var remaining = b.RemainingFraction!.Value;
        var used = Math.Clamp(1.0 - remaining, 0.0, 1.0);

        var tokenType = b.TokenType;
        var label = string.IsNullOrEmpty(tokenType) || tokenType.Equals("REQUESTS", StringComparison.OrdinalIgnoreCase)
            ? "Gemini Requests"
            : $"Gemini {Humanize(tokenType)}";

        var idSeed = b.ModelId ?? tokenType ?? "quota";

        return new Gauge(
            Id: "gemini_" + Sanitize(idSeed),
            Label: label,
            Utilization: used,
            UsedLabel: $"{Math.Round(used * 100)}%",
            ResetsAt: ParseRfc3339ToUnix(b.ResetTime),
            LimitLabel: "daily limit");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static HttpRequestMessage BuildPost(string url, string accessToken, string jsonBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        // SECURITY: do not log accessToken; used only in the Authorization header.
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd($"GeminiCLI/{GeminiConstants.GeminiCliVersion}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    /// <summary>Mirror of <see cref="WhamUsageClient"/>'s ParseRetryAfter; default 180 s.</summary>
    private static long ParseRetryAfter(HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("Retry-After", out var values))
        {
            foreach (var v in values)
                if (long.TryParse(v, out var secs)) return secs;
        }
        return 180;
    }

    public static string PlanFromTier(string? currentTierId, string? paidTierId)
    {
        if (string.Equals(paidTierId, "standard-tier", StringComparison.OrdinalIgnoreCase))
            return "Gemini (Paid)";
        // Normalize to lowercase so matching is case-insensitive, consistent with
        // the OrdinalIgnoreCase checks elsewhere in this file. The fallback arm uses
        // the original string so unknown tiers display as the server sent them.
        return currentTierId?.ToLowerInvariant() switch
        {
            "free-tier" => "Gemini (Free)",
            "legacy-tier" => "Gemini (Legacy)",
            "standard-tier" => "Gemini (Standard)",
            _ when currentTierId is { Length: > 0 } => $"Gemini ({currentTierId})",
            _ => "Gemini",
        };
    }

    private static string Humanize(string s)
    {
        var words = s.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i].ToLowerInvariant();
            words[i] = char.ToUpperInvariant(w[0]) + w[1..];
        }
        return string.Join(' ', words);
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        return sb.ToString();
    }

    private static long? ParseRfc3339ToUnix(string? s)
    {
        if (s is null) return null;
        return DateTimeOffset.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dto)
            ? dto.ToUnixTimeSeconds()
            : null;
    }
}

// ── JSON deserialization shapes ───────────────────────────────────────────────

internal sealed class LoadCodeAssistDto
{
    [JsonPropertyName("currentTier")] public TierDto? CurrentTier { get; set; }
    [JsonPropertyName("paidTier")] public TierDto? PaidTier { get; set; }
    [JsonPropertyName("cloudaicompanionProject")] public string? CloudaicompanionProject { get; set; }
}

internal sealed class TierDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
}

internal sealed class QuotaDto
{
    [JsonPropertyName("buckets")] public List<BucketDto>? Buckets { get; set; }
}

internal sealed class BucketDto
{
    /// <summary>Fraction REMAINING (0–1), NOT used. Always present for a real bucket.</summary>
    [JsonPropertyName("remainingFraction")] public double? RemainingFraction { get; set; }
    [JsonPropertyName("resetTime")] public string? ResetTime { get; set; }
    [JsonPropertyName("modelId")] public string? ModelId { get; set; }
    [JsonPropertyName("tokenType")] public string? TokenType { get; set; }
}

[JsonSerializable(typeof(LoadCodeAssistDto))]
[JsonSerializable(typeof(QuotaDto))]
internal sealed partial class CloudCodeJsonContext : JsonSerializerContext { }
