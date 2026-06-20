using System;
using System.Globalization;
using System.Net.Http.Headers;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Shared HTTP parsing/formatting utilities used by all usage clients
/// (OauthUsageClient, WhamUsageClient, CloudCodeUsageClient).
/// <para>
/// Scope is intentionally narrow: retry-after parsing, RFC 3339 → Unix conversion,
/// and snake_case humanization. Gauge math stays per-provider (never consolidated here).
/// </para>
/// </summary>
internal static class HttpClientHelper
{
    /// <summary>
    /// Read the integer value from the <c>Retry-After</c> response header.
    /// Returns 180 (the poll floor) when the header is absent or unparseable.
    /// </summary>
    internal static long ParseRetryAfter(HttpResponseHeaders headers)
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

    /// <summary>
    /// Parse an RFC 3339 timestamp (with optional fractional seconds and non-UTC offsets
    /// such as <c>+00:00</c>) to a Unix timestamp in whole seconds.
    /// Returns null if the input is null or unparseable.
    /// </summary>
    internal static long? ParseRfc3339ToUnix(string? s)
    {
        if (s is null) return null;

        return DateTimeOffset.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dto)
            ? dto.ToUnixTimeSeconds()
            : null;
    }

    /// <summary>
    /// Convert a snake_case or SCREAMING_SNAKE_CASE identifier to Title Case words.
    /// Examples: <c>"monthly_opus"</c> → <c>"Monthly Opus"</c>;
    ///           <c>"REQUESTS"</c> → <c>"Requests"</c>.
    /// <para>
    /// Each word is lowercased first, then the first character is uppercased.
    /// This is byte-identical to the original OauthUsageClient behaviour for
    /// Claude's lowercase <c>kind</c> values, and identical to CloudCodeUsageClient's
    /// behaviour for uppercase <c>tokenType</c> values such as <c>"REQUESTS"</c>.
    /// </para>
    /// </summary>
    internal static string Humanize(string s)
    {
        var words = s.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i].ToLowerInvariant();
            words[i] = char.ToUpperInvariant(w[0]) + w[1..];
        }
        return string.Join(' ', words);
    }
}
