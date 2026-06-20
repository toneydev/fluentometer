// app/Fluentometer.Tests/Demo/DemoUsageSimulatorTests.cs
using System.Collections.Generic;
using System.Linq;
using Fluentometer.Logic.Demo;
using Xunit;

public class DemoUsageSimulatorTests
{
    private const long Now = 1_700_000_000;

    // Helpers — all look up the Claude snapshot (index 0) for backward-compat.
    private static double Util(double t, int gaugeIndex) =>
        Claude(DemoUsageSimulator.Sample(t, Now)).Gauges[gaugeIndex].Utilization!.Value;

    private static Fluentometer.Logic.Ipc.UsageSnapshot Claude(IReadOnlyList<Fluentometer.Logic.Ipc.UsageSnapshot> snaps) =>
        snaps.First(s => s.Provider == "claude");

    // ── Multi-provider shape ────────────────────────────────────────────────────

    [Fact]
    public void ReturnsAtLeastTwoSnapshots()
    {
        var snaps = DemoUsageSimulator.Sample(10, Now);
        Assert.True(snaps.Count >= 2, $"expected >=2 snapshots, got {snaps.Count}");
    }

    [Fact]
    public void ClaudeSnapshotIsFirst()
    {
        var snaps = DemoUsageSimulator.Sample(10, Now);
        Assert.Equal("claude", snaps[0].Provider);
    }

    [Fact]
    public void GeminiSnapshotIsThird()
    {
        var snaps = DemoUsageSimulator.Sample(10, Now);
        Assert.Equal("gemini", snaps[2].Provider);
    }

    [Fact]
    public void DemoProviderIdSetIsExactlyClaudeGeminiAndChatGpt()
    {
        // GUARD: adding a real provider without a demo sample should be a VISIBLE gap.
        // Update this set when you add a new provider demo sample.
        var expected = new HashSet<string> { "claude", "gemini", "chatgpt" };
        var actual = new HashSet<string>(DemoUsageSimulator.Sample(10, Now).Select(s => s.Provider));
        Assert.Equal(expected, actual);
    }

    // ── Provider ordering ──────────────────────────────────────────────────────────

    [Fact]
    public void ClaudeIsFirst_ChatGptIsSecond_GeminiIsThird()
    {
        var snaps = DemoUsageSimulator.Sample(10, Now);
        Assert.True(snaps.Count >= 3, $"Expected >= 3 snapshots, got {snaps.Count}");
        Assert.Equal("claude", snaps[0].Provider);
        Assert.Equal("chatgpt", snaps[1].Provider);
        Assert.Equal("gemini", snaps[2].Provider);
    }

    // ── ChatGPT snapshot shape ─────────────────────────────────────────────────────

    [Fact]
    public void ChatGptSnapshot_HasTwoGauges()
    {
        var chatgpt = DemoUsageSimulator.Sample(10, Now).First(s => s.Provider == "chatgpt");
        Assert.Equal(2, chatgpt.Gauges.Count);
    }

    [Fact]
    public void ChatGptSnapshot_GaugeLabelsAreProviderPrefixed()
    {
        var chatgpt = DemoUsageSimulator.Sample(10, Now).First(s => s.Provider == "chatgpt");
        Assert.Equal("ChatGPT 5-hour", chatgpt.Gauges[0].Label);
        Assert.Equal("ChatGPT Weekly", chatgpt.Gauges[1].Label);
    }

    [Fact]
    public void ChatGptSnapshot_GaugeHasRealUtilization_NotNull()
    {
        // ChatGPT is server-truth: demo gauges show animated percent bars (Utilization != null).
        var chatgpt = DemoUsageSimulator.Sample(15, Now).First(s => s.Provider == "chatgpt");
        Assert.NotNull(chatgpt.Gauges[0].Utilization);
        Assert.NotNull(chatgpt.Gauges[1].Utilization);
    }

    [Fact]
    public void ChatGptSnapshot_UtilizationInRange()
    {
        // Utilization must be in 0..1 (not raw percent 0..100).
        var chatgpt = DemoUsageSimulator.Sample(15, Now).First(s => s.Provider == "chatgpt");
        Assert.InRange(chatgpt.Gauges[0].Utilization!.Value, 0.0, 1.0);
        Assert.InRange(chatgpt.Gauges[1].Utilization!.Value, 0.0, 1.0);
    }

    [Fact]
    public void ChatGptSnapshot_SourceIsDemo()
    {
        var chatgpt = DemoUsageSimulator.Sample(10, Now).First(s => s.Provider == "chatgpt");
        Assert.Equal("demo", chatgpt.Source);
    }

    [Fact]
    public void ChatGptSnapshot_HealthIsOk()
    {
        var chatgpt = DemoUsageSimulator.Sample(10, Now).First(s => s.Provider == "chatgpt");
        Assert.Equal("ok", chatgpt.Health);
    }

