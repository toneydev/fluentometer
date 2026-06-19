using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Capture;
using Xunit;

namespace Fluentometer.Tests.Capture;

[CollectionDefinition(nameof(ChatGptDetectorCollection), DisableParallelization = true)]
public sealed class ChatGptDetectorCollection { }

[Collection(nameof(ChatGptDetectorCollection))]
public sealed class ChatGptProviderDetectorTests : IDisposable
{
    // SECURITY: no real token. Detector never reads access_token (G-2).
    private const string ValidAuthJson = """
        {
          "auth_mode": "chatgpt",
          "tokens": { "account_id": "REDACTED-ACCOUNT-ID-PLACEHOLDER" }
        }
        """;

    private readonly string _tempDir;

    public ChatGptProviderDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ChatGptDetectorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteAuth(string json = ValidAuthJson)
    {
        var path = Path.Combine(_tempDir, "auth.json");
        File.WriteAllText(path, json);
        return path;
    }

    // 1. Valid auth.json with auth_mode=chatgpt → Detected
    [Fact]
    public async Task Detect_ValidFile_ChatGptMode_ReturnsDetected()
    {
        var path = WriteAuth();
        var detector = new ChatGptProviderDetector(path);
        var result = await detector.DetectAsync(CancellationToken.None);
        Assert.Equal(ProviderDetectionStatus.Detected, result.Status);
        Assert.Equal("ChatGPT", result.ProviderDisplayName);
    }

    // 2. Missing file → NotFound
    [Fact]
    public async Task Detect_MissingFile_ReturnsNotFound()
    {
        var path = Path.Combine(_tempDir, "no-such-file.json");
        var detector = new ChatGptProviderDetector(path);
        var result = await detector.DetectAsync(CancellationToken.None);
        Assert.Equal(ProviderDetectionStatus.NotFound, result.Status);
    }

    // 3. auth_mode = "api-key" → NotFound (API-key-only users excluded)
    [Fact]
    public async Task Detect_ApiKeyMode_ReturnsNotFound()
    {
        var path = WriteAuth("""{"auth_mode":"api-key","tokens":{"account_id":"x"}}""");
        var detector = new ChatGptProviderDetector(path);
        var result = await detector.DetectAsync(CancellationToken.None);
        Assert.Equal(ProviderDetectionStatus.NotFound, result.Status);
    }

    // 4. auth_mode absent → NotFound
    [Fact]
    public async Task Detect_MissingAuthMode_ReturnsNotFound()
    {
        var path = WriteAuth("""{"tokens":{"account_id":"x"}}""");
        var detector = new ChatGptProviderDetector(path);
        var result = await detector.DetectAsync(CancellationToken.None);
        Assert.Equal(ProviderDetectionStatus.NotFound, result.Status);
    }

    // 5. tokens block absent → NotFound
    [Fact]
    public async Task Detect_MissingTokensBlock_ReturnsNotFound()
    {
        var path = WriteAuth("""{"auth_mode":"chatgpt"}""");
        var detector = new ChatGptProviderDetector(path);
        var result = await detector.DetectAsync(CancellationToken.None);
        Assert.Equal(ProviderDetectionStatus.NotFound, result.Status);
    }

    // 6. Malformed JSON → Error (not a crash)
    [Fact]
    public async Task Detect_MalformedJson_ReturnsError()
    {
        var path = WriteAuth("not-json{{{{");
        var detector = new ChatGptProviderDetector(path);
        var result = await detector.DetectAsync(CancellationToken.None);
        Assert.Equal(ProviderDetectionStatus.Error, result.Status);
    }

    // 7. auth_mode case-insensitive: "ChatGPT" (capital) → Detected
    [Fact]
    public async Task Detect_AuthModeCaseInsensitive_ReturnsDetected()
    {
        var path = WriteAuth("""{"auth_mode":"ChatGPT","tokens":{"account_id":"x"}}""");
        var detector = new ChatGptProviderDetector(path);
        var result = await detector.DetectAsync(CancellationToken.None);
        Assert.Equal(ProviderDetectionStatus.Detected, result.Status);
    }

    // 8. ProviderId is "chatgpt" (exact, lowercase)
    [Fact]
    public void ProviderId_IsExactlyLowercaseChatgpt()
    {
        var detector = new ChatGptProviderDetector(Path.Combine(_tempDir, "x.json"));
        Assert.Equal("chatgpt", detector.ProviderId);
    }

    // 9. Defensive parse: extra/unknown fields present alongside valid ones → still Detected
    //    (Replaces the plan's tautological "does not expose token" test with a real assertion:
    //     the detector tolerates unknown JSON fields and still detects correctly.)
    [Fact]
    public async Task Detect_ValidFileWithUnknownExtraFields_ReturnsDetected()
    {
        var path = WriteAuth("""
            {
              "auth_mode": "chatgpt",
              "some_future_field": { "nested": 1 },
              "tokens": { "account_id": "x", "access_token": "SHOULD-NOT-BE-READ", "extra": true }
            }
            """);
        var detector = new ChatGptProviderDetector(path);
        var result = await detector.DetectAsync(CancellationToken.None);
        Assert.Equal(ProviderDetectionStatus.Detected, result.Status);
        Assert.Equal("ChatGPT", result.ProviderDisplayName);
    }
}
