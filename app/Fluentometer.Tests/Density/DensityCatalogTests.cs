using Fluentometer.Logic.Density;
using Xunit;

namespace Fluentometer.Tests.Density;

public sealed class DensityCatalogTests
{
    [Fact]
    public void Comfortable_matches_current_baseline()
    {
        var m = DensityCatalog.For(GaugeDensity.Comfortable);
        Assert.Equal(176, m.ItemMinHeight);
        Assert.Equal(120, m.CardMinHeight);
        Assert.Equal(40, m.ValueFontSize);
        Assert.Equal((20, 14, 20, 14), (m.PadLeft, m.PadTop, m.PadRight, m.PadBottom));
        Assert.True(m.ShowCountdown);
    }

    [Fact]
    public void Compact_shrinks_but_keeps_countdown()
    {
        var m = DensityCatalog.For(GaugeDensity.Compact);
        Assert.Equal(120, m.ItemMinHeight);
        Assert.Equal(96, m.CardMinHeight);
        Assert.Equal(28, m.ValueFontSize);
        Assert.Equal((16, 12, 16, 12), (m.PadLeft, m.PadTop, m.PadRight, m.PadBottom));
        Assert.True(m.ShowCountdown);
    }

    [Fact]
    public void Mini_hides_countdown()
    {
        var m = DensityCatalog.For(GaugeDensity.Mini);
        Assert.Equal(84, m.ItemMinHeight);
        Assert.Equal(72, m.CardMinHeight);
        Assert.Equal(22, m.ValueFontSize);
        Assert.Equal((12, 10, 12, 10), (m.PadLeft, m.PadTop, m.PadRight, m.PadBottom));
        Assert.False(m.ShowCountdown);
    }

    [Theory]
    [InlineData("comfortable", GaugeDensity.Comfortable)]
    [InlineData("compact", GaugeDensity.Compact)]
    [InlineData("mini", GaugeDensity.Mini)]
    [InlineData("MINI", GaugeDensity.Mini)]
    [InlineData("  compact  ", GaugeDensity.Compact)]
    [InlineData(null, GaugeDensity.Comfortable)]
    [InlineData("bogus", GaugeDensity.Comfortable)]
    public void Parse_maps_ids_and_defaults_to_comfortable(string? id, GaugeDensity expected)
        => Assert.Equal(expected, DensityCatalog.Parse(id));

    [Theory]
    [InlineData(GaugeDensity.Comfortable, "comfortable")]
    [InlineData(GaugeDensity.Compact, "compact")]
    [InlineData(GaugeDensity.Mini, "mini")]
    public void ToId_round_trips_with_Parse(GaugeDensity density, string expectedId)
    {
        Assert.Equal(expectedId, DensityCatalog.ToId(density));
        Assert.Equal(density, DensityCatalog.Parse(DensityCatalog.ToId(density)));
    }
}
