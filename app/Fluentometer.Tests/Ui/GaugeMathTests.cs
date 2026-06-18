using Fluentometer.Logic.Ui;
using Xunit;

public class GaugeMathTests
{
    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(0.0005, 0.0)] // below epsilon snaps to 0
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.0)]
    public void FractionClampsAndSnaps(double value, double expected)
        => Assert.Equal(expected, GaugeMath.Fraction(value), 5);

    [Theory]
    [InlineData(0.0, 200.0, 200.0)] // empty -> full inset (nothing shown)
    [InlineData(1.0, 200.0, 0.0)]   // full  -> no inset (all shown)
    [InlineData(0.25, 200.0, 150.0)]
    public void RightInsetMapsValueToReveal(double value, double width, double expected)
        => Assert.Equal(expected, GaugeMath.RightInset(value, width), 5);

    [Theory]
    [InlineData(0.0, 200.0, 56.0, -56.0)] // empty -> glow off-screen left
    [InlineData(1.0, 200.0, 56.0, 144.0)] // full  -> glow right edge at width
    [InlineData(0.5, 200.0, 56.0, 44.0)]
    public void GlowOffsetTracksTip(double value, double width, double glowWidth, double expected)
        => Assert.Equal(expected, GaugeMath.GlowOffsetX(value, width, glowWidth), 5);
}
