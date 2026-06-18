using System.Linq;
using Fluentometer.Logic.Theming;
using Xunit;

public class GradientStopsTests
{
    [Fact]
    public void BrightToDeepKeepsAuthoredOrder()
    {
        var r = GradientStops.OrderedStops(new[] { "#A", "#B", "#C" }, GradientDirection.BrightToDeep);
        Assert.Equal(new[] { "#A", "#B", "#C" }, r.Select(x => x.Color).ToArray());
        Assert.Equal(new[] { 0.0, 0.5, 1.0 }, r.Select(x => x.Offset).ToArray());
    }

    [Fact]
    public void DeepToBrightReversesColorsNotOffsets()
    {
        var r = GradientStops.OrderedStops(new[] { "#A", "#B", "#C" }, GradientDirection.DeepToBright);
        Assert.Equal(new[] { "#C", "#B", "#A" }, r.Select(x => x.Color).ToArray());
        Assert.Equal(new[] { 0.0, 0.5, 1.0 }, r.Select(x => x.Offset).ToArray());
    }

    [Fact]
    public void SingleStopUsesOffsetZeroInBothDirections()
    {
        foreach (var dir in new[] { GradientDirection.BrightToDeep, GradientDirection.DeepToBright })
        {
            var r = GradientStops.OrderedStops(new[] { "#A" }, dir);
            Assert.Single(r);
            Assert.Equal("#A", r[0].Color);
            Assert.Equal(0.0, r[0].Offset);
        }
    }
}
