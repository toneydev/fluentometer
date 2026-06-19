using Fluentometer.Logic.ViewModels;
using Xunit;

/// <summary>
/// Locks the display-name contract for <see cref="ProviderGroupViewModel.Title"/>.
///
/// Provider IDs are lowercase machine identifiers ("chatgpt", "claude", "gemini").
/// The section header shown in the dashboard must be human-readable brand names, not
/// naive first-char-capitalizations. This matters especially for "chatgpt" → "ChatGPT":
/// naive capitalization yields "Chatgpt", which a visual-gate pass caught as wrong.
///
/// The fallback (first-char-uppercase) must still fire for unknown future providers
/// so new providers get a reasonable default without requiring a code change here.
/// </summary>
public class ProviderGroupViewModelTests
{
    [Theory]
    [InlineData("chatgpt", "ChatGPT")]
    [InlineData("claude", "Claude")]
    [InlineData("gemini", "Gemini")]
    public void Title_ReturnsKnownProviderDisplayName(string providerId, string expectedTitle)
    {
        var vm = new ProviderGroupViewModel(providerId);
        Assert.Equal(expectedTitle, vm.Title);
    }

    [Fact]
    public void Title_FallsBackToFirstCharUppercaseForUnknownProvider()
    {
        var vm = new ProviderGroupViewModel("perplexity");
        Assert.Equal("Perplexity", vm.Title);
    }

    [Fact]
    public void Title_EmptyProviderIdReturnsEmpty()
    {
        var vm = new ProviderGroupViewModel("");
        Assert.Equal("", vm.Title);
    }

    [Fact]
    public void ProviderId_IsPreservedExactly()
    {
        var vm = new ProviderGroupViewModel("chatgpt");
        Assert.Equal("chatgpt", vm.ProviderId);
    }
}
