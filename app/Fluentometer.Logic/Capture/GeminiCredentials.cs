using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fluentometer.Logic.Capture;

// ── Public contract ───────────────────────────────────────────────────────────

/// <summary>
/// Gemini CLI OAuth credential loaded from <c>~/.gemini/oauth_creds.json</c>.
/// <para>
/// <see cref="AccessToken"/> is wrapped in <see cref="RedactedString"/> (P1-C) so it
/// cannot appear in log sinks. <see cref="ExpiresAtMs"/> is Unix milliseconds.
/// </para>
/// </summary>
public sealed record GeminiCredential(
    RedactedString AccessToken,  // P1-C
    long ExpiresAtMs)            // Unix milliseconds (oauth_creds.json "expiry_date")
{
    /// <summary>True when <paramref name="nowMs"/> is at or past the expiry timestamp.</summary>
    public bool IsExpired(long nowMs) => nowMs >= ExpiresAtMs;
}

public enum GeminiCredentialStatus { Ok, NotFound, ParseError }

public sealed record GeminiCredentialResult(GeminiCredentialStatus Status, GeminiCredential? Credential);

public interface IGeminiCredentialReader
{
    GeminiCredentialResult Read();
}

// ── Reader implementation ─────────────────────────────────────────────────────

/// <summary>
/// Reads the Gemini CLI's <c>~/.gemini/oauth_creds.json</c> file (read-only).
/// </summary>
/// <remarks>
/// Security guardrails applied here:
/// <list type="bullet">
///   <item>P1-B: reparse-point guard via <c>File.GetAttributes</c> before <c>ReadAllText</c>.</item>
///   <item>P1-C: <c>access_token</c> wrapped in <see cref="RedactedString"/> immediately at the
///   deserialization boundary.</item>
///   <item>G-11: catch blocks never forward <c>ex.Message</c> (the path contains the Windows username).</item>
/// </list>
/// </remarks>
public sealed class GeminiCredentialReader : IGeminiCredentialReader
{
    private readonly string _path;

    /// <param name="credsPath">
    /// Optional explicit path. Defaults to <c>%USERPROFILE%\.gemini\oauth_creds.json</c>.
    /// </param>
    public GeminiCredentialReader(string? credsPath = null)
    {
        _path = credsPath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".gemini",
                "oauth_creds.json");
    }

    public GeminiCredentialResult Read()
    {
        // P1-B / G-1 / G-6 / G-11: two-phase read (GetAttributes → ReadAllText) is
        // delegated to the shared CredentialFileReader helper.  Deserialization,
        // field access, and RedactedString wrapping remain here so each provider's
        // G-2 audit stays independently verifiable by reading this file.
        var fileResult = CredentialFileReader.Read(_path);
        if (!fileResult.IsSuccess)
            return new GeminiCredentialResult(GeminiCredentialStatus.NotFound, null);

        var json = fileResult.Json!;

        // Deserialize and extract fields.
        try
        {
            var raw = JsonSerializer.Deserialize(json, GeminiOauthCredsJsonContext.Default.GeminiOauthCredsRaw);
            if (raw is null)
                return new GeminiCredentialResult(GeminiCredentialStatus.ParseError, null);

            if (string.IsNullOrEmpty(raw.AccessToken))
                return new GeminiCredentialResult(GeminiCredentialStatus.ParseError, null);

            // expiry_date absent → 0 → provider treats as expired (safe default).
            var credential = new GeminiCredential(
                AccessToken: new RedactedString(raw.AccessToken),
                ExpiresAtMs: raw.ExpiryDate ?? 0L);

            return new GeminiCredentialResult(GeminiCredentialStatus.Ok, credential);
        }
        catch (JsonException)
        {
            return new GeminiCredentialResult(GeminiCredentialStatus.ParseError, null);
        }
    }
}

// ── Internal JSON deserialization shape ──────────────────────────────────────
// Only the fields we need are declared. refresh_token / id_token are deliberately
// NOT declared so the deserializer never materializes them.

internal sealed class GeminiOauthCredsRaw
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    /// <summary>Unix milliseconds.</summary>
    [JsonPropertyName("expiry_date")]
    public long? ExpiryDate { get; set; }
}

[JsonSerializable(typeof(GeminiOauthCredsRaw))]
internal sealed partial class GeminiOauthCredsJsonContext : JsonSerializerContext { }
