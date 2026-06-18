// app/Fluentometer.Tests/Demo/DemoUsageSimulatorTests.cs
using Fluentometer.Logic.Demo;
using Xunit;

public class DemoUsageSimulatorTests
{
    private const long Now = 1_700_000_000;

    private static double Util(double t, int gaugeIndex) =>
        DemoUsageSimulator.Sample(t, Now).Gauges[gaugeIndex].Utilization!.Value;

    [Fact]
    public void ProducesThreeCanonicalGaugesInOrder()
    {
        var snap = DemoUsageSimulator.Sample(10, Now);
        Assert.Equal(3, snap.Gauges.Count);
        Assert.Equal(("session", "Claude 5-hour"), (snap.Gauges[0].Id, snap.Gauges[0].Label));
        Assert.Equal(("weekly_all", "Claude Weekly"), (snap.Gauges[1].Id, snap.Gauges[1].Label));
        Assert.Equal(("weekly_scoped", "Claude Weekly (Sonnet)"), (snap.Gauges[2].Id, snap.Gauges[2].Label));
        Assert.Equal("demo", snap.Source);
        Assert.Equal("ok", snap.Health);
    }

    [Fact]
    public void AtZeroAllGaugesAreZero()
    {
        var snap = DemoUsageSimulator.Sample(0, Now);
        Assert.Equal(0.0, snap.Gauges[0].Utilization!.Value, 3);
        Assert.Equal(0.0, snap.Gauges[1].Utilization!.Value, 3);
        Assert.Equal(0.0, snap.Gauges[2].Utilization!.Value, 3);
    }

    [Fact]
    public void WeeklyReachesNinetyToHundredByEndOfCycle()
    {
        var weekly = Util(29.99, 1);
        Assert.InRange(weekly, 0.90, 1.00);
    }

    [Fact]
    public void SonnetTrailsWeeklyButIsPositive()
    {
        var weekly = Util(15.0, 1);
        var sonnet = Util(15.0, 2);
        Assert.True(sonnet < weekly, $"sonnet {sonnet} should trail weekly {weekly}");
        Assert.True(sonnet > 0.0);
    }

    [Fact]
    public void WeeklyIsMonotonicWithinACycle()
    {
        var prev = -1.0;
        for (var t = 0.0; t < 30.0; t += 0.5)
        {
            var w = Util(t, 1);
            Assert.True(w >= prev - 1e-9, $"weekly dropped at t={t}: {w} < {prev}");
            prev = w;
        }
    }

    [Fact]
    public void SessionSawtoothDropsAtWindowBoundary()
    {
        // window length = 30 / 5 = 6s. Just before the first boundary the session is
        // near its peak; just after, it has reset toward zero.
        var before = Util(5.9, 0);
        var after = Util(6.1, 0);
        Assert.True(after < before, $"expected reset: after {after} < before {before}");
    }

    [Fact]
    public void SessionPeaksVary_SomeFull_SomePartial_WithinFirstCycle()
    {
        // Sample each window near its end (localFrac ~ 1) to read its peak height.
        var peaks = new[] { Util(5.99, 0), Util(11.99, 0), Util(17.99, 0), Util(23.99, 0), Util(29.99, 0) };
        Assert.Contains(peaks, p => p > 0.95);                 // at least one ~100%
        Assert.Contains(peaks, p => p is >= 0.30 and <= 0.70); // at least one partial
    }

    [Fact]
    public void SecondCycleDiffersFromFirst()
    {
        Assert.NotEqual(Util(5.99, 0), Util(35.99, 0), precision: 2);
    }

    [Fact]
    public void IsDeterministic()
    {
        var a = DemoUsageSimulator.Sample(15.0, Now);
        var b = DemoUsageSimulator.Sample(15.0, Now);
        Assert.Equal(a.Gauges[0], b.Gauges[0]);
        Assert.Equal(a.Gauges[1], b.Gauges[1]);
        Assert.Equal(a.Gauges[2], b.Gauges[2]);
    }
}
