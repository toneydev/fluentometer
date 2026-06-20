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

    // -------------------------------------------------------------------------
    // DisplayNameFor — smoke tests verifying the promoted internal static is
    // externally accessible and returns correct brand names from call sites
    // outside the ViewModels assembly (e.g. SettingsPage.xaml.cs).
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("chatgpt", "ChatGPT")]
    [InlineData("claude", "Claude")]
    [InlineData("gemini", "Gemini")]
    public void DisplayNameFor_ReturnsKnownBrandName(string providerId, string expected)
    {
        // Verifies that DisplayNameFor is internal static and accessible from the
        // test assembly (via InternalsVisibleTo), returning the correct brand name.
        Assert.Equal(expected, ProviderGroupViewModel.DisplayNameFor(providerId));
    }

    [Fact]
    public void DisplayNameFor_FallsBackToFirstCharUppercaseForUnknownProvider()
    {
        Assert.Equal("Perplexity", ProviderGroupViewModel.DisplayNameFor("perplexity"));
    }

    [Fact]
    public void DisplayNameFor_EmptyStringReturnsEmpty()
    {
        Assert.Equal("", ProviderGroupViewModel.DisplayNameFor(""));
    }
}