    [Fact]
    public void ChatGptSnapshot_IsDeterministic()
    {
        var a = DemoUsageSimulator.Sample(15.0, Now).First(s => s.Provider == "chatgpt");
        var b = DemoUsageSimulator.Sample(15.0, Now).First(s => s.Provider == "chatgpt");
        Assert.Equal(a.Provider, b.Provider);
        Assert.Equal(a.Source, b.Source);
        Assert.Equal(a.Health, b.Health);
        Assert.Equal(a.Gauges.Count, b.Gauges.Count);
        for (var i = 0; i < a.Gauges.Count; i++)
            Assert.Equal(a.Gauges[i], b.Gauges[i]);
    }

    [Fact]
    public void ChatGptSnapshot_AnimatesOverTime_WeeklyIncreases()
    {
        // Verify ChatGPT weekly gauge animates (is not static) — differs from Gemini's null/static gauge.
        var at0 = DemoUsageSimulator.Sample(0, Now).First(s => s.Provider == "chatgpt");
        var at15 = DemoUsageSimulator.Sample(15, Now).First(s => s.Provider == "chatgpt");
        // Weekly at t=15 must be different from t=0 (animation working).
        Assert.NotEqual(at0.Gauges[1].Utilization, at15.Gauges[1].Utilization);
    }

    // ── Gemini demo snapshot shape ──────────────────────────────────────────────

    [Fact]
    public void GeminiSnapshot_HasExactlyOneGauge()
    {
        var gemini = DemoUsageSimulator.Sample(10, Now).First(s => s.Provider == "gemini");
        Assert.Single(gemini.Gauges);
    }

    [Fact]
    public void GeminiSnapshot_GaugeHasRealUtilization_NotNull()
    {
        // Gemini is now server-truth: demo gauge shows an animated percent bar (Utilization != null).
        var gemini = DemoUsageSimulator.Sample(15, Now).First(s => s.Provider == "gemini");
        Assert.NotNull(gemini.Gauges[0].Utilization);
        Assert.InRange(gemini.Gauges[0].Utilization!.Value, 0.0, 1.0);
    }

    [Fact]
    public void GeminiSnapshot_SourceIsDemo()
    {
        var gemini = DemoUsageSimulator.Sample(10, Now).First(s => s.Provider == "gemini");
        Assert.Equal("demo", gemini.Source);
    }

    [Fact]
    public void GeminiSnapshot_HealthIsOk()
    {
        var gemini = DemoUsageSimulator.Sample(10, Now).First(s => s.Provider == "gemini");
        Assert.Equal("ok", gemini.Health);
    }

    [Fact]
    public void GeminiSnapshot_GaugeIdAndLabels()
    {
        var gemini = DemoUsageSimulator.Sample(10, Now).First(s => s.Provider == "gemini");
        var g = gemini.Gauges[0];
        Assert.Equal("gemini_requests", g.Id);
        Assert.Equal("Gemini Requests", g.Label);
        Assert.Equal("daily limit", g.LimitLabel);
        // UsedLabel is the formatted percent (Pct) — must be a non-empty "NN%" string,
        // and ResetsAt is the daily reset (nowUnix + 24h) that drives the countdown.
        Assert.False(string.IsNullOrEmpty(g.UsedLabel));
        Assert.EndsWith("%", g.UsedLabel);
        Assert.Equal(Now + 24 * 3600, g.ResetsAt);
    }

    [Fact]
    public void GeminiSnapshot_AnimatesOverTime()
    {
        var a = DemoUsageSimulator.Sample(2, Now).First(s => s.Provider == "gemini").Gauges[0].Utilization;
        var b = DemoUsageSimulator.Sample(5, Now).First(s => s.Provider == "gemini").Gauges[0].Utilization;
        Assert.NotEqual(a, b);
    }

    // ── Claude snapshot — all existing behavior preserved byte-for-byte ─────────

    [Fact]
    public void ProducesThreeCanonicalGaugesInOrder()
    {
        var snap = Claude(DemoUsageSimulator.Sample(10, Now));
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
        var snap = Claude(DemoUsageSimulator.Sample(0, Now));
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
        var a = Claude(DemoUsageSimulator.Sample(15.0, Now));
        var b = Claude(DemoUsageSimulator.Sample(15.0, Now));
        Assert.Equal(a.Gauges[0], b.Gauges[0]);
        Assert.Equal(a.Gauges[1], b.Gauges[1]);
        Assert.Equal(a.Gauges[2], b.Gauges[2]);
    }

    [Fact]
    public void GeminiSnapshot_IsDeterministic()
    {
        var a = DemoUsageSimulator.Sample(15.0, Now).First(s => s.Provider == "gemini");
        var b = DemoUsageSimulator.Sample(15.0, Now).First(s => s.Provider == "gemini");
        // Compare field-by-field: record equality on UsageSnapshot compares Gauges by
        // reference (List<T> is not a value type), so we unpack manually.
        Assert.Equal(a.Provider, b.Provider);
        Assert.Equal(a.CapturedAt, b.CapturedAt);
        Assert.Equal(a.Source, b.Source);
        Assert.Equal(a.Health, b.Health);
        Assert.Equal(a.Plan, b.Plan);
        Assert.Equal(a.Gauges.Count, b.Gauges.Count);
        for (var i = 0; i < a.Gauges.Count; i++)
            Assert.Equal(a.Gauges[i], b.Gauges[i]); // Gauge is a record → value equality
    }
}
