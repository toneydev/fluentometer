using System;
using System.IO;
using Fluentometer.Logic.Capture;
using Xunit;

namespace Fluentometer.Tests.Capture;

public class CredentialsTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Write <paramref name="json"/> to a temp file and return its path.
    /// The caller is responsible for deleting it; tests use try/finally.
    /// </summary>
    private static string WriteTempFile(string json)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        return path;
    }

    // ── Credential loading ────────────────────────────────────────────────────

    [Fact]
    public void LoadsFullCredential()
    {
        const string json =
            """{"claudeAiOauth":{"accessToken":"sk-tok","refreshToken":"rf-tok","expiresAt":1700000000000,"subscriptionType":"max"}}""";
        var path = WriteTempFile(json);
        try
        {
            var reader = new ClaudeCredentialReader(path);
            var result = reader.Read();

            Assert.Equal(CredentialStatus.Ok, result.Status);
            Assert.NotNull(result.Credential);
            Assert.Equal("sk-tok", result.Credential.AccessToken.Expose());
            Assert.NotNull(result.Credential.RefreshToken);
            Assert.Equal("rf-tok", result.Credential.RefreshToken!.Expose());
            Assert.Equal(1700000000000L, result.Credential.ExpiresAtMs);
            Assert.Equal("max", result.Credential.SubscriptionType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadsCredentialWithNullOptionalFields()
    {
        const string json =
            """{"claudeAiOauth":{"accessToken":"sk-tok2","expiresAt":1700000000000}}""";
        var path = WriteTempFile(json);
        try
        {
            var reader = new ClaudeCredentialReader(path);
            var result = reader.Read();

            Assert.Equal(CredentialStatus.Ok, result.Status);
            Assert.NotNull(result.Credential);
            Assert.Equal("sk-tok2", result.Credential.AccessToken.Expose());
            Assert.Null(result.Credential.RefreshToken);
            Assert.Null(result.Credential.SubscriptionType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Error cases ───────────────────────────────────────────────────────────

    [Fact]
    public void MissingFileReturnsNotFound()
    {
        var reader = new ClaudeCredentialReader(
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".json"));

        var result = reader.Read();

        Assert.Equal(CredentialStatus.NotFound, result.Status);
        Assert.Null(result.Credential);
    }

    [Fact]
    public void MalformedJsonReturnsParseError()
    {
        var path = WriteTempFile("this is not json {{{{");
        try
        {
            var reader = new ClaudeCredentialReader(path);
            var result = reader.Read();

            Assert.Equal(CredentialStatus.ParseError, result.Status);
            Assert.Null(result.Credential);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingClaudeAiOauthKeyReturnsParseError()
    {
        var path = WriteTempFile("""{"someOtherKey":{"accessToken":"tok","expiresAt":1}}""");
        try
        {
            var reader = new ClaudeCredentialReader(path);
            var result = reader.Read();

            Assert.Equal(CredentialStatus.ParseError, result.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── IsExpired boundary ────────────────────────────────────────────────────

    [Fact]
    public void IsExpiredReturnsFalseJustBeforeExpiry()
    {
        const string json =
            """{"claudeAiOauth":{"accessToken":"tok","expiresAt":1700000000000,"subscriptionType":null}}""";
        var path = WriteTempFile(json);
        try
        {
            var result = new ClaudeCredentialReader(path).Read();
            var cred = result.Credential!;

            // 1 ms before expiry → NOT expired
            Assert.False(cred.IsExpired(1_699_999_999_999L));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsExpiredReturnsTrueAtExpiryMs()
    {
        const string json =
            """{"claudeAiOauth":{"accessToken":"tok","expiresAt":1700000000000,"subscriptionType":null}}""";
        var path = WriteTempFile(json);
        try
        {
            var result = new ClaudeCredentialReader(path).Read();
            var cred = result.Credential!;

            // Exactly at expiry → expired (nowMs >= expiresAtMs)
            Assert.True(cred.IsExpired(1_700_000_000_000L));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsExpiredReturnsTrueAfterExpiry()
    {
        const string json =
            """{"claudeAiOauth":{"accessToken":"tok","expiresAt":1700000000000}}""";
        var path = WriteTempFile(json);
        try
        {
            var result = new ClaudeCredentialReader(path).Read();
            var cred = result.Credential!;

            Assert.True(cred.IsExpired(1_700_000_001_000L));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Redaction / secret safety ─────────────────────────────────────────────

    [Fact]
    public void AccessTokenDoesNotAppearInRedactedRepresentation()
    {
        // SECURITY: tokens must never leak into logs via ToString() or string formatting.
        // This test is the $100-rule guard for credential redaction.
        const string tokenValue = "super-secret-access-token-12345";
        var redacted = new RedactedString(tokenValue);

        var toStr = redacted.ToString();

        Assert.DoesNotContain(tokenValue, toStr);
        Assert.Contains("***", toStr);
    }

    [Fact]
    public void RefreshTokenDoesNotAppearInRedactedRepresentation()
    {
        const string tokenValue = "super-secret-refresh-token-67890";
        var redacted = new RedactedString(tokenValue);

        // Simulating what happens when the whole ClaudeCredential is formatted for a log:
        // object.ToString() → "ClaudeCredential { AccessToken = ***, ... }"
        var credStr = redacted.ToString();

        Assert.DoesNotContain(tokenValue, credStr);
    }

    [Fact]
    public void ExposeReturnsBareTokenValue()
    {
        const string tokenValue = "the-real-token";
        var redacted = new RedactedString(tokenValue);

        Assert.Equal(tokenValue, redacted.Expose());
    }
}
