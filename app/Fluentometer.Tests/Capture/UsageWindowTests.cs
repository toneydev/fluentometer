using Fluentometer.Logic.Capture;
using Xunit;

/// <summary>
/// Tests for <see cref="UsageWindow.Summarize"/>.
/// </summary>
public class UsageWindowTests
{
    private static UsageEvent Ev(long ts, long tokens) => new(ts, tokens);

    // ────────────────────────────────────────────────────────────────────────
    // Core window-math tests
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Summarize_SumsOnlyEventsInsideWindow()
    {
        const long now = 10_000;
        var events = new[]
        {
            Ev(now - UsageWindow.FiveHourSecs - 1, 999), // just outside — excluded
            Ev(now - 3600, 100),                          // inside
            Ev(now - 60,   50),                           // inside
        };

        var s = UsageWindow.Summarize(events, UsageWindow.FiveHourSecs, now);

        Assert.Equal(150L, s.TokensInWindow);
        Assert.Equal(now - 3600 + UsageWindow.FiveHourSecs, s.ResetsAt);
    }

    [Fact]
    public void Summarize_EmptyEvents_ReturnsZeroTokensAndNullReset()
    {
        var s = UsageWindow.Summarize([], UsageWindow.FiveHourSecs, 10_000);

        Assert.Equal(0L, s.TokensInWindow);
        Assert.Null(s.ResetsAt);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Additional boundary / correctness tests
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Summarize_EventExactlyAtStart_IsIncluded()
    {
        const long now = 10_000;
        long start = now - UsageWindow.FiveHourSecs;

        // Event at exactly start (inclusive boundary)
        var events = new[] { Ev(start, 42) };

        var s = UsageWindow.Summarize(events, UsageWindow.FiveHourSecs, now);

        Assert.Equal(42L, s.TokensInWindow);
        Assert.Equal(start + UsageWindow.FiveHourSecs, s.ResetsAt);
    }

    [Fact]
    public void Summarize_EventExactlyAtNow_IsIncluded()
    {
        const long now = 10_000;
        var events = new[] { Ev(now, 99) };

        var s = UsageWindow.Summarize(events, UsageWindow.FiveHourSecs, now);

        Assert.Equal(99L, s.TokensInWindow);
        Assert.Equal(now + UsageWindow.FiveHourSecs, s.ResetsAt);
    }

    [Fact]
    public void Summarize_EventOneSecondBeforeStart_IsExcluded()
    {
        const long now = 10_000;
        long justOutside = now - UsageWindow.FiveHourSecs - 1;

        var events = new[] { Ev(justOutside, 500) };

        var s = UsageWindow.Summarize(events, UsageWindow.FiveHourSecs, now);

        Assert.Equal(0L, s.TokensInWindow);
        Assert.Null(s.ResetsAt);
    }

    [Fact]
    public void Summarize_ResetsAtBasedOnOldestInWindowEvent()
    {
        // oldest is the one that determines resets_at
        const long now = 100_000;
        long oldest = now - 3000;
        long newer = now - 1000;

        var events = new[]
        {
            Ev(newer,  10),
            Ev(oldest, 20),
        };

        var s = UsageWindow.Summarize(events, UsageWindow.FiveHourSecs, now);

        Assert.Equal(30L, s.TokensInWindow);
        Assert.Equal(oldest + UsageWindow.FiveHourSecs, s.ResetsAt);
    }

    [Fact]
    public void Summarize_SevenDayWindowConstants()
    {
        Assert.Equal(18_000L, UsageWindow.FiveHourSecs);
        Assert.Equal(604_800L, UsageWindow.SevenDaySecs);
    }

    [Fact]
    public void Summarize_MultipleEventsAllOutside_NullReset()
    {
        const long now = 1_000_000;
        var events = new[]
        {
            Ev(now - UsageWindow.SevenDaySecs - 1, 100),
            Ev(now - UsageWindow.SevenDaySecs - 100, 200),
        };

        var s = UsageWindow.Summarize(events, UsageWindow.SevenDaySecs, now);

        Assert.Equal(0L, s.TokensInWindow);
        Assert.Null(s.ResetsAt);
    }
}
