using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fluentometer.Logic.Settings;

namespace Fluentometer.Logic.Theming;

/// <summary>
/// Persists app settings (theme, poll interval, offline flag) to
/// %LOCALAPPDATA%\Fluentometer\settings.json.
/// Uses Environment.GetFolderPath — NOT ApplicationData.Current, which throws
/// when the app runs unpackaged (no package identity).
///
/// File schema (superset of the original ThemeId-only schema):
/// <code>
/// {
///   "ThemeId": "aurora",
///   "PollIntervalSeconds": 300,
///   "OfflineOnly": false,
///   "GradientDirection": "DeepToBright"
/// }
/// </code>
/// Missing or null fields are tolerated; the file is silently recreated on write.
///
/// INVARIANT — two disjoint persistence paths:
/// - IThemeStore methods (LoadThemeId/SaveThemeId/LoadGradientDirection/SaveGradientDirection)
///   are atomic field reads/writes that touch only their respective field.
/// - LoadAppSettings/SaveAppSettings map only ThemeId/PollIntervalSeconds/OfflineOnly.
/// GradientDirection is intentionally excluded from Load/SaveAppSettings so that a
/// poll-interval save does NOT silently null out the saved gradient direction (or any
/// future IThemeStore field). Do NOT add new IThemeStore fields to Load/SaveAppSettings.
/// </summary>
public sealed class FileThemeStore : IThemeStore
{
    // -------------------------------------------------------------------------
    // Optional base-directory override — used by tests to redirect away from the
    // real user's %LOCALAPPDATA%\Fluentometer\ without relying on env-var mutation.
    // (Environment.GetFolderPath reads the Windows known-folder registry, not the
    // LOCALAPPDATA env var, so env-var redirection does not work on .NET/Windows.)
    // -------------------------------------------------------------------------

    private readonly string? _baseDirOverride;

    /// <summary>
    /// Creates a FileThemeStore that uses the real %LOCALAPPDATA%\Fluentometer\ path.
    /// </summary>
    public FileThemeStore() { }

    /// <summary>
    /// Creates a FileThemeStore that uses <paramref name="baseDirOverride"/> as the
    /// settings directory instead of %LOCALAPPDATA%\Fluentometer\.
    /// For use in tests only — production code always uses the parameterless constructor.
    /// </summary>
    internal FileThemeStore(string baseDirOverride) => _baseDirOverride = baseDirOverride;

    // -------------------------------------------------------------------------
    // Path helpers
    // -------------------------------------------------------------------------

    private string Dir =>
        _baseDirOverride ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fluentometer");

    public string FilePath => Path.Combine(Dir, "settings.json");

    // -------------------------------------------------------------------------
    // JSON shape — matches AppSettings property names so the two can share the
    // same file without a second I/O path.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Loose JSON record for load/save.  All properties are nullable so that
    /// old files with only "ThemeId" deserialise without error.
    /// </summary>
    private sealed class SettingsJson
    {
        public string? ThemeId { get; set; }
        public int? PollIntervalSeconds { get; set; }
        public bool? OfflineOnly { get; set; }
        public string? Density { get; set; }
        public string? GradientDirection { get; set; }
    }

    // -------------------------------------------------------------------------
    // IThemeStore implementation
    // -------------------------------------------------------------------------

    public string? LoadThemeId()
    {
        return TryLoad()?.ThemeId;
    }

    public void SaveThemeId(string id)
    {
        var current = TryLoad() ?? new SettingsJson();
        current.ThemeId = id;
        Save(current);
    }

    public GradientDirection LoadGradientDirection()
    {
        var raw = TryLoad()?.GradientDirection;
        return Enum.TryParse<GradientDirection>(raw, ignoreCase: true, out var dir)
            ? dir
            : GradientDirection.DeepToBright;
    }

    public void SaveGradientDirection(GradientDirection direction)
    {
        var current = TryLoad() ?? new SettingsJson();
        current.GradientDirection = direction.ToString();
        Save(current);
    }

    // -------------------------------------------------------------------------
    // AppSettings load/save (shared schema)
    // INVARIANT: GradientDirection is NOT read or written here — it is managed
    // exclusively by LoadGradientDirection/SaveGradientDirection above. Adding
    // GradientDirection (or any new IThemeStore field) here would cause a
    // SaveAppSettings call to silently null out a previously saved direction.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Loads the full AppSettings from the shared settings.json.
    /// Returns a default AppSettings instance if the file is missing/corrupt.
    /// </summary>
    public AppSettings LoadAppSettings()
    {
        var json = TryLoad();
        var s = new AppSettings();
        if (json is null) return s;
        if (json.ThemeId is not null) s.ThemeId = json.ThemeId;
        if (json.PollIntervalSeconds.HasValue) s.PollIntervalSeconds = json.PollIntervalSeconds.Value;
        if (json.OfflineOnly.HasValue) s.OfflineOnly = json.OfflineOnly.Value;
        if (json.Density is not null) s.Density = json.Density;
        return s;
    }

    /// <summary>
    /// Persists the full AppSettings to the shared settings.json.
    /// </summary>
    public void SaveAppSettings(AppSettings settings)
    {
        var json = TryLoad() ?? new SettingsJson();
        json.ThemeId = settings.ThemeId;
        json.PollIntervalSeconds = settings.PollIntervalSeconds;
        json.OfflineOnly = settings.OfflineOnly;
        json.Density = settings.Density;
        Save(json);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private SettingsJson? TryLoad()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<SettingsJson>(File.ReadAllText(FilePath));
        }
        catch
        {
            // Corrupt or missing file — fall back to defaults.
            return null;
        }
    }

    private void Save(SettingsJson json)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(json, s_writeOptions));
    }
}
