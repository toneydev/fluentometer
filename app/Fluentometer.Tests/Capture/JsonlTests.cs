using System;
using System.IO;
using Fluentometer.Logic.Capture;
using Xunit;

/// <summary>
/// Tests for <see cref="JsonlReader.ParseLine"/> and
/// <see cref="JsonlReader.CollectEvents"/>.
/// </summary>
public class JsonlTests
{
    // ────────────────────────────────────────────────────────────────────────
    // ParseLine
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseLine_AssistantUsageLine_ReturnsCorrectTotalAndTimestamp()
    {
        // input=100, output=50, cache_read=10 → 160 tokens
        // 2026-06-16T10:00:00Z = Unix 1781604000
        const string line =
            """{"type":"assistant","timestamp":"2026-06-16T10:00:00Z","message":{"usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":10}}}""";

        var ev = JsonlReader.ParseLine(line);

        Assert.NotNull(ev);
        Assert.Equal(160L, ev!.Value.TotalTokens);
        Assert.Equal(1_781_604_000L, ev.Value.TsUnix);
    }

    [Fact]
    public void ParseLine_AllFourTokenFieldsPresent_SumsAll()
    {
        // input=100, output=50, cache_creation=30, cache_read=20 → 200 total
        const string line =
            """{"timestamp":"2026-06-16T10:00:00Z","message":{"usage":{"input_tokens":100,"output_tokens":50,"cache_creation_input_tokens":30,"cache_read_input_tokens":20}}}""";

        var ev = JsonlReader.ParseLine(line);

        Assert.NotNull(ev);
        Assert.Equal(200L, ev!.Value.TotalTokens);
    }

    [Fact]
    public void ParseLine_OnlyOutputTokensPresent_AbsentFieldsDefaultToZero()
    {
        const string line =
            """{"timestamp":"2026-06-16T10:00:00Z","message":{"usage":{"output_tokens":77}}}""";

        var ev = JsonlReader.ParseLine(line);

        Assert.NotNull(ev);
        Assert.Equal(77L, ev!.Value.TotalTokens);
    }

    [Theory]
    [InlineData("""{"type":"user","timestamp":"2026-06-16T10:00:00Z"}""")]             // no message.usage
    [InlineData("not json at all")]                                                     // malformed
    [InlineData("")]                                                                    // empty
    [InlineData("   ")]                                                                 // whitespace only
    [InlineData("""{"type":"assistant"}""")]                                             // no usage/timestamp
    [InlineData("""{"timestamp":"2026-06-16T10:00:00Z","message":{"role":"user"}}""")] // message but no usage
    [InlineData("""{"message":{"usage":{"input_tokens":1}}}""")]                        // missing timestamp
    [InlineData("""{"timestamp":"not-a-date","message":{"usage":{"input_tokens":1}}}""")]// bad timestamp
    public void ParseLine_NonUsageOrMalformedLine_ReturnsNull(string line)
    {
        var ev = JsonlReader.ParseLine(line);
        Assert.Null(ev);
    }

    [Fact]
    public void ParseLine_RoundtripTimezoneOffset_ConvertsToUtcUnixSeconds()
    {
        // 2026-06-16T12:00:00+02:00 == 2026-06-16T10:00:00Z == 1781604000
        const string line =
            """{"timestamp":"2026-06-16T12:00:00+02:00","message":{"usage":{"input_tokens":10}}}""";

        var ev = JsonlReader.ParseLine(line);

        Assert.NotNull(ev);
        Assert.Equal(1_781_604_000L, ev!.Value.TsUnix);
    }

    // ────────────────────────────────────────────────────────────────────────
    // CollectEvents
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CollectEvents_MissingDirectory_ReturnsEmpty()
    {
        var reader = new JsonlReader();
        var result = reader.CollectEvents(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        Assert.Empty(result);
    }

    [Fact]
    public void CollectEvents_EmptyDirectory_ReturnsEmpty()
    {
        var dir = CreateTempDir();
        try
        {
            var result = new JsonlReader().CollectEvents(dir);
            Assert.Empty(result);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CollectEvents_SingleFileInRoot_ReturnsEvents()
    {
        var dir = CreateTempDir();
        try
        {
            WriteJsonl(Path.Combine(dir, "session.jsonl"), new[]
            {
                """{"timestamp":"2026-06-16T10:00:00Z","message":{"usage":{"input_tokens":100,"output_tokens":50}}}""",
                """{"timestamp":"2026-06-16T10:01:00Z","message":{"usage":{"input_tokens":200}}}""",
            });

            var result = new JsonlReader().CollectEvents(dir);

            Assert.Equal(2, result.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CollectEvents_NestedSubfolders_FindsEventsInAllLevels()
    {
        // Exercises recursive traversal across nested subfolders.
        var dir = CreateTempDir();
        try
        {
            // Root file
            WriteJsonl(Path.Combine(dir, "a.jsonl"), new[]
            {
                """{"timestamp":"2026-06-16T10:00:00Z","message":{"usage":{"input_tokens":10}}}""",
            });

            // One-level deep
            var sub1 = Path.Combine(dir, "project1");
            Directory.CreateDirectory(sub1);
            WriteJsonl(Path.Combine(sub1, "b.jsonl"), new[]
            {
                """{"timestamp":"2026-06-16T11:00:00Z","message":{"usage":{"output_tokens":20}}}""",
            });

            // Two-levels deep
            var sub2 = Path.Combine(sub1, "session");
            Directory.CreateDirectory(sub2);
            WriteJsonl(Path.Combine(sub2, "c.jsonl"), new[]
            {
                """{"timestamp":"2026-06-16T12:00:00Z","message":{"usage":{"input_tokens":5,"output_tokens":5}}}""",
                // malformed line mixed in — should be skipped
                "not json",
            });

            var result = new JsonlReader().CollectEvents(dir);

            // 1 (a) + 1 (b) + 1 (c, good line) = 3 events; malformed skipped
            Assert.Equal(3, result.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CollectEvents_NonJsonlFilesIgnored()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "notes.txt"),
                """{"timestamp":"2026-06-16T10:00:00Z","message":{"usage":{"input_tokens":99}}}""");
            File.WriteAllText(Path.Combine(dir, "data.json"),
                """{"timestamp":"2026-06-16T10:00:00Z","message":{"usage":{"input_tokens":99}}}""");
            WriteJsonl(Path.Combine(dir, "session.jsonl"), new[]
            {
                """{"timestamp":"2026-06-16T10:00:00Z","message":{"usage":{"input_tokens":1}}}""",
            });

            var result = new JsonlReader().CollectEvents(dir);

            // Only the .jsonl file is read
            Assert.Single(result);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ────────────────────────────────────────────────────────────────────────
    // ClaudePaths
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClaudePaths_ProjectsDir_EndsWithDotClaudeProjects()
    {
        // Verify the path ends with \.claude\projects regardless of the actual profile.
        var dir = ClaudePaths.ProjectsDir;
        Assert.True(
            dir.EndsWith(Path.Combine(".claude", "projects"), StringComparison.OrdinalIgnoreCase),
            $"Expected path to end with '.claude\\projects' but was: {dir}");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluentometer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteJsonl(string path, string[] lines)
        => File.WriteAllText(path, string.Join('\n', lines));
}
