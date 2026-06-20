using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fluentometer.Logic.Capture;

// ── Public contract ───────────────────────────────────────────────────────────

/// <summary>
/// Codex CLI OAuth credential loaded from <c>~/.codex/auth.json</c>.
/// <para>
/// Both <see cref="AccessToken"/> and <see cref="AccountId"/> are wrapped in
/// <see cref="RedactedString"/> (P1-C) so they cannot appear in log sinks, even if
/// the record is inadvertently formatted into a log line.
/// </para>
/// </summary>
public sealed record CodexCredential(
    RedactedString AccessToken,  // P1-C: RedactedString wraps JWT
    RedactedString AccountId,    // P1-C: stable identity token, never logged
    string? PlanType);           // non-secret: chatgpt_plan_type from id_token fields

public enum CodexCredentialStatus { Ok, NotFound, ParseError }

public sealed record CodexCredentialResult(CodexCredentialStatus Status, CodexCredential? Credential);

public interface ICodexCredentialReader
{
    CodexCredentialResult Read();
}

// ── Reader implementation ─────────────────────────────────────────────────────

/// <summary>
/// Reads the Codex CLI's <c>~/.codex/auth.json</c> file.
/// Fluentometer only <em>reads</em> this file; it never writes credentials of its own.
/// </summary>
/// <remarks>
/// Security guardrails applied here:
/// <list type="bullet">
///   <item>P1-B: reparse-point guard via <c>File.GetAttributes</c> before <c>ReadAllText</c>.</item>
///   <item>P1-C: <c>access_token</c> and <c>account_id</c> wrapped in <see cref="RedactedString"/>
///   immediately at the deserialization boundary.</item>
///   <item>G-11: catch blocks never forward <c>ex.Message</c> (it can contain the Windows username
///   in the file path).</item>
/// </list>
/// </remarks>
public sealed class CodexCredentialReader : ICodexCredentialReader
{
    private readonly string _path;

    /// <param name="authPath">
    /// Optional explicit path to <c>auth.json</c>.
    /// When <c>null</c>, defaults to <see cref="CodexPaths.ResolveAuthPath"/>, which
    /// honors the <c>CODEX_HOME</c> environment variable (falling back to
    /// <c>%USERPROFILE%\.codex\auth.json</c>).
    /// </param>
    public CodexCredentialReader(string? authPath = null)
    {
        _path = authPath ?? CodexPaths.ResolveAuthPath();
    }

    /// <summary>
    /// Reads and parses the Codex auth file synchronously.
    /// The file is small so sync I/O is acceptable here.
    /// </summary>
    public CodexCredentialResult Read()
    {
        // P1-B / G-1 / G-6 / G-11: two-phase read (GetAttributes → ReadAllText) is
        // delegated to the shared CredentialFileReader helper.  Deserialization,
        // field access, and RedactedString wrapping remain here so each provider's
        // G-2 audit stays independently verifiable by reading this file.
        var fileResult = CredentialFileReader.Read(_path);
        if (!fileResult.IsSuccess)
            return new CodexCredentialResult(CodexCredentialStatus.NotFound, null);

        var json = fileResult.Json!;

        // Deserialize and extract fields.
        try
        {
            var raw = JsonSerializer.Deserialize(json, CodexAuthFileJsonContext.Default.CodexAuthFileRaw);
            if (raw is null)
                return new CodexCredentialResult(CodexCredentialStatus.ParseError, null);

            var tokens = raw.Tokens;
            if (tokens is null)
                return new CodexCredentialResult(CodexCredentialStatus.ParseError, null);

            // P1-C: wrap immediately at deserialization boundary — no plain string escapes.
            var accessToken = tokens.AccessToken;
            var accountId = tokens.AccountId;

            if (string.IsNullOrEmpty(accessToken))
                return new CodexCredentialResult(CodexCredentialStatus.ParseError, null);

            if (string.IsNullOrEmpty(accountId))
                return new CodexCredentialResult(CodexCredentialStatus.ParseError, null);

            var planType = tokens.IdToken?.ChatgptPlanType;

            var credential = new CodexCredential(
                AccessToken: new RedactedString(accessToken),
                AccountId: new RedactedString(accountId),
                PlanType: planType);

            return new CodexCredentialResult(CodexCredentialStatus.Ok, credential);
        }
        catch (JsonException)
        {
            return new CodexCredentialResult(CodexCredentialStatus.ParseError, null);
        }
    }
}

// ── Internal JSON deserialization shapes ─────────────────────────────────────
// These are private to the reader and never exposed as public API.

internal sealed class CodexAuthFileRaw
{
    [JsonPropertyName("tokens")]
    public CodexTokenDataRaw? Tokens { get; set; }
}

internal sealed class CodexTokenDataRaw
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("account_id")]
    public string? AccountId { get; set; }

    [JsonPropertyName("id_token")]
    public CodexIdTokenRaw? IdToken { get; set; }
}

internal sealed class CodexIdTokenRaw
{
    [JsonPropertyName("chatgpt_plan_type")]
    public string? ChatgptPlanType { get; set; }
}

[JsonSerializable(typeof(CodexAuthFileRaw))]
internal sealed partial class CodexAuthFileJsonContext : JsonSerializerContext { }
