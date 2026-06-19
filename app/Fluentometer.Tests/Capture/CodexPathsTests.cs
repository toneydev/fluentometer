using System;
using System.IO;
using Fluentometer.Logic.Capture;
using Xunit;

namespace Fluentometer.Tests.Capture;

/// <summary>
/// Tests for <see cref="CodexPaths.ResolveAuthPath"/>.
/// These tests mutate the process-global CODEX_HOME environment variable, so
/// they must not run in parallel with each other or with other tests that touch
/// that variable.
/// </summary>
[CollectionDefinition(nameof(CodexPathsCollection), DisableParallelization = true)]
public sealed class CodexPathsCollection { }

[Collection(nameof(CodexPathsCollection))]
public sealed class CodexPathsTests
{
    private const string CodexHomeVar = "CODEX_HOME";

    // ── 1. CODEX_HOME set → returns <that dir>\auth.json ─────────────────────

    [Fact]
    public void ResolveAuthPath_CodexHomeSet_ReturnsCodexHomePlusAuthJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "CodexPathsTest_" + Guid.NewGuid().ToString("N"));
        var original = Environment.GetEnvironmentVariable(CodexHomeVar);
        try
        {
            Environment.SetEnvironmentVariable(CodexHomeVar, tempDir);

            var result = CodexPaths.ResolveAuthPath();

            Assert.Equal(Path.Combine(tempDir, "auth.json"), result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodexHomeVar, original);
        }
    }

    // ── 2. CODEX_HOME unset (null) → returns %USERPROFILE%\.codex\auth.json ──

    [Fact]
    public void ResolveAuthPath_CodexHomeNull_ReturnsUserProfileDotCodexAuthJson()
    {
        var original = Environment.GetEnvironmentVariable(CodexHomeVar);
        try
        {
            Environment.SetEnvironmentVariable(CodexHomeVar, null);

            var result = CodexPaths.ResolveAuthPath();

            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex",
                "auth.json");
            Assert.Equal(expected, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodexHomeVar, original);
        }
    }

    // ── 3. CODEX_HOME set to empty string → falls back to default path ────────

    [Fact]
    public void ResolveAuthPath_CodexHomeEmpty_FallsBackToUserProfileDefault()
    {
        var original = Environment.GetEnvironmentVariable(CodexHomeVar);
        try
        {
            Environment.SetEnvironmentVariable(CodexHomeVar, string.Empty);

            var result = CodexPaths.ResolveAuthPath();

            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex",
                "auth.json");
            Assert.Equal(expected, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodexHomeVar, original);
        }
    }
}
