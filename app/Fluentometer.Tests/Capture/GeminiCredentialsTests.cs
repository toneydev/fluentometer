using System;
using System.IO;
using Fluentometer.Logic.Capture;
using Xunit;

namespace Fluentometer.Tests.Capture;

[CollectionDefinition(nameof(GeminiCredentialsCollection), DisableParallelization = true)]
public sealed class GeminiCredentialsCollection { }

[Collection(nameof(GeminiCredentialsCollection))]
public sealed class GeminiCredentialsTests : IDisposable
{
    // SECURITY: no real token — clearly-fake placeholder (G-3 posture).
    private const string ValidJson = """
        {
          "access_token": "REDACTED-ACCESS-TOKEN-PLACEHOLDER",
          "refresh_token": "REDACTED-REFRESH-TOKEN-PLACEHOLDER",
          "id_token": "REDACTED-ID-TOKEN-PLACEHOLDER",
          "expiry_date": 1700000000000,
          "token_type": "Bearer"
        }
        """;

    private readonly string _tempDir;
    private readonly string _credPath;

    public GeminiCredentialsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GeminiCredTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _credPath = Path.Combine(_tempDir, "oauth_creds.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // 1. Valid file → Ok, RedactedString-wrapped token, parsed expiry
    [Fact]
    public void Read_ValidFile_ReturnsOkWithRedactedTokenAndExpiry()
    {
        File.WriteAllText(_credPath, ValidJson);
        var reader = new GeminiCredentialReader(_credPath);

        var result = reader.Read();

        Assert.Equal(GeminiCredentialStatus.Ok, result.Status);
        Assert.NotNull(result.Credential);
        Assert.Equal("REDACTED-ACCESS-TOKEN-PLACEHOLDER", result.Credential!.AccessToken.Expose());
        Assert.Equal(1700000000000L, result.Credential!.ExpiresAtMs);
    }

    // 2. P1-C: ToString() of the wrapped token is redacted
    [Fact]
    public void Read_ValidFile_TokenToStringIsRedacted()
    {
        File.WriteAllText(_credPath, ValidJson);
        var result = new GeminiCredentialReader(_credPath).Read();
        Assert.Equal("***", result.Credential!.AccessToken.ToString());
    }

    // 3. Missing file → NotFound
    [Fact]
    public void Read_MissingFile_ReturnsNotFound()
    {
        var result = new GeminiCredentialReader(Path.Combine(_tempDir, "nope.json")).Read();
        Assert.Equal(GeminiCredentialStatus.NotFound, result.Status);
        Assert.Null(result.Credential);
    }

    // 4. Malformed JSON → ParseError
    [Fact]
    public void Read_MalformedJson_ReturnsParseError()
    {
        File.WriteAllText(_credPath, "not-json{{{{");
        var result = new GeminiCredentialReader(_credPath).Read();
        Assert.Equal(GeminiCredentialStatus.ParseError, result.Status);
    }

    // 5. Missing access_token → ParseError
    [Fact]
    public void Read_MissingAccessToken_ReturnsParseError()
    {
        File.WriteAllText(_credPath, """{"refresh_token":"x","expiry_date":1700000000000}""");
        var result = new GeminiCredentialReader(_credPath).Read();
        Assert.Equal(GeminiCredentialStatus.ParseError, result.Status);
    }

    // 6. Missing expiry_date → Ok with ExpiresAtMs == 0 (safe default → provider treats as expired)
    [Fact]
    public void Read_MissingExpiry_ReturnsOkWithZeroExpiry()
    {
        File.WriteAllText(_credPath, """{"access_token":"tok"}""");
        var result = new GeminiCredentialReader(_credPath).Read();
        Assert.Equal(GeminiCredentialStatus.Ok, result.Status);
        Assert.Equal(0L, result.Credential!.ExpiresAtMs);
    }

    // 7. IsExpired math
    [Fact]
    public void IsExpired_BeforeAndAfter()
    {
        var cred = new GeminiCredential(new RedactedString("tok"), 1000L);
        Assert.False(cred.IsExpired(999L));
        Assert.True(cred.IsExpired(1000L));
        Assert.True(cred.IsExpired(1001L));
    }

    // 8. P1-B: normal (non-reparse) file is accepted — confirms guard doesn't false-reject
    [Fact]
    public void Read_NormalFile_NotRejectedByReparseGuard()
    {
        File.WriteAllText(_credPath, ValidJson);
        var result = new GeminiCredentialReader(_credPath).Read();
        Assert.Equal(GeminiCredentialStatus.Ok, result.Status);
    }
}
