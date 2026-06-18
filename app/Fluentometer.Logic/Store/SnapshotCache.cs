using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fluentometer.Logic.Ipc;

namespace Fluentometer.Logic.Store;

/// <summary>
/// Source-generated JSON context for <see cref="UsageSnapshot"/> / <see cref="Gauge"/>
/// used by <see cref="SnapshotCache"/>. Uses camelCase property names to match the
/// rest of the codebase's wire format.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(UsageSnapshot))]
[JsonSerializable(typeof(Gauge))]
[JsonSerializable(typeof(List<Gauge>))]
internal partial class SnapshotJsonContext : JsonSerializerContext;

/// <summary>
/// Persists and retrieves the most recent <see cref="UsageSnapshot"/> to/from disk.
/// </summary>
public interface ISnapshotCache
{
    /// <summary>
    /// Loads the last saved snapshot from disk.
    /// Returns <c>null</c> if no file exists or the file cannot be read/deserialized.
    /// Never throws.
    /// </summary>
    UsageSnapshot? LoadLast();

    /// <summary>
    /// Saves <paramref name="snapshot"/> to disk, creating the cache directory if needed.
    /// Failures are swallowed — the cache is best-effort.
    /// </summary>
    void SaveLast(UsageSnapshot snapshot);
}

/// <summary>
/// File-backed implementation of <see cref="ISnapshotCache"/>.
/// Default storage path: <c>%LOCALAPPDATA%\Fluentometer\last-snapshot.json</c>.
/// </summary>
public sealed class SnapshotCache : ISnapshotCache
{
    private const string FileName = "last-snapshot.json";

    private readonly string _directory;

    /// <summary>
    /// Creates a <see cref="SnapshotCache"/> that stores files in
    /// <paramref name="directory"/> (defaults to
    /// <c>%LOCALAPPDATA%\Fluentometer</c> when <c>null</c>).
    /// </summary>
    public SnapshotCache(string? directory = null)
    {
        _directory = directory ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Fluentometer");
    }

    /// <inheritdoc/>
    public UsageSnapshot? LoadLast()
    {
        try
        {
            var path = Path.Combine(_directory, FileName);
            var bytes = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize(bytes, SnapshotJsonContext.Default.UsageSnapshot);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public void SaveLast(UsageSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, FileName);
            var json = JsonSerializer.SerializeToUtf8Bytes(
                snapshot,
                SnapshotJsonContext.Default.UsageSnapshot);
            File.WriteAllBytes(path, json);
        }
        catch
        {
            // Best-effort cache write — swallow all errors.
        }
    }
}
