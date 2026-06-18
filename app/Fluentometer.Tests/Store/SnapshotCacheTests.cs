using System;
using System.Collections.Generic;
using System.IO;
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.Store;
using Xunit;

/// <summary>
/// Tests for <see cref="SnapshotCache"/>.
/// Uses temp directories created via <see cref="Path.GetTempPath"/> — no external fixture files.
/// </summary>
public class SnapshotCacheTests
{
    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static string CreateTempDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "fluentometer-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static UsageSnapshot MakeSnapshot(IReadOnlyList<Gauge>? gauges = null) =>
        new(
            Provider: "claude",
            CapturedAt: 1_750_000_000L,
            Source: "jsonl",
            Health: "degraded",
            Plan: "Max",
            Gauges: gauges ?? Array.Empty<Gauge>());

    // ────────────────────────────────────────────────────────────────────────
    // Round-trip persistence tests
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SaveLast_ThenLoadLast_RoundTripsEqualSnapshot()
    {
        var dir = CreateTempDir();
        try
        {
            var cache = new SnapshotCache(dir);
            var snap = MakeSnapshot();

            cache.SaveLast(snap);
            var loaded = cache.LoadLast();

            // UsageSnapshot is a record; IReadOnlyList<Gauge> is compared by reference,
            // so assert field-by-field rather than whole-object equality.
            Assert.NotNull(loaded);
            Assert.Equal(snap.Provider, loaded!.Provider);
            Assert.Equal(snap.CapturedAt, loaded.CapturedAt);
            Assert.Equal(snap.Source, loaded.Source);
            Assert.Equal(snap.Health, loaded.Health);
            Assert.Equal(snap.Plan, loaded.Plan);
            Assert.Empty(loaded.Gauges);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadLast_EmptyDirectory_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            var cache = new SnapshotCache(dir);
            var result = cache.LoadLast();
            Assert.Null(result);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Additional correctness tests
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SaveLast_CreatesDirectoryIfMissing()
    {
        // Start with a path that does not yet exist.
        var parent = Path.Combine(Path.GetTempPath(), "fm-cache-newdir-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(parent, "Fluentometer");
        try
        {
            Assert.False(Directory.Exists(dir));

            var cache = new SnapshotCache(dir);
            cache.SaveLast(MakeSnapshot());

            Assert.True(Directory.Exists(dir));
            Assert.True(File.Exists(Path.Combine(dir, "last-snapshot.json")));
        }
        finally
        {
            if (Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void SaveLast_OverwritesPreviousFile()
    {
        var dir = CreateTempDir();
        try
        {
            var cache = new SnapshotCache(dir);

            var first = MakeSnapshot() with { Plan = "Pro" };
            var second = MakeSnapshot() with { Plan = "Max" };

            cache.SaveLast(first);
            cache.SaveLast(second);

            var loaded = cache.LoadLast();
            Assert.NotNull(loaded);
            Assert.Equal("Max", loaded!.Plan);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SaveLast_ThenLoadLast_RoundTripsGaugeWithNullUtilization()
    {
        // Gauges may carry null Utilization (estimate-only mode).
        var dir = CreateTempDir();
        try
        {
            var cache = new SnapshotCache(dir);
            var gauge = new Gauge(
                Id: "weekly_all",
                Label: "Claude Weekly",
                Utilization: null,
                UsedLabel: "~1.2M",
                ResetsAt: null,
                LimitLabel: "estimate");
            var snap = MakeSnapshot(new List<Gauge> { gauge });

            cache.SaveLast(snap);
            var loaded = cache.LoadLast();

            Assert.NotNull(loaded);
            Assert.Single(loaded!.Gauges);
            Assert.Null(loaded.Gauges[0].Utilization);
            Assert.Null(loaded.Gauges[0].ResetsAt);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SaveLast_ThenLoadLast_RoundTripsMultipleGauges()
    {
        var dir = CreateTempDir();
        try
        {
            var cache = new SnapshotCache(dir);
            var gauges = new List<Gauge>
            {
                new("session",    "Claude 5-hour",  0.42,  "42%",   1_750_018_000L, "5-hour limit"),
                new("weekly_all", "Claude Weekly",  0.15,  "15%",   1_750_604_800L, "weekly limit"),
                new("weekly_scoped", "Claude Weekly (Sonnet)", null, "~800k", null, "estimate"),
            };
            var snap = MakeSnapshot(gauges);

            cache.SaveLast(snap);
            var loaded = cache.LoadLast();

            Assert.NotNull(loaded);
            Assert.Equal(3, loaded!.Gauges.Count);
            Assert.Equal(0.42, loaded.Gauges[0].Utilization);
            Assert.Equal("weekly limit", loaded.Gauges[1].LimitLabel);
            Assert.Null(loaded.Gauges[2].Utilization);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadLast_CorruptFile_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "last-snapshot.json"), "not valid json{{{");
            var cache = new SnapshotCache(dir);

            var result = cache.LoadLast();

            Assert.Null(result);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadLast_EmptyFile_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "last-snapshot.json"), "");
            var cache = new SnapshotCache(dir);

            var result = cache.LoadLast();

            Assert.Null(result);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void DefaultConstructor_UsesLocalAppDataFluentometer()
    {
        // Verify the default directory path is under %LOCALAPPDATA%\Fluentometer.
        // Inspect the private field via reflection to avoid writing to the real path in tests.
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fluentometer");

        var cache = new SnapshotCache();
        var field = typeof(SnapshotCache)
            .GetField("_directory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var actual = (string?)field?.GetValue(cache);

        Assert.Equal(expected, actual, StringComparer.OrdinalIgnoreCase);
    }
}
