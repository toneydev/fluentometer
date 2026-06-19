using System;
using System.IO;
using Fluentometer.Logic.Capture;
using Xunit;

namespace Fluentometer.Tests.Capture;

[CollectionDefinition(nameof(CodexCredentialsCollection), DisableParallelization = true)]
public sealed class CodexCredentialsCollection { }

[Collection(nameof(CodexCredentialsCollection))]
public sealed class CodexCredentialsTests : IDisposable
{
    // SECURITY: no real token. Clearly-fake placeholder values (G-3 posture).
    private const string ValidAuthJson = """
        {
          "auth_mode": "chatgpt",
          "tokens": {
            "access_token": "REDACTED-ACCESS-TOKEN-PLACEHOLDER",
            "refresh_token": "REDACTED-REFRESH-TOKEN-PLACEHOLDER",
            "account_id": "REDACTED-ACCOUNT-ID-PLACEHOLDER",
            "id_token": {
              "chatgpt_plan_type": "plus"
            }
          }
        }
        """;

    private readonly string _tempDir;
    private readonly string _authPath;

    public CodexCredentialsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CodexCredTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _authPath = Path.Combine(_tempDir, "auth.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // 1. Valid file → Ok with RedactedString-wrapped token and account_id
    [Fact]
    public void Read_ValidFile_ReturnsOkWithRedactedFields()
    {
        File.WriteAllText(_authPath, ValidAuthJson);
        var reader = new CodexCredentialReader(_authPath);

        var result = reader.Read();

        Assert.Equal(CodexCredentialStatus.Ok, result.Status);
        Assert.NotNull(result.Credential);
        Assert.Equal("REDACTED-ACCESS-TOKEN-PLACEHOLDER", result.Credential!.AccessToken.Expose());
        Assert.Equal("REDACTED-ACCOUNT-ID-PLACEHOLDER", result.Credential!.AccountId.Expose());
        Assert.Equal("plus", result.Credential!.PlanType);
    }

    // 2. P1-C: ToString() of credential fields never returns the raw value
    [Fact]
    public void Read_ValidFile_CredentialToStringIsRedacted()
    {
        File.WriteAllText(_authPath, ValidAuthJson);
        var reader = new CodexCredentialReader(_authPath);
        var result = reader.Read();

        Assert.Equal("***", result.Credential!.AccessToken.ToString());
        Assert.Equal("***", result.Credential!.AccountId.ToString());
    }

    // 3. Missing file → NotFound
    [Fact]
    public void Read_MissingFile_ReturnsNotFound()
    {
        var reader = new CodexCredentialReader(Path.Combine(_tempDir, "no-such-file.json"));
        var result = reader.Read();
        Assert.Equal(CodexCredentialStatus.NotFound, result.Status);
        Assert.Null(result.Credential);
    }

    // 4. Malformed JSON → ParseError
    [Fact]
    public void Read_MalformedJson_ReturnsParseError()
    {
        File.WriteAllText(_authPath, "not-valid-json{{{{");
        var reader = new CodexCredentialReader(_authPath);
        var result = reader.Read();
        Assert.Equal(CodexCredentialStatus.ParseError, result.Status);
    }

    // 5. Missing access_token field → ParseError
    [Fact]
    public void Read_MissingAccessToken_ReturnsParseError()
    {
        File.WriteAllText(_authPath, """{"auth_mode":"chatgpt","tokens":{"account_id":"x"}}""");
        var reader = new CodexCredentialReader(_authPath);
        var result = reader.Read();
        Assert.Equal(CodexCredentialStatus.ParseError, result.Status);
    }

    // 6. Missing account_id field → ParseError
    [Fact]
    public void Read_MissingAccountId_ReturnsParseError()
    {
        File.WriteAllText(_authPath, """{"auth_mode":"chatgpt","tokens":{"access_token":"tok"}}""");
        var reader = new CodexCredentialReader(_authPath);
        var result = reader.Read();
        Assert.Equal(CodexCredentialStatus.ParseError, result.Status);
    }

    // 7. P1-B: reparse-point guard — normal file must succeed (guard doesn't falsely reject)
    [Fact]
    public void Read_NormalFile_ReparseGuardDoesNotFalselyReject()
    {
        File.WriteAllText(_authPath, ValidAuthJson);
        var reader = new CodexCredentialReader(_authPath);
        var result = reader.Read();
        Assert.Equal(CodexCredentialStatus.Ok, result.Status);
    }

    // 8. plan_type absent → Ok with null PlanType (non-blocking)
    [Fact]
    public void Read_MissingPlanType_ReturnsOkWithNullPlanType()
    {
        File.WriteAllText(_authPath, """
            {"auth_mode":"chatgpt","tokens":{"access_token":"tok","account_id":"acct"}}
            """);
        var reader = new CodexCredentialReader(_authPath);
        var result = reader.Read();
        Assert.Equal(CodexCredentialStatus.Ok, result.Status);
        Assert.Null(result.Credential!.PlanType);
    }
}
