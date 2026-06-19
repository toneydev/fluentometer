using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fluentometer.Logic.Settings;

/// <summary>
/// File-backed implementation of <see cref="IProviderStore"/>.
///
/// <para>
/// Storage: <c>%LOCALAPPDATA%\Fluentometer\providers.json</c>.
/// This is a SEPARATE file from <c>settings.json</c> — never touches
/// <c>FileThemeStore</c> or <c>AppSettings</c> (disjoint-path invariant).
/// </para>
///
/// <para>
/// JSON schema:
/// <code>
/// {
///   "enabled": { "claude": true, "gemini": false },
///   "seen": [ "claude" ]
/// }
/// </code>
/// Missing or null fields are tolerated; the file is recreated on write.
/// </para>
///
/// <para>
/// Constructor accepts a <paramref name="baseDirOverride"/> for tests so that
/// tests do not touch the real user profile (mirrors <c>FileThemeStore</c>'s pattern).
/// </para>
/// </summary>
public sealed class FileProviderStore : IProviderStore
{
    // -------------------------------------------------------------------------
    // Optional base-directory override — used by tests to redirect away from the
    // real user's %LOCALAPPDATA%\Fluentometer\ without relying on env-var mutation.
    // -------------------------------------------------------------------------

    private readonly string? _baseDirOverride;

    /// <summary>
    /// Creates a <see cref="FileProviderStore"/> that uses the real
    /// <c>%LOCALAPPDATA%\Fluentometer\</c> path.
    /// </summary>
    public FileProviderStore() { }

    /// <summary>
    /// Creates a <see cref="FileProviderStore"/> that uses
    /// <paramref name="baseDirOverride"/> as the storage directory instead of
    /// <c>%LOCALAPPDATA%\Fluentometer\</c>.
    /// For use in tests only — production code always uses the parameterless constructor.
    /// </summary>
    internal FileProviderStore(string baseDirOverride) => _baseDirOverride = baseDirOverride;

    // -------------------------------------------------------------------------
    // Path helpers
    // -------------------------------------------------------------------------

    private string Dir =>
        _baseDirOverride ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fluentometer");

    private string FilePath => Path.Combine(Dir, "providers.json");

    // -------------------------------------------------------------------------
    // JSON shape
    // -------------------------------------------------------------------------

    private sealed class ProvidersJson
    {
        [JsonPropertyName("enabled")]
        public Dictionary<string, bool>? Enabled { get; set; }

        [JsonPropertyName("seen")]
        public List<string>? Seen { get; set; }
    }

    // -------------------------------------------------------------------------
    // IProviderStore implementation
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public bool IsEnabled(string providerId)
    {
        var json = TryLoad();
        if (json?.Enabled is null) return true; // default: enabled
        if (!json.Enabled.TryGetValue(providerId, out var val)) return true; // unknown → enabled
        return val;
    }

    /// <inheritdoc/>
    public void SetEnabled(string providerId, bool enabled)
    {
        var json = TryLoad() ?? new ProvidersJson();
        json.Enabled ??= [];
        json.Enabled[providerId] = enabled;
        Save(json);
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> Seen()
    {
        var json = TryLoad();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (json?.Seen is not null)
            foreach (var id in json.Seen)
                set.Add(id);
        return set;
    }

    /// <inheritdoc/>
    public void MarkSeen(string providerId)
    {
        var json = TryLoad() ?? new ProvidersJson();
        json.Seen ??= [];
        if (!json.Seen.Exists(s => string.Equals(s, providerId, StringComparison.OrdinalIgnoreCase)))
        {
            json.Seen.Add(providerId);
            Save(json);
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private ProvidersJson? TryLoad()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<ProvidersJson>(File.ReadAllText(FilePath));
        }
        catch
        {
            return null;
        }
    }

    private void Save(ProvidersJson json)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(json, s_writeOptions));
        }
        catch
        {
            // Best-effort — swallow errors (same pattern as FileThemeStore).
        }
    }
}
