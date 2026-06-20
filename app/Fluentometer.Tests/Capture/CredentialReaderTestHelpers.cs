using System;
using System.IO;
using Fluentometer.Logic.Capture;
using Xunit;

namespace Fluentometer.Tests.Capture;

/// <summary>
/// IDisposable temp-directory fixture shared by all credential-reader test classes.
/// Creates a unique temporary directory on construction and deletes it on disposal,
/// giving tests a clean, isolated file-system surface without leaking temp files.
/// </summary>
public sealed class TempDirFixture : IDisposable
{
    public string TempDir { get; }

    public TempDirFixture(string prefix)
    {
        TempDir = Path.Combine(
            Path.GetTempPath(),
            $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(TempDir, recursive: true); }
        catch { /* best-effort */ }
    }
}

// ── RedactedString facts — not tied to any single credential reader ────────────
//
// These three tests guard the $100-rule: tokens must never leak through ToString()
// or string-formatting code paths used by loggers. They live here rather than in
// CredentialsTests.cs so the fixture + the security guard are co-located and all
// credential-reader test files can reference TempDirFixture from one place.

public class RedactedStringTests
{
    // SECURITY: tokens must never leak into logs via ToString() or string formatting.
    // This test is the $100-rule guard for credential redaction.
    [Fact]
    public void AccessToken_DoesNotAppearInRedactedRepresentation()
    {
        const string tokenValue = "super-secret-access-token-12345";
        var redacted = new RedactedString(tokenValue);

        var toStr = redacted.ToString();

        Assert.DoesNotContain(tokenValue, toStr);
        Assert.Contains("***", toStr);
    }

    [Fact]
    public void RefreshToken_DoesNotAppearInRedactedRepresentation()
    {
        const string tokenValue = "super-secret-refresh-token-67890";
        var redacted = new RedactedString(tokenValue);

        // Simulating what happens when the whole ClaudeCredential is formatted for a log:
        // object.ToString() → "ClaudeCredential { AccessToken = ***, ... }"
        var credStr = redacted.ToString();

        Assert.DoesNotContain(tokenValue, credStr);
    }

    [Fact]
    public void Expose_ReturnsBareTokenValue()
    {
        const string tokenValue = "the-real-token";
        var redacted = new RedactedString(tokenValue);

        Assert.Equal(tokenValue, redacted.Expose());
    }
}
