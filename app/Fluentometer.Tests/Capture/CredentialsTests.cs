using System;
using System.IO;
using Fluentometer.Logic.Capture;
using Xunit;

namespace Fluentometer.Tests.Capture;

[CollectionDefinition(nameof(CredentialsCollection), DisableParallelization = true)]
public sealed class CredentialsCollection { }

[Collection(nameof(CredentialsCollection))]
public sealed class CredentialsTests : IDisposable
{
    private readonly TempDirFixture _fixture = new("ClaudeCredTests");
    private readonly string _credPath;

    public CredentialsTests()
    {
        _credPath = Path.Combine(_fixture.TempDir, "credentials.json");
    }

    public void Dispose() => _fixture.Dispose();

    // ── Credential loading ────────────────────────────────────────────────────

    [Fact]
    public void LoadsFullCredential()
    {
        const string json =
            """{"claudeAiOauth":{"accessToken":"sk-tok","refreshToken":"rf-tok","expiresAt":1700000000000,"subscriptionType":"max"}}""";
        File.WriteAllText(_credPath, json);
        var reader = new ClaudeCredentialReader(_credPath);
        var result = reader.Read();

        Assert.Equal(CredentialStatus.Ok, result.Status);
        Assert.NotNull(result.Credential);
        Assert.Equal("sk-tok", result.Credential.AccessToken.Expose());
        Assert.NotNull(result.Credential.RefreshToken);
        Assert.Equal("rf-tok", result.Credential.RefreshToken!.Expose());
        Assert.Equal(1700000000000L, result.Credential.ExpiresAtMs);
        Assert.Equal("max", result.Credential.SubscriptionType);
    }

    [Fact]
    public void LoadsCredentialWithNullOptionalFields()
    {
        const string json =
            """{"claudeAiOauth":{"accessToken":"sk-tok2","expiresAt":1700000000000}}""";
        File.WriteAllText(_credPath, json);
        var reader = new ClaudeCredentialReader(_credPath);
        var result = reader.Read();

        Assert.Equal(CredentialStatus.Ok, result.Status);
        Assert.NotNull(result.Credential);
        Assert.Equal("sk-tok2", result.Credential.AccessToken.Expose());
        Assert.Null(result.Credential.RefreshToken);
        Assert.Null(result.Credential.SubscriptionType);
    }

    // ── Error cases ───────────────────────────────────────────────────────────

    [Fact]
    public void MissingFileReturnsNotFound()
    {
        var reader = new ClaudeCredentialReader(
            Path.Combine(_fixture.TempDir, "does-not-exist-" + Guid.NewGuid() + ".json"));

        var result = reader.Read();

        Assert.Equal(CredentialStatus.NotFound, result.Status);
        Assert.Null(result.Credential);
    }

    [Fact]
    public void MalformedJsonReturnsParseError()
    {
        File.WriteAllText(_credPath, "this is not json {{{{");
        var reader = new ClaudeCredentialReader(_credPath);
        var result = reader.Read();

        Assert.Equal(CredentialStatus.ParseError, result.Status);
        Assert.Null(result.Credential);
    }

    [Fact]
    public void MissingClaudeAiOauthKeyReturnsParseError()
    {
        File.WriteAllText(_credPath, """{"someOtherKey":{"accessToken":"tok","expiresAt":1}}""");
        var reader = new ClaudeCredentialReader(_credPath);
        var result = reader.Read();

        Assert.Equal(CredentialStatus.ParseError, result.Status);
    }

    // ── IsExpired boundary (Claude-specific: expiresAt in milliseconds) ───────

    [Fact]
    public void IsExpiredReturnsFalseJustBeforeExpiry()
    {
        const string json =
            """{"claudeAiOauth":{"accessToken":"tok","expiresAt":1700000000000,"subscriptionType":null}}""";
        File.WriteAllText(_credPath, json);
        var result = new ClaudeCredentialReader(_credPath).Read();
        var cred = result.Credential!;

        // 1 ms before expiry → NOT expired
        Assert.False(cred.IsExpired(1_699_999_999_999L));
    }

    [Fact]
    public void IsExpiredReturnsTrueAtExpiryMs()
    {
        const string json =
            """{"claudeAiOauth":{"accessToken":"tok","expiresAt":1700000000000,"subscriptionType":null}}""";
        File.WriteAllText(_credPath, json);
        var result = new ClaudeCredentialReader(_credPath).Read();
        var cred = result.Credential!;

        // Exactly at expiry → expired (nowMs >= expiresAtMs)
        Assert.True(cred.IsExpired(1_700_000_000_000L));
    }

    [Fact]
    public void IsExpiredReturnsTrueAfterExpiry()
    {
        const string json =
            """{"claudeAiOauth":{"accessToken":"tok","expiresAt":1700000000000}}""";
        File.WriteAllText(_credPath, json);
        var result = new ClaudeCredentialReader(_credPath).Read();
        var cred = result.Credential!;

        Assert.True(cred.IsExpired(1_700_000_001_000L));
    }
}
