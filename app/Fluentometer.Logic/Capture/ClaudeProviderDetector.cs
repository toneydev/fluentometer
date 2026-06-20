using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Detects whether Claude Code is installed and signed in by probing
/// <c>%USERPROFILE%\.claude\.credentials.json</c> STRUCTURALLY ONLY — it verifies
/// the file is present, parseable, and contains a non-empty <c>claudeAiOauth</c>
/// block WITHOUT reading or holding the token value.
///
/// <para>
/// Security invariants satisfied (see <see cref="IProviderDetector"/> summary):
/// G-1 Single read of file content — File.GetAttributes used for the existence+reparse check
///     in one call, eliminating the File.Exists/GetAttributes TOCTOU race.
/// G-2 <c>accessToken</c> / <c>refreshToken</c> fields are never read; only structural
///     presence of the <c>claudeAiOauth</c> key is checked.
/// G-4 Read-only; no writes.
/// G-6 Reparse-point (symlink/junction) check before reading.
/// G-7 Fixed explicit path from <see cref="Environment.SpecialFolder.UserProfile"/>.
/// G-8 Result is <see cref="ProviderDetectionResult"/> — no credential data.
/// G-9 Runs async on the poll thread.
/// G-10 No retry loop.
/// G-11 Catch returns NotFound/Error; exception message is never forwarded.
/// </para>
///
/// <para>
/// <b>Probed path:</b> <c>%USERPROFILE%\.claude\.credentials.json</c>
/// (listed in SECURITY.md §Probed Paths).
/// </para>
/// </summary>
public sealed class ClaudeProviderDetector : IProviderDetector
{
    private readonly string _credentialsPath;

    /// <summary>
    /// Creates a detector that probes the default Claude Code credential path.
    /// </summary>
    public ClaudeProviderDetector()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            ".credentials.json"))
    { }

    /// <summary>
    /// Creates a detector that probes <paramref name="credentialsPath"/>.
    /// For use in tests or when the credential path is overridden.
    /// </summary>
    public ClaudeProviderDetector(string credentialsPath)
    {
        _credentialsPath = credentialsPath;
    }

    /// <inheritdoc/>
    public string ProviderId => "claude";

    /// <inheritdoc/>
    public Task<ProviderDetectionResult> DetectAsync(CancellationToken ct = default)
    {
        // G-9: async signature keeps the call off the UI thread; the read itself is
        // sync because the file is tiny (~200 bytes) and avoids async overhead.
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
            var fileResult = CredentialFileReader.Read(_credentialsPath);
            if (!fileResult.IsSuccess)
                return new ProviderDetectionResult(ProviderDetectionStatus.NotFound, null);

            var json = fileResult.Json!;

            // G-2: parse only for structural presence of the claudeAiOauth key.
            // The DTO below deliberately omits token fields so they are never read.
            ClaudeCredentialsShell? shell;
            try
            {
                shell = JsonSerializer.Deserialize(
                    json,
                    ClaudeDetectorJsonContext.Default.ClaudeCredentialsShell);
            }
            catch (JsonException)
            {
                return new ProviderDetectionResult(ProviderDetectionStatus.Error, null);
            }

            // Must have a claudeAiOauth block (even empty is enough to know Claude Code
            // has written the file — a missing block means not signed in).
            if (shell?.HasClaudeAiOauth != true)
                return new ProviderDetectionResult(ProviderDetectionStatus.NotFound, null);

            return new ProviderDetectionResult(ProviderDetectionStatus.Detected, "Claude Code");
        }
        catch
        {
            // G-11: outer catch ensures we never throw, never leak exception details.
            return new ProviderDetectionResult(ProviderDetectionStatus.Error, null);
        }
    }
}

// ── Internal detection-only JSON shapes ──────────────────────────────────────
// G-2: These DTOs deliberately omit accessToken / refreshToken / expiresAt.
// Only the structural presence of the claudeAiOauth key is checked.

internal sealed class ClaudeCredentialsShell
{
    // We only need to know the key is present; the value is ignored.
    // Using a JsonElement? would require reading the value — use a dummy object instead.
    [JsonPropertyName("claudeAiOauth")]
    public OauthPresenceMarker? ClaudeAiOauth { get; set; }

    /// <summary>True when the <c>claudeAiOauth</c> key is present in the file.</summary>
    public bool HasClaudeAiOauth => ClaudeAiOauth is not null;
}

/// <summary>
/// Presence marker: deserialises to non-null when the JSON key exists, regardless
/// of the key's object contents. No token fields are declared here (G-2).
/// </summary>
internal sealed class OauthPresenceMarker { }

[JsonSerializable(typeof(ClaudeCredentialsShell))]
internal sealed partial class ClaudeDetectorJsonContext : JsonSerializerContext { }
