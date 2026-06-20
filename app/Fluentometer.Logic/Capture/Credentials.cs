using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fluentometer.Logic.Capture;

// ── Credential value types ────────────────────────────────────────────────────

/// <summary>
/// A string value that is never exposed through ToString() or in exception messages,
/// preventing accidental credential leakage into logs.
/// The raw value is accessible only via <see cref="Expose"/>.
/// </summary>
public sealed class RedactedString
{
    private readonly string _value;

    public RedactedString(string value) => _value = value;

    /// <summary>Returns the raw string value for use in network calls.</summary>
    public string Expose() => _value;

    /// <inheritdoc/>
    public override string ToString() => "***";
}

// ── Public contract ───────────────────────────────────────────────────────────

/// <summary>
/// Claude Code OAuth credential loaded from <c>~/.claude/.credentials.json</c>.
/// <para>
/// Tokens are wrapped in <see cref="RedactedString"/> so they cannot appear in
/// log sinks, even if the record is inadvertently formatted into a log line.
/// </para>
/// </summary>
public sealed record ClaudeCredential(
    RedactedString AccessToken,
    RedactedString? RefreshToken,
    long ExpiresAtMs,
    string? SubscriptionType)
{
    /// <summary>Returns true when <paramref name="nowMs"/> is at or past the expiry timestamp.</summary>
    public bool IsExpired(long nowMs) => nowMs >= ExpiresAtMs;
}

public enum CredentialStatus { Ok, NotFound, ParseError }

public sealed record CredentialResult(CredentialStatus Status, ClaudeCredential? Credential);

public interface IClaudeCredentialReader
{
    CredentialResult Read();
}

// ── Reader implementation ─────────────────────────────────────────────────────

/// <summary>
/// Reads Claude Code's own <c>~/.claude/.credentials.json</c> file.
/// Fluentometer only <em>reads</em> this file; it never writes credentials of its own.
/// The "credentials in Windows Credential Manager" rule governs secrets that
/// Fluentometer itself stores — this reader is for credentials authored by Claude Code.
/// </summary>
public sealed class ClaudeCredentialReader : IClaudeCredentialReader
{
    private readonly string _path;

    /// <param name="credentialsPath">
    /// Optional explicit path to <c>.credentials.json</c>.
    /// Defaults to <c>%USERPROFILE%\.claude\.credentials.json</c>.
    /// </param>
    public ClaudeCredentialReader(string? credentialsPath = null)
    {
        _path = credentialsPath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                ".credentials.json");
    }

    /// <summary>
    /// Reads and parses the credential file synchronously.
    /// The file is small (~200 bytes) so sync I/O is acceptable here.
    /// </summary>
    public CredentialResult Read()
    {
        // P1-B / G-1 / G-6 / G-11: two-phase read (GetAttributes → ReadAllText) is
        // delegated to the shared CredentialFileReader helper.  This closes the
        // reparse-point/TOCTOU gap that existed when File.ReadAllText was called
        // directly without a prior GetAttributes check.
        var fileResult = CredentialFileReader.Read(_path);
        if (!fileResult.IsSuccess)
            return new CredentialResult(CredentialStatus.NotFound, null);

        var json = fileResult.Json!;

        try
        {
            var raw = JsonSerializer.Deserialize(json, CredentialFileJsonContext.Default.CredentialFileRaw);
            if (raw?.ClaudeAiOauth is null)
                return new CredentialResult(CredentialStatus.ParseError, null);

            var oauth = raw.ClaudeAiOauth;
            if (oauth.AccessToken is null)
                return new CredentialResult(CredentialStatus.ParseError, null);

            var credential = new ClaudeCredential(
                AccessToken: new RedactedString(oauth.AccessToken),
                RefreshToken: oauth.RefreshToken is null ? null : new RedactedString(oauth.RefreshToken),
                ExpiresAtMs: oauth.ExpiresAt,
                SubscriptionType: oauth.SubscriptionType);

            return new CredentialResult(CredentialStatus.Ok, credential);
        }
        catch (JsonException)
        {
            return new CredentialResult(CredentialStatus.ParseError, null);
        }
    }
}

// ── Internal JSON deserialization shapes ─────────────────────────────────────
// These are private to the reader and never exposed as public API.

internal sealed class CredentialFileRaw
{
    [JsonPropertyName("claudeAiOauth")]
    public OauthRaw? ClaudeAiOauth { get; set; }
}

internal sealed class OauthRaw
{
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    /// <summary>Unix milliseconds.</summary>
    [JsonPropertyName("expiresAt")]
    public long ExpiresAt { get; set; }

    [JsonPropertyName("subscriptionType")]
    public string? SubscriptionType { get; set; }
}

[JsonSerializable(typeof(CredentialFileRaw))]
internal sealed partial class CredentialFileJsonContext : JsonSerializerContext { }
