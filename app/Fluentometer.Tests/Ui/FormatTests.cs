using Fluentometer.Logic.Ui;
using Xunit;

public class FormatTests
{
    [Fact]
    public void CountdownFormatsHoursAndMinutes()
    {
        // resetsAt is 2h14m after now
        var now = 1_000_000L;
        var resets = now + (2 * 3600) + (14 * 60);
        Assert.Equal("resets in 2h 14m", Format.ResetCountdown(resets, now));
    }

    [Fact]
    public void CountdownNullIsDash() => Assert.Equal("—", Format.ResetCountdown(null, 0));

    [Fact]
    public void CountdownPastIsResetsNow() =>
        Assert.Equal("resets now", Format.ResetCountdown(500, 1000));

    [Fact]
    public void PercentPrefersUtilization() =>
        Assert.Equal("42%", Format.PercentOrEstimate(0.42, "ignored"));

    [Fact]
    public void PercentFallsBackToEstimateLabel() =>
        Assert.Equal("~1.2M tokens", Format.PercentOrEstimate(null, "~1.2M tokens"));

    [Fact]
    public void BarValueClampsAndDefaultsZero()
    {
        Assert.Equal(0.5, Format.BarValue(0.5));
        Assert.Equal(1.0, Format.BarValue(1.7));
        Assert.Equal(0.0, Format.BarValue(null));
    }

    // --- Additional branch coverage ---

    [Fact]
    public void CountdownFormatsMinutesOnly()
    {
        // 5 minutes remaining — no hours component.
        var now = 1_000_000L;
        var resets = now + (5 * 60);
        Assert.Equal("resets in 5m", Format.ResetCountdown(resets, now));
    }

    [Fact]
    public void CountdownFormatsSecondsOnly()
    {
        // 47 seconds remaining — below 1 minute.
        var now = 1_000_000L;
        var resets = now + 47;
        Assert.Equal("resets in 47s", Format.ResetCountdown(resets, now));
    }

    [Fact]
    public void CountdownExactZeroIsResetsNow()
    {
        // resetsAt == nowUnix: secs == 0, must return "resets now" not a negative countdown.
        Assert.Equal("resets now", Format.ResetCountdown(1000, 1000));
    }

    [Fact]
    public void PercentOrEstimateRoundingUsesBankersRounding()
    {
        // 0.425 * 100 = 42.5 — C# Math.Round uses ToEven (banker's rounding) → 42.
        Assert.Equal("42%", Format.PercentOrEstimate(0.425, "ignored"));
    }

    [Fact]
    public void PercentOrEstimateRoundsNormalMidpoint()
    {
        // 0.435 * 100 = 43.5 — nearest even is 44.
        Assert.Equal("44%", Format.PercentOrEstimate(0.435, "ignored"));
    }

    // Collapsed from 3 separate Facts (BarValueNegativeClampsToZero, BarValueExactlyOneIsPreserved,
    // BarValueZeroIsPreserved) — all are boundary variants of the same clamp method.
    [Theory]
    [InlineData(-0.5, 0.0)]  // below zero clamps to 0
    [InlineData(0.0, 0.0)]   // exactly zero is preserved
    [InlineData(1.0, 1.0)]   // exactly one is preserved
    public void BarValueBoundariesClampCorrectly(double input, double expected)
    {
        Assert.Equal(expected, Format.BarValue(input));
    }
}
