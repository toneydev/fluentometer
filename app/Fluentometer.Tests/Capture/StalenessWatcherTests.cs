using System;
using Fluentometer.Logic.Capture;
using Fluentometer.Logic.Ipc;
using Xunit;

namespace Fluentometer.Tests.Capture;

public class StalenessWatcherTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);

    private static RefreshStatus Status(
        string id = "claude",
        DateTimeOffset? lastSuccess = null,
        int failures = 0,
        long interval = 180)
        => new(id, lastSuccess, failures, interval);

    [Fact]
    public void NoStatusYet_IsNotStale()
    {
        var w = new StalenessWatcher();
        var r = w.Evaluate(T0);
        Assert.False(r.IsStale);
        Assert.Empty(r.StaleProviders);
    }

    [Fact]
    public void BelowFailureThreshold_IsNotStale()
    {
        var w = new StalenessWatcher();
        w.Update(Status(failures: 1, lastSuccess: T0));
        Assert.False(w.Evaluate(T0).IsStale);
    }

    [Fact]
    public void AtFailureThreshold_IsStale()
    {
        var w = new StalenessWatcher();
        w.Update(Status(failures: 2, lastSuccess: T0));
        var r = w.Evaluate(T0);
        Assert.True(r.IsStale);
        Assert.Contains("claude", r.StaleProviders);
    }

    [Fact]
    public void AgeWithinThreshold_IsNotStale()
    {
        var w = new StalenessWatcher();
        w.Update(Status(failures: 0, lastSuccess: T0, interval: 180));
        Assert.False(w.Evaluate(T0.AddSeconds(360)).IsStale); // 2.0× < 2.5×180s
    }

    [Fact]
    public void AgePastThreshold_IsStale()
    {
        var w = new StalenessWatcher();
        w.Update(Status(failures: 0, lastSuccess: T0, interval: 180));
        Assert.True(w.Evaluate(T0.AddSeconds(451)).IsStale); // > 2.5×180 = 450s
    }

    [Fact]
    public void AgePath_RequiresPriorSuccess()
    {
        var w = new StalenessWatcher();
        w.Update(Status(failures: 1, lastSuccess: null));
        Assert.False(w.Evaluate(T0.AddDays(1)).IsStale);
    }

    [Fact]
    public void SuccessAfterStale_Recovers()
    {
        var w = new StalenessWatcher();
        w.Update(Status(failures: 3, lastSuccess: T0));
        Assert.True(w.Evaluate(T0).IsStale);

        w.Update(Status(failures: 0, lastSuccess: T0.AddSeconds(10)));
        Assert.False(w.Evaluate(T0.AddSeconds(10)).IsStale);
    }

    [Fact]
    public void MultipleProviders_RollupAndDetailListsBoth()
    {
        var w = new StalenessWatcher();
        w.Update(Status("claude", failures: 2, lastSuccess: T0));
        w.Update(Status("gemini", failures: 2, lastSuccess: T0));
        var r = w.Evaluate(T0);
        Assert.True(r.IsStale);
        Assert.Equal(2, r.StaleProviders.Count);
        Assert.Contains("Claude", r.Detail);
        Assert.Contains("Gemini", r.Detail);
    }

    [Fact]
    public void Detail_ForAgeTrip_MentionsLastUpdateAndExpectedRate()
    {
        var w = new StalenessWatcher();
        w.Update(Status("claude", failures: 0, lastSuccess: T0, interval: 180));
        var r = w.Evaluate(T0.AddSeconds(600));
        Assert.Contains("Claude", r.Detail);
        Assert.Contains("stale", r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detail_ForFailureTripWithNoSuccess_SaysUnreachable()
    {
        var w = new StalenessWatcher();
        w.Update(Status("claude", failures: 2, lastSuccess: null));
        var r = w.Evaluate(T0);
        Assert.Contains("unreachable", r.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
