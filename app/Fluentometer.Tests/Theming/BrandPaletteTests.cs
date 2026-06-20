using Fluentometer.Logic.Theming;
using Xunit;

public class BrandPaletteTests
{
    [Theory]
    [InlineData("claude", "#C15F3C")]
    [InlineData("chatgpt", "#74AA9C")]
    [InlineData("gemini", "#4796E3")]
    public void KnownProviderHasPrimaryAsMidStopAndAccent(string id, string primary)
    {
        var c = BrandPalette.For(id);
        Assert.Equal(3, c.BarStops.Length);
        Assert.Equal(primary, c.BarStops[1]); // primary is the middle stop
        Assert.Equal(primary, c.Accent);      // primary is also the glow accent
    }

    [Theory]
    [InlineData("CLAUDE")]
    [InlineData("ChatGPT")]
    [InlineData("Gemini")]
    public void LookupIsCaseInsensitive(string id)
    {
        var c = BrandPalette.For(id);
        Assert.Equal(3, c.BarStops.Length);
    }

    [Fact]
    public void UnknownProviderReturnsNeutralSlateFallback()
    {
        var c = BrandPalette.For("does-not-exist");
        Assert.Equal(new[] { "#94A3B8", "#64748B", "#334155" }, c.BarStops);
        Assert.Equal("#64748B", c.Accent);
    }

    [Fact]
    public void AllKnownStopsAndAccentsAreHexColors()
    {
        foreach (var id in new[] { "claude", "chatgpt", "gemini" })
        {
            var c = BrandPalette.For(id);
            Assert.StartsWith("#", c.Accent);
            Assert.Equal(7, c.Accent.Length);
            foreach (var s in c.BarStops)
            {
                Assert.StartsWith("#", s);
                Assert.Equal(7, s.Length);
            }
        }
    }
}
