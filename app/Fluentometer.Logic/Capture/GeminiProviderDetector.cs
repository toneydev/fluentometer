using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Detects whether Google Gemini CLI is installed and configured by probing
/// <c>%USERPROFILE%\.gemini\settings.json</c> and reading ONLY the
/// <c>selectedAuthType</c> field.
///
/// <para>
/// Security invariants satisfied (G-1…G-12 from the multi-provider security model):
/// G-1  Single read of file content — File.GetAttributes used for the existence+reparse check
///      in one call, eliminating the File.Exists/GetAttributes TOCTOU race.
/// G-2  NEVER reads <c>tokens.json</c>, gcloud credentials.db, or
///      <c>application_default_credentials.json</c>. Does NOT read <c>GEMINI_API_KEY</c>
///      value. Only reads the <c>selectedAuthType</c> string from settings.json.
/// G-4  Read-only; no writes.
/// G-5  Env-var detection not used (name-presence check avoided for Gemini per spec).
/// G-6  Reparse-point (symlink/junction) check before reading.
/// G-7  Fixed explicit path from <see cref="Environment.SpecialFolder.UserProfile"/>.
/// G-8  Result is <see cref="ProviderDetectionResult"/> — no credential data.
/// G-9  Runs async on the poll thread.
/// G-10 No retry loop.
/// G-11 Catch returns NotFound/Error; exception messages are never forwarded.
/// </para>
///
/// <para>
/// <b>Probed path:</b> <c>%USERPROFILE%\.gemini\settings.json</c>
/// (listed in SECURITY.md §Probed Paths).
/// </para>
///
/// <para>
/// <b>Explicitly NOT probed (per security model):</b>
/// <list type="bullet">
///   <item><c>%APPDATA%\gcloud\application_default_credentials.json</c> — deferred.</item>
///   <item><c>%APPDATA%\gcloud\credentials.db</c> — never read.</item>
///   <item><c>GEMINI_API_KEY</c> env var value — not extracted.</item>
///   <item>VS Code extension storage — deferred.</item>
/// </list>
/// </para>
/// </summary>
public sealed class GeminiProviderDetector : IProviderDetector
{
    private readonly string _settingsPath;

    /// <summary>
    /// Creates a detector that probes the default Gemini CLI settings path.
    /// </summary>
    public GeminiProviderDetector()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini",
            "settings.json"))
    { }

    /// <summary>
    /// Creates a detector that probes <paramref name="settingsPath"/>.
    /// For use in tests or when the settings path is overridden.
    /// </summary>
    public GeminiProviderDetector(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    /// <inheritdoc/>
    public string ProviderId => "gemini";

    /// <inheritdoc/>
    public Task<ProviderDetectionResult> DetectAsync(CancellationToken ct = default)
    {
        // G-9: async signature; the file read is sync because the file is tiny.
        return Task.FromResult(Detect());
    }

    private ProviderDetectionResult Detect()
    {
        try
        {
            // G-1 + G-6 + G-11: two-phase read (GetAttributes → ReadAllText) delegated to
            // CredentialFileReader.  The helper eliminates the TOCTOU race (single syscall
            // for both existence and reparse-point check) and never forwards ex.Message.
            var fileResult = CredentialFileReader.Read(_settingsPath);
            if (!fileResult.IsSuccess)
                return new ProviderDetectionResult(ProviderDetectionStatus.NotFound, null);

            var json = fileResult.Json!;

            // G-2: parse ONLY selectedAuthType — all other fields are ignored.
            GeminiSettingsNarrow? settings;
            try
            {
                settings = JsonSerializer.Deserialize(
                    json,
                    GeminiDetectorJsonContext.Default.GeminiSettingsNarrow);
            }
            catch (JsonException)
            {
                return new ProviderDetectionResult(ProviderDetectionStatus.Error, null);
            }

            // The file is present — even without a selectedAuthType the CLI is installed.
            // Server-truth monitoring requires an OAuth login: only oauth-* auth types can
            // present a Bearer token to the Code Assist backend.  api-key / vertex-ai users
            // cannot be server-monitored, so they are reported NotFound (excluded), mirroring
            // ChatGptProviderDetector's auth_mode=="chatgpt" gate.
            var authType = settings?.SelectedAuthType;
            if (string.IsNullOrWhiteSpace(authType) ||
                !authType.StartsWith("oauth", StringComparison.OrdinalIgnoreCase))
            {
                return new ProviderDetectionResult(ProviderDetectionStatus.NotFound, null);
            }

            return new ProviderDetectionResult(ProviderDetectionStatus.Detected, "Gemini");
        }
        catch
        {
            // G-11: outer catch — never throw, never leak.
            return new ProviderDetectionResult(ProviderDetectionStatus.Error, null);
        }
    }
}

// ── Internal detection-only JSON shape ───────────────────────────────────────
// G-2: Only selectedAuthType is declared. All other fields (tokens, keys, etc.)
// are deliberately omitted so the deserializer never populates them.

internal sealed class GeminiSettingsNarrow
{
    /// <summary>
    /// The auth type selected by the user during <c>gemini auth login</c>.
    /// Examples: "oauth-personal", "api-key", "vertex-ai".
    /// </summary>
    [JsonPropertyName("selectedAuthType")]
    public string? SelectedAuthType { get; set; }
}

[JsonSerializable(typeof(GeminiSettingsNarrow))]
internal sealed partial class GeminiDetectorJsonContext : JsonSerializerContext { }
