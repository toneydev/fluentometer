// app/Fluentometer.Tests/Demo/DemoDriverTests.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Demo;
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.Ui;
using Fluentometer.Logic.ViewModels;
using Xunit;

public class DemoDriverTests
{
    private sealed class SyncDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class FakeClient : IUsageClient
    {
        public event Action<UsageSnapshot>? SnapshotReceived;
#pragma warning disable CS0067  // ConnectionChanged satisfies IUsageClient; never raised in tests
        public event Action<bool>? ConnectionChanged;
#pragma warning restore CS0067
        public ClientCommand? LastSent;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SendAsync(ClientCommand cmd) { LastSent = cmd; return Task.CompletedTask; }
        public void PushSnapshot(UsageSnapshot s) => SnapshotReceived?.Invoke(s);
    }

    private static (UsageViewModel vm, FakeClient client, DemoDriver driver) Build()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());
        var driver = new DemoDriver(vm, client, () => 1_700_000_000);
        return (vm, client, driver);
    }

    // ── Existing behaviour — preserved ─────────────────────────────────────────

    [Fact]
    public void BeginEntersDemoMode()
    {
        var (vm, _, driver) = Build();
        driver.Begin();
        Assert.True(vm.IsDemoMode);
    }

    [Fact]
    public void AdvanceMovesTheGaugesForward()
    {
        var (vm, _, driver) = Build();
        driver.Begin();
        driver.Advance(15.0);
        // Claude weekly (provider 0, gauge 1) has climbed above zero.
        var claudeGroup = vm.Groups[0];
        Assert.True(claudeGroup.Gauges[1].Utilization > 0.0);
    }

    [Fact]
    public void AdvanceBeforeBeginDoesNothing()
    {
        var (vm, _, driver) = Build();
        driver.Advance(5.0);
        Assert.Empty(vm.Gauges);
        Assert.False(vm.IsDemoMode);
    }

    [Fact]
    public void EndLeavesDemoModeAndRequestsRefresh()
    {
        var (vm, client, driver) = Build();
        driver.Begin();
        driver.End();
        Assert.False(vm.IsDemoMode);
        Assert.Equal("refreshNow", client.LastSent?.Type);
    }

    [Fact]
    public void LiveDataReturnsAfterEnd()
    {
        var (vm, client, driver) = Build();
        driver.Begin();
        driver.End();
        client.PushSnapshot(new UsageSnapshot("claude", 1, "oauth", "ok", "Max",
            new List<Gauge>
            { new("session", "Claude 5-hour", 0.5, "50%", 1, "5-hour limit") }));
        Assert.Equal("Max", vm.Plan);
    }

    // ── Multi-provider Begin() ──────────────────────────────────────────────────

    [Fact]
    public void Begin_PopulatesClaudeChatGptAndGeminiGroups()
    {
        var (vm, _, driver) = Build();
        driver.Begin();

        // Should have three provider groups: claude [0], chatgpt [1], gemini [2].
        Assert.Equal(3, vm.Groups.Count);
        Assert.Equal("claude", vm.Groups[0].ProviderId);
        Assert.Equal("chatgpt", vm.Groups[1].ProviderId);
        Assert.Equal("gemini", vm.Groups[2].ProviderId);
    }

    [Fact]
    public void Begin_ClaudeGroupHasThreeGauges()
    {
        var (vm, _, driver) = Build();
        driver.Begin();
        Assert.Equal(3, vm.Groups[0].Gauges.Count);
    }

    [Fact]
    public void Begin_GeminiGroupHasOneGaugeWithRealUtilization()
    {
        var (vm, _, driver) = Build();
        driver.Begin();

        // Gemini is now at index 2 (Claude [0], ChatGPT [1], Gemini [2]).
        // Gemini is now server-truth: Utilization is a real animated percent (not null).
        var geminiGroup = vm.Groups[2];
        Assert.Single(geminiGroup.Gauges);
        Assert.NotNull(geminiGroup.Gauges[0].Utilization);
    }

    [Fact]
    public void Begin_GeminiGaugeIsNotMarkedAsEstimate()
    {
        var (vm, _, driver) = Build();
        driver.Begin();
        // Gemini is now at index 2 (Claude [0], ChatGPT [1], Gemini [2]).
        // Server-truth gauge: IsEstimate must be false (Utilization != null).
        var geminiGauge = vm.Groups[2].Gauges[0];
        Assert.False(geminiGauge.IsEstimate);
    }

    [Fact]
    public void Begin_FlatGaugesContainsAllDemoGauges()
    {
        var (vm, _, driver) = Build();
        driver.Begin();
        // Claude has 3, ChatGPT has 2, Gemini has 1 → total 6 in flat mirror.
        Assert.Equal(6, vm.Gauges.Count);
    }

    // ── End() stale-group fix ───────────────────────────────────────────────────

    [Fact]
    public void End_ClearsAllDemoGroups_BeforeRefresh()
    {
        var (vm, _, driver) = Build();
        driver.Begin();

        // Verify demo groups are there before End: Claude [0], ChatGPT [1], Gemini [2].
        Assert.Equal(3, vm.Groups.Count);

        driver.End();

        // After End(), all demo-only groups must be cleared so a provider that
        // isn't actually installed (e.g. Gemini absent) doesn't persist on the dashboard.
        Assert.Empty(vm.Groups);
        Assert.Empty(vm.Gauges);
    }

    [Fact]
    public void End_DemoOnlyGeminiGroupDoesNotPersistAfterLiveClaudeSnapshot()
    {
        var (vm, client, driver) = Build();
        driver.Begin();
        driver.End();

        // Simulate live data repopulating Claude — Gemini is not installed, so no
        // Gemini snapshot arrives. The dashboard must show only Claude.
        client.PushSnapshot(new UsageSnapshot("claude", 1, "oauth", "ok", "Max",
            new List<Gauge>
            {
                new("session", "Claude 5-hour", 0.5, "50%", 1, "5-hour limit"),
                new("weekly_all", "Claude Weekly", 0.3, "30%", 1, "weekly limit"),
            }));

        Assert.Single(vm.Groups);
        Assert.Equal("claude", vm.Groups[0].ProviderId);
        Assert.Equal(2, vm.Gauges.Count);
    }

    [Fact]
    public void End_LiveClaudeSnapshotLandsCorrectlyAfterDemoExit()
    {
        var (vm, client, driver) = Build();
        driver.Begin();
        driver.End();

        client.PushSnapshot(new UsageSnapshot("claude", 1, "oauth", "ok", "Max",
            new List<Gauge>
            { new("session", "Claude 5-hour", 0.5, "50%", 1, "5-hour limit") }));

        Assert.Equal("Max", vm.Plan);
        Assert.Single(vm.Gauges);
        Assert.Equal(0.5, vm.Gauges[0].Utilization);
    }
}
