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
/// Persists and retrieves the most recent <see cref="UsageSnapshot"/> to/from disk,
/// keyed by provider identifier.
///
/// <para>
/// Per-provider file naming: <c>last-snapshot-{providerId}.json</c>.
/// The legacy file <c>last-snapshot.json</c> (written by v0.x with a single provider)
/// is ignored; the first warm-start will simply produce no cached value for "claude"
/// until the provider writes a keyed file.
/// </para>
/// </summary>
public interface ISnapshotCache
{
    /// <summary>
    /// Loads the last saved snapshot for <paramref name="providerId"/> from disk.
    /// Returns <c>null</c> if no file exists or the file cannot be read/deserialized.
    /// Never throws.
    /// </summary>
    UsageSnapshot? LoadLast(string providerId);

    /// <summary>
    /// Saves <paramref name="snapshot"/> for <paramref name="providerId"/> to disk,
    /// creating the cache directory if needed.
    /// Failures are swallowed — the cache is best-effort.
    /// </summary>
    void SaveLast(string providerId, UsageSnapshot snapshot);
}

/// <summary>
/// File-backed implementation of <see cref="ISnapshotCache"/>.
/// Default storage path: <c>%LOCALAPPDATA%\Fluentometer\last-snapshot-{providerId}.json</c>.
/// </summary>
public sealed class SnapshotCache : ISnapshotCache
{
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

    private static string FileName(string providerId) => $"last-snapshot-{providerId}.json";

    /// <inheritdoc/>
    public UsageSnapshot? LoadLast(string providerId)
    {
        try
        {
            var path = Path.Combine(_directory, FileName(providerId));
            var bytes = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize(bytes, SnapshotJsonContext.Default.UsageSnapshot);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public void SaveLast(string providerId, UsageSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, FileName(providerId));
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
