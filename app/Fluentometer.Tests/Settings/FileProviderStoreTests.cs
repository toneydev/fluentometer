using System;
using System.IO;
using Fluentometer.Logic.Settings;
using Xunit;

namespace Fluentometer.Tests.Settings;

// ─────────────────────────────────────────────────────────────────────────────
// ISOLATION STRATEGY (mirrors FileThemeStoreTests)
//
// FileProviderStore exposes an internal constructor FileProviderStore(string
// baseDirOverride) that redirects storage to a temp directory.  Tests use this
// so they never touch the real user's %LOCALAPPDATA%\Fluentometer\providers.json.
//
// DISJOINT-PATH INVARIANT:
// FileProviderStore writes to providers.json; FileThemeStore writes to settings.json.
// These are in the SAME directory but different files and must NEVER clobber each
// other.  One test (DisjointPaths_ProviderStoreAndSettingsFileCoexist) asserts this
// explicitly by creating both stores in the same temp dir and verifying both files
// are written independently.
// ─────────────────────────────────────────────────────────────────────────────

[CollectionDefinition(nameof(FileProviderStoreCollection), DisableParallelization = true)]
public sealed class FileProviderStoreCollection { }

[Collection(nameof(FileProviderStoreCollection))]
public sealed class FileProviderStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileProviderStore _store;

    public FileProviderStoreTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "FileProviderStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new FileProviderStore(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 1. IsEnabled defaults to true for unknown provider IDs (allow-by-default)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_UnknownProviderId_ReturnsTrueByDefault()
    {
        Assert.True(_store.IsEnabled("claude"));
        Assert.True(_store.IsEnabled("gemini"));
        Assert.True(_store.IsEnabled("some-future-provider"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // 2. IsEnabled: empty store file → still returns true (no file = defaults)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_EmptyDirectory_ReturnsTrueForAllIds()
    {
        // No providers.json written yet.
        Assert.True(_store.IsEnabled("claude"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // 3. SetEnabled persists and reloads correctly (true)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetEnabled_True_PersistsAndLoadsBack()
    {
        _store.SetEnabled("claude", true);

        var store2 = new FileProviderStore(_tempDir); // fresh instance from same dir
        Assert.True(store2.IsEnabled("claude"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // 4. SetEnabled persists disabled state (false)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetEnabled_False_PersistsAcrossReload()
    {
        _store.SetEnabled("gemini", false);

        var store2 = new FileProviderStore(_tempDir);
        Assert.False(store2.IsEnabled("gemini"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // 5. SetEnabled for two providers independently
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetEnabled_TwoProviders_IndependentStates()
    {
        _store.SetEnabled("claude", true);
        _store.SetEnabled("gemini", false);

        var store2 = new FileProviderStore(_tempDir);
        Assert.True(store2.IsEnabled("claude"));
        Assert.False(store2.IsEnabled("gemini"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // 6. MarkSeen + Seen() round-trip (single provider)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkSeen_ThenSeen_ContainsProvider()
    {
        _store.MarkSeen("claude");

        var seen = _store.Seen();
        Assert.Contains("claude", seen);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 7. MarkSeen + Seen() persists across reload
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkSeen_PersistsAcrossReload()
    {
        _store.MarkSeen("claude");

        var store2 = new FileProviderStore(_tempDir);
        Assert.Contains("claude", store2.Seen());
    }

    // ────────────────────────────────────────────────────────────────────────
    // 8. MarkSeen: multiple providers round-trip
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkSeen_MultipleProviders_AllAppearInSeen()
    {
        _store.MarkSeen("claude");
        _store.MarkSeen("gemini");

        var store2 = new FileProviderStore(_tempDir);
        var seen = store2.Seen();
        Assert.Contains("claude", seen);
        Assert.Contains("gemini", seen);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 9. MarkSeen is idempotent — duplicate entries are not written
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkSeen_CalledTwice_IsDuplicated_Idempotent()
    {
        _store.MarkSeen("claude");
        _store.MarkSeen("claude"); // second call must not error or duplicate

        var seen = _store.Seen();
        // There may be exactly one "claude" entry (case-insensitive set).
        // Count elements matching "claude".
        int count = 0;
        foreach (var id in seen)
            if (string.Equals(id, "claude", StringComparison.OrdinalIgnoreCase))
                count++;
        Assert.Equal(1, count);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 10. Seen() on fresh store returns empty set
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Seen_EmptyStore_ReturnsEmptySet()
    {
        var seen = _store.Seen();
        Assert.Empty(seen);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 11. Corrupt file → returns defaults, does not throw
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CorruptFile_ReturnsDefaultsWithoutThrowing()
    {
        var filePath = Path.Combine(_tempDir, "providers.json");
        File.WriteAllText(filePath, "NOT JSON {{{");

        var store2 = new FileProviderStore(_tempDir);

        // Must not throw; must return safe defaults.
        var ex = Record.Exception(() =>
        {
            Assert.True(store2.IsEnabled("claude"));
            Assert.Empty(store2.Seen());
        });
        Assert.Null(ex);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 12. DISJOINT-PATH INVARIANT — THE $100 TEST
    //
    // providers.json and settings.json are separate files in the same directory.
    // Writing to FileProviderStore must NEVER modify settings.json, and vice versa.
    //
    // This catches the disjoint-path trap: a new field added to LoadAppSettings/
    // SaveAppSettings silently nulling out provider state (or vice versa).
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DisjointPaths_ProviderStoreAndSettingsFileCoexist()
    {
        // Write to the provider store.
        _store.SetEnabled("gemini", false);
        _store.MarkSeen("claude");

        // Write to the theme store (settings.json) in the SAME directory.
        var themeStore = new Fluentometer.Logic.Theming.FileThemeStore(_tempDir);
        themeStore.SaveAppSettings(new Fluentometer.Logic.Settings.AppSettings
        {
            ThemeId = "ember",
            PollIntervalSeconds = 300,
            OfflineOnly = false,
        });

        // Both files must now exist independently.
        var providerFile = Path.Combine(_tempDir, "providers.json");
        var settingsFile = themeStore.FilePath;

        Assert.True(File.Exists(providerFile),
            "providers.json must exist after FileProviderStore writes");
        Assert.True(File.Exists(settingsFile),
            "settings.json must exist after FileThemeStore writes");

        // They must be different files.
        Assert.NotEqual(
            Path.GetFullPath(providerFile),
            Path.GetFullPath(settingsFile),
            StringComparer.OrdinalIgnoreCase);

        // Content of providers.json must still reflect provider state.
        var reloadedProvider = new FileProviderStore(_tempDir);
        Assert.False(reloadedProvider.IsEnabled("gemini"),
            "providers.json gemini disabled state must survive FileThemeStore write");
        Assert.Contains("claude", reloadedProvider.Seen());

        // Content of settings.json must still reflect theme settings.
        var reloadedSettings = themeStore.LoadAppSettings();
        Assert.Equal("ember", reloadedSettings.ThemeId);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 13. SetEnabled overwrites a previous value (update, not append)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetEnabled_OverwritesPreviousValue()
    {
        _store.SetEnabled("claude", false);
        _store.SetEnabled("claude", true); // re-enable

        Assert.True(_store.IsEnabled("claude"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // 14. Storage uses providers.json filename (not settings.json or cache)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Storage_UsesProvidersJsonFilename()
    {
        _store.SetEnabled("claude", true); // trigger write

        var files = Directory.GetFiles(_tempDir);
        Assert.Contains(files, f =>
            Path.GetFileName(f).Equals("providers.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f =>
            Path.GetFileName(f).Equals("settings.json", StringComparison.OrdinalIgnoreCase));
    }
}
