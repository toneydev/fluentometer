using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Detects whether the Codex CLI is installed and signed in with a ChatGPT account
/// by probing <c>~/.codex/auth.json</c> (or <c>$CODEX_HOME/auth.json</c>) STRUCTURALLY
/// ONLY — it verifies the file is present, parseable, contains
/// <c>auth_mode == "chatgpt"</c> (case-insensitive), and has a <c>tokens</c> block,
/// WITHOUT reading any token values.
///
/// <para>
/// Security invariants satisfied (see <see cref="IProviderDetector"/> summary):
/// G-1  Single read of file content — File.GetAttributes used for the existence+reparse
///      check in one call, eliminating the File.Exists/GetAttributes TOCTOU race.
/// G-2  <c>access_token</c>, <c>refresh_token</c>, <c>account_id</c>, and all other
///      token fields are never read. Only the structural presence of the <c>tokens</c>
///      key and the non-secret <c>auth_mode</c> string are checked.
/// G-4  Read-only; no writes.
/// G-6  Reparse-point (symlink/junction) check before reading.
/// G-7  Fixed explicit path — either default <c>%USERPROFILE%\.codex\auth.json</c> or
///      <c>$CODEX_HOME\auth.json</c> (P1-A: the env value is never logged).
/// G-8  Result is <see cref="ProviderDetectionResult"/> — no credential data.
/// G-9  Runs async on the poll thread.
/// G-10 No retry loop.
/// G-11 Catch blocks return NotFound/Error; exception messages are never forwarded.
/// </para>
///
/// <para>
/// <b>Auth-mode gate:</b> requires <c>auth_mode == "chatgpt"</c> (case-insensitive).
/// API-key-only Codex users (auth_mode == "api-key") are excluded — they have no
/// subscription gauge to show.
/// </para>
///
/// <para>
/// <b>Probed path:</b> <c>%USERPROFILE%\.codex\auth.json</c>, or
/// <c>$CODEX_HOME\auth.json</c> when the <c>CODEX_HOME</c> environment variable is set
/// (P1-A: the resolved path is used but never logged).
/// </para>
/// </summary>
public sealed class ChatGptProviderDetector : IProviderDetector
{
    private readonly string _authPath;

    /// <summary>
    /// Creates a detector that resolves the Codex auth path from the
    /// <c>CODEX_HOME</c> environment variable (P1-A), or falls back to the
    /// default <c>%USERPROFILE%\.codex\auth.json</c>.
    /// The env-var value is never logged.
    /// </summary>
    public ChatGptProviderDetector()
        : this(ResolveAuthPath())
    { }

    /// <summary>
    /// Creates a detector that probes <paramref name="authPath"/>.
    /// For use in tests or when the auth path is overridden.
    /// </summary>
    public ChatGptProviderDetector(string authPath)
    {
        _authPath = authPath;
    }

    /// <inheritdoc/>
    public string ProviderId => "chatgpt";

    /// <inheritdoc/>
    public Task<ProviderDetectionResult> DetectAsync(CancellationToken ct = default)
    {
        // G-9: async signature keeps the call off the UI thread; the read itself is
        // sync because the file is tiny and avoids async overhead.
        // Return immediately — detection is bounded (G-10).
        return Task.FromResult(Detect());
    }

    private ProviderDetectionResult Detect()
    {
        try
        {
            // G-1 + G-6 + G-11: two-phase read (GetAttributes → ReadAllText) delegated to
            // CredentialFileReader.  The helper eliminates the TOCTOU race (single syscall
            // for both existence and reparse-point check) and never forwards ex.Message.
            var fileResult = CredentialFileReader.Read(_authPath);
            if (!fileResult.IsSuccess)
                return new ProviderDetectionResult(ProviderDetectionStatus.NotFound, null);

            var json = fileResult.Json!;

            // G-2: parse only auth_mode (a non-secret config string) and the structural
            // presence of the tokens block. access_token / refresh_token / account_id must
            // NOT appear in any detection DTO — CodexAuthNarrow has zero token fields.
            CodexAuthNarrow? auth;
            try
            {
                auth = JsonSerializer.Deserialize(
                    json,
                    ChatGptDetectorJsonContext.Default.CodexAuthNarrow);
            }
            catch (JsonException)
            {
                return new ProviderDetectionResult(ProviderDetectionStatus.Error, null);
            }

            // Auth-mode gate: require auth_mode == "chatgpt" (case-insensitive).
            // Absent or different (e.g. "api-key") → NotFound, preventing a subscription
            // gauge from appearing for API-key-only Codex users.
            if (auth?.AuthMode?.Equals("chatgpt", StringComparison.OrdinalIgnoreCase) != true)
                return new ProviderDetectionResult(ProviderDetectionStatus.NotFound, null);

            // Tokens-block gate: the tokens key must be present (non-null).
            // We only check structural presence — no token value is read (G-2).
            if (auth.Tokens is null)
                return new ProviderDetectionResult(ProviderDetectionStatus.NotFound, null);

            return new ProviderDetectionResult(ProviderDetectionStatus.Detected, "ChatGPT");
        }
        catch
        {
            // G-11: outer catch ensures we never throw, never leak exception details.
            return new ProviderDetectionResult(ProviderDetectionStatus.Error, null);
        }
    }

    /// <summary>
    /// Resolves the Codex auth.json path by delegating to the shared
    /// <see cref="CodexPaths.ResolveAuthPath"/> resolver.
    /// P1-A: the CODEX_HOME value is used to build a path but is never logged.
    /// </summary>
    private static string ResolveAuthPath() => CodexPaths.ResolveAuthPath();
}

// ── Internal detection-only JSON shapes ──────────────────────────────────────
// G-2: These DTOs deliberately omit access_token / refresh_token / account_id.
// Only auth_mode (non-secret config string) and structural presence of the tokens
// block are checked. The detector's narrow DTO is SEPARATE from CodexCredentials.cs's
// full DTO — do not share them (detection must not even have token fields available).

internal sealed class CodexAuthNarrow
{
    /// <summary>
    /// The authentication mode (e.g. "chatgpt", "api-key").
    /// Non-secret configuration string — safe to read during detection.
    /// </summary>
    [JsonPropertyName("auth_mode")]
    public string? AuthMode { get; set; }

    /// <summary>
    /// Presence marker: non-null when the "tokens" key exists in the JSON.
    /// No token fields are declared here (G-2) — only structural presence is checked.
    /// </summary>
    [JsonPropertyName("tokens")]
    public CodexTokensPresenceMarker? Tokens { get; set; }
}

/// <summary>
/// Presence marker: deserialises to non-null when the JSON "tokens" key exists,
/// regardless of the key's object contents. No token fields are declared (G-2).
/// </summary>
internal sealed class CodexTokensPresenceMarker { }

[JsonSerializable(typeof(CodexAuthNarrow))]
internal sealed partial class ChatGptDetectorJsonContext : JsonSerializerContext { }
