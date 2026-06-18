using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// A single JSONL usage event extracted from a Claude session file.
/// </summary>
public readonly record struct UsageEvent(long TsUnix, long TotalTokens);

/// <summary>
/// Reads usage events from Claude's local JSONL session files.
/// </summary>
public interface IJsonlReader
{
    IReadOnlyList<UsageEvent> CollectEvents(string projectsDir);
}

/// <summary>
/// Implements <see cref="IJsonlReader"/> by recursively walking the projects directory
/// and parsing every <c>*.jsonl</c> file.
/// </summary>
public sealed class JsonlReader : IJsonlReader
{
    /// <summary>
    /// Recursively collects all usage events from every <c>*.jsonl</c> file under
    /// <paramref name="projectsDir"/>. Returns an empty list if the directory is
    /// missing, unreadable, or contains no events. Never throws.
    /// </summary>
    public IReadOnlyList<UsageEvent> CollectEvents(string projectsDir)
    {
        var events = new List<UsageEvent>();
        try
        {
            CollectRecursive(projectsDir, events);
        }
        catch
        {
            // Swallow — per spec: missing/unreadable dir → empty list, never throw.
        }
        return events;
    }

    private static void CollectRecursive(string dir, List<UsageEvent> output)
    {
        if (!Directory.Exists(dir))
            return;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (Directory.Exists(entry))
                {
                    CollectRecursive(entry, output);
                }
                else if (entry.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    ParseSessionFile(entry, output);
                }
            }
        }
        catch
        {
            // Unreadable directory entry — skip silently.
        }
    }

    private static void ParseSessionFile(string path, List<UsageEvent> output)
    {
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch
        {
            return;
        }

        foreach (var line in content.Split('\n'))
        {
            var ev = ParseLine(line);
            if (ev.HasValue)
                output.Add(ev.Value);
        }
    }

    /// <summary>
    /// Parses a single JSONL line into a <see cref="UsageEvent"/>.
    /// Returns <c>null</c> if the line lacks <c>message.usage</c> or a valid
    /// RFC 3339 timestamp, or is otherwise malformed/empty.
    /// <para>
    /// Token sum = <c>input_tokens</c> + <c>output_tokens</c> +
    /// <c>cache_creation_input_tokens</c> + <c>cache_read_input_tokens</c>
    /// (each defaults to 0 if absent).
    /// </para>
    /// </summary>
    public static UsageEvent? ParseLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            // Require a valid RFC 3339 timestamp.
            if (!root.TryGetProperty("timestamp", out var tsProp) ||
                tsProp.ValueKind != JsonValueKind.String)
                return null;

            var tsStr = tsProp.GetString();
            if (tsStr is null)
                return null;

            if (!DateTimeOffset.TryParse(tsStr,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var dto))
                return null;

            // Require message.usage.
            if (!root.TryGetProperty("message", out var msgProp) ||
                msgProp.ValueKind != JsonValueKind.Object)
                return null;

            if (!msgProp.TryGetProperty("usage", out var usageProp) ||
                usageProp.ValueKind != JsonValueKind.Object)
                return null;

            long inputTokens = ReadLong(usageProp, "input_tokens");
            long outputTokens = ReadLong(usageProp, "output_tokens");
            long cacheCreationTokens = ReadLong(usageProp, "cache_creation_input_tokens");
            long cacheReadTokens = ReadLong(usageProp, "cache_read_input_tokens");

            long totalTokens = inputTokens + outputTokens + cacheCreationTokens + cacheReadTokens;

            return new UsageEvent(dto.ToUnixTimeSeconds(), totalTokens);
        }
        catch
        {
            return null;
        }
    }

    private static long ReadLong(JsonElement parent, string name)
    {
        if (parent.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetInt64(out var val))
            return val;
        return 0L;
    }
}

/// <summary>
/// Returns the canonical path for Claude's local project sessions directory:
/// <c>%USERPROFILE%\.claude\projects</c>.
/// </summary>
public static class ClaudePaths
{
    /// <summary>
    /// Full path to the Claude projects directory, e.g.
    /// <c>C:\Users\Kyle\.claude\projects</c>.
    /// </summary>
    public static string ProjectsDir { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects");
}
