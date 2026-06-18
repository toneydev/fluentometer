using Fluentometer.Logic.Settings;
using Xunit;

namespace Fluentometer.Tests.Settings;

public class AppSettingsTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(-1)]
    [InlineData(179)]
    public void PollIntervalBelowFloorClampsTo180(int input)
    {
        var s = new AppSettings { PollIntervalSeconds = input };
        Assert.Equal(180, s.PollIntervalSeconds);
    }

    [Theory]
    [InlineData(180)]
    [InlineData(600)]
    [InlineData(1800)]
    public void PollIntervalAtOrAboveFloorPassesThrough(int input)
    {
        var s = new AppSettings { PollIntervalSeconds = input };
        Assert.Equal(input, s.PollIntervalSeconds);
    }

    [Fact]
    public void DefaultThemeIdIsAurora()
    {
        var s = new AppSettings();
        Assert.Equal("aurora", s.ThemeId);
    }

    [Fact]
    public void DefaultOfflineOnlyIsFalse()
    {
        var s = new AppSettings();
        Assert.False(s.OfflineOnly);
    }
}
