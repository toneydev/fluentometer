using Fluentometer.Logic.Capture;
using Xunit;

namespace Fluentometer.Tests.Capture;

public sealed class ProviderCatalogTests
{
    [Fact]
    public void RecognizedIds_AreTheThreeProviders_InAlphabeticalOrder()
    {
        Assert.Equal(new[] { "chatgpt", "claude", "gemini" }, ProviderCatalog.RecognizedIds);
    }

    [Theory]
    [InlineData("claude", "Claude — server data · requires Claude Code")]
    [InlineData("chatgpt", "ChatGPT — server data · requires Codex CLI")]
    [InlineData("gemini", "Gemini — server data · requires Gemini CLI")]
    public void SourceHint_ReturnsExpectedCopy_ForKnownProviders(string id, string expected)
    {
        Assert.Equal(expected, ProviderCatalog.SourceHint(id));
    }

    [Fact]
    public void SourceHint_FallsBackToLocalEstimate_ForUnknownProvider()
    {
        Assert.Equal(
            "Perplexity — local estimate (no API key required)",
            ProviderCatalog.SourceHint("perplexity"));
    }
}
