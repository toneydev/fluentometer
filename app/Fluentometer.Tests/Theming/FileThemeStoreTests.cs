using System;
using System.IO;
using Fluentometer.Logic.Settings;
using Fluentometer.Logic.Theming;
using Xunit;

namespace Fluentometer.Tests.Theming;

// ─────────────────────────────────────────────────────────────────────────────
// ISOLATION STRATEGY
//
// Environment.GetFolderPath(SpecialFolder.LocalApplicationData) reads the
// Windows known-folder path from the registry — it does NOT honor the
// LOCALAPPDATA environment variable, so env-var redirection does not work on
// .NET/Windows (verified empirically: setting LOCALAPPDATA before calling
// GetFolderPath returns the original registry value, not the overridden one).
//
// Instead, FileThemeStore exposes an internal constructor that accepts a
// base-directory override.  Tests pass a per-test temp directory so they never
// touch the real user's %LOCALAPPDATA%\Fluentometer\settings.json.
// Fluentometer.Logic exposes the internal constructor to this project via
// [assembly: InternalsVisibleTo("Fluentometer.Tests")] in AssemblyInfo.cs.
//
// NON-PARALLEL COLLECTION
// These tests mutate disk state in their own temp directories. Although each
// test gets a unique temp dir, xUnit can run tests within a class in parallel
// by default within a collection.  We place them in a dedicated
// [Collection] with [CollectionDefinition(DisableParallelization = true)] so
// they run serially and cannot race on directory creation or file I/O.
// ─────────────────────────────────────────────────────────────────────────────

[CollectionDefinition(nameof(FileThemeStoreCollection), DisableParallelization = true)]
public sealed class FileThemeStoreCollection { }

[Collection(nameof(FileThemeStoreCollection))]
public sealed class FileThemeStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileThemeStore _store;

    public FileThemeStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FileThemeStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new FileThemeStore(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup; don't fail the test run */ }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 1 — THE CORE INVARIANT
    // SaveGradientDirection must survive a SaveAppSettings call unchanged.
    // This is the regression the disjoint-persistence design prevents: a
    // poll-interval save used to silently null out the saved direction.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GradientDirectionSurvivesSaveAppSettings()
    {
        // Arrange: save a non-default direction.
        _store.SaveGradientDirection(GradientDirection.BrightToDeep);

        // Act: overwrite the three AppSettings fields (simulates the user changing
        // the poll interval in SettingsPage, which calls SaveAppSettings).
        var settings = new AppSettings
        {
            ThemeId = "ember",
            PollIntervalSeconds = 300,
            OfflineOnly = false,
        };
        _store.SaveAppSettings(settings);

        // Assert: the direction must NOT have been reset.
        var reloaded = _store.LoadGradientDirection();
        Assert.Equal(GradientDirection.BrightToDeep, reloaded);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 2 — AppSettings round-trip + direction independence
    // SaveAppSettings / LoadAppSettings must round-trip their three fields;
    // LoadGradientDirection must still return its independently saved value.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AppSettingsRoundTripAndDirectionIsIndependent()
    {
        // Arrange: independently save a direction.
        _store.SaveGradientDirection(GradientDirection.BrightToDeep);

        // Act: save and reload AppSettings.
        var original = new AppSettings
        {
            ThemeId = "glacier",
            PollIntervalSeconds = 600,
            OfflineOnly = true,
        };
        _store.SaveAppSettings(original);
        var loaded = _store.LoadAppSettings();

        // Assert AppSettings round-trip.
        Assert.Equal("glacier", loaded.ThemeId);
        Assert.Equal(600, loaded.PollIntervalSeconds);
        Assert.True(loaded.OfflineOnly);

        // Assert direction was NOT touched by SaveAppSettings.
        Assert.Equal(GradientDirection.BrightToDeep, _store.LoadGradientDirection());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 3 — SaveThemeId / LoadThemeId round-trip independent of the others
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThemeIdRoundTripIsIndependentOfOtherFields()
    {
        // Arrange: prime the file with AppSettings and a direction.
        _store.SaveAppSettings(new AppSettings { ThemeId = "aurora", PollIntervalSeconds = 180, OfflineOnly = false });
        _store.SaveGradientDirection(GradientDirection.DeepToBright);

        // Act: overwrite just the ThemeId via the IThemeStore path.
        _store.SaveThemeId("nebula");

        // Assert: IThemeStore LoadThemeId returns the new value.
        Assert.Equal("nebula", _store.LoadThemeId());

        // Assert: the other fields are untouched.
        var settings = _store.LoadAppSettings();
        Assert.Equal(180, settings.PollIntervalSeconds);
        Assert.False(settings.OfflineOnly);

        Assert.Equal(GradientDirection.DeepToBright, _store.LoadGradientDirection());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 4 (optional) — corrupt-file tolerance
    // LoadAppSettings must return defaults and must not throw when the file
    // contains garbage JSON.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CorruptFileReturnsDefaultsAndDoesNotThrow()
    {
        // Arrange: write garbage to the settings file.
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(_store.FilePath, "THIS IS NOT JSON {{{{{{");

        // Act + Assert: no exception, returns default AppSettings.
        var ex = Record.Exception(() =>
        {
            var settings = _store.LoadAppSettings();
            Assert.Equal("aurora", settings.ThemeId);   // AppSettings default
            Assert.Equal(300, settings.PollIntervalSeconds); // AppSettings default (_pollIntervalSeconds = 300)
            Assert.False(settings.OfflineOnly);
        });
        Assert.Null(ex);
    }
}
