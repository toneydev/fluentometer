using System;
using System.IO;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Single source of truth for resolving Codex CLI file paths.
/// </summary>
/// <remarks>
/// P1-A: the <c>CODEX_HOME</c> environment variable value is used only to build
/// a file-system path and is NEVER forwarded to any log sink.
/// </remarks>
internal static class CodexPaths
{
    /// <summary>
    /// Resolves the path to the Codex CLI auth file, honoring the
    /// <c>CODEX_HOME</c> environment variable override.
    /// <para>
    /// When <c>CODEX_HOME</c> is set (non-null, non-empty), it is used as the
    /// base directory; otherwise <c>%USERPROFILE%\.codex</c> is used.
    /// Returns <c>&lt;base&gt;\auth.json</c>.
    /// </para>
    /// <para>
    /// P1-A: the resolved path is used but never logged. This method does NOT
    /// read the file — only the path string is returned.
    /// </para>
    /// </summary>
    public static string ResolveAuthPath()
    {
        // P1-A: read the env var but never log its value.
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        string baseDir;
        if (string.IsNullOrEmpty(codexHome))
        {
            // Default: %USERPROFILE%\.codex
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }
        else
        {
            // P1-A: use the env value as the base directory; never log it.
            baseDir = codexHome;
        }

        return Path.Combine(baseDir, "auth.json");
    }
}
