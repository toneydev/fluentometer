using Fluentometer.Logic.Density;
using Xunit;

namespace Fluentometer.Tests.Density;

public sealed class DensityCatalogTests
{
    [Fact]
    public void Comfortable_is_scaled_up_compact()
    {
        var m = DensityCatalog.For(GaugeDensity.Comfortable);
        Assert.Equal(144, m.ItemMinHeight);
        Assert.Equal(116, m.CardMinHeight);
        Assert.Equal(34, m.ValueFontSize);
        Assert.Equal((20, 14, 20, 14), (m.PadLeft, m.PadTop, m.PadRight, m.PadBottom));
        Assert.True(m.ShowCountdown);
        Assert.Equal(GaugeBarLayout.Wipe, m.BarLayout);
        Assert.False(m.MiniInline);
    }

    [Fact]
    public void Compact_keeps_countdown_and_wipe()
    {
        var m = DensityCatalog.For(GaugeDensity.Compact);
        Assert.Equal(84, m.ItemMinHeight);
        Assert.Equal(72, m.CardMinHeight);
        Assert.Equal(22, m.ValueFontSize);
        Assert.Equal((12, 10, 12, 10), (m.PadLeft, m.PadTop, m.PadRight, m.PadBottom));
        Assert.True(m.ShowCountdown);
        Assert.Equal(GaugeBarLayout.Wipe, m.BarLayout);
        Assert.False(m.MiniInline);
    }

    [Fact]
    public void Mini_uses_track_bar_inline_and_hides_countdown()
    {
        var m = DensityCatalog.For(GaugeDensity.Mini);
        Assert.Equal(44, m.ItemMinHeight);
        Assert.Equal(40, m.CardMinHeight);
        Assert.Equal(17, m.ValueFontSize);
        Assert.Equal((12, 8, 12, 8), (m.PadLeft, m.PadTop, m.PadRight, m.PadBottom));
        Assert.False(m.ShowCountdown);
        Assert.Equal(GaugeBarLayout.Track, m.BarLayout);
        Assert.True(m.MiniInline);
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
