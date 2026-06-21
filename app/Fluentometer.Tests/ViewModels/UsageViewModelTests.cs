using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.Ui;
using Fluentometer.Logic.ViewModels;
using Xunit;

public class UsageViewModelTests
{
    private sealed class SyncDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class FakeClient : IUsageClient
    {
        public event Action<UsageSnapshot>? SnapshotReceived;
        public event Action<bool>? ConnectionChanged;
        public event Action<RefreshStatus>? StatusChanged;
        public ClientCommand? LastSent;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SendAsync(ClientCommand cmd) { LastSent = cmd; return Task.CompletedTask; }
        public void PushSnapshot(UsageSnapshot s) => SnapshotReceived?.Invoke(s);
        public void SetConnected(bool c) => ConnectionChanged?.Invoke(c);
        public void PushStatus(RefreshStatus s) => StatusChanged?.Invoke(s);
    }

    // -------------------------------------------------------------------------
    // Backward-compat: flat Gauges + Plan + Health still work for single provider
    // -------------------------------------------------------------------------

    [Fact]
    public void SnapshotUpdatesGaugesPlanAndHealth()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());

        client.PushSnapshot(new UsageSnapshot("claude", 1, "oauth", "ok", "Max 20x",
            new List<Gauge>
            {
                new("session", "Claude 5-hour", 0.42, "42%", 1700003600, "5-hour"),
                new("weekly_all", "Claude Weekly", 0.61, "61%", 1700300000, "Weekly"),
            }));

        Assert.Equal("Max 20x", vm.Plan);
        Assert.Equal("ok", vm.Health);
        Assert.Equal(2, vm.Gauges.Count);
        Assert.Equal(0.42, vm.Gauges[0].Utilization);
        Assert.Equal("Claude Weekly", vm.Gauges[1].Label);
    }

    [Fact]
    public void SecondSnapshotUpdatesInPlaceNotDuplicate()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());
        var g = new List<Gauge> { new("session", "Claude 5-hour", 0.10, "10%", 1, "5-hour") };
        client.PushSnapshot(new UsageSnapshot("claude", 1, "oauth", "ok", "Max", g));
        client.PushSnapshot(new UsageSnapshot("claude", 2, "oauth", "ok", "Max",
            new List<Gauge> { new("session", "Claude 5-hour", 0.20, "20%", 1, "5-hour") }));

        Assert.Single(vm.Gauges);
        Assert.Equal(0.20, vm.Gauges[0].Utilization);
    }

    [Fact]
    public void ConnectionChangedUpdatesIsConnected()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());
        client.SetConnected(true);
        Assert.True(vm.IsConnected);
        client.SetConnected(false);
        Assert.False(vm.IsConnected);
    }

    [Fact]
    public async Task RefreshCommandSendsRefreshNow()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("refreshNow", client.LastSent?.Type);
    }

    [Fact]
    public void ShrinkingGaugeListRemovesExtraViewModels()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());

        client.PushSnapshot(new UsageSnapshot("claude", 1, "oauth", "ok", "Max",
            new List<Gauge>
            {
                new("session", "Claude 5-hour", 0.30, "30%", 1700003600, "5-hour limit"),
                new("weekly_all", "Claude Weekly", 0.70, "70%", 1700604800, "weekly limit"),
                new("weekly_scoped", "Claude Weekly (Sonnet)", null, "~2M tokens", null, "estimate"),
            }));
        Assert.Equal(3, vm.Gauges.Count);

        // Second snapshot has only 1 gauge — extra ViewModels must be removed.
        client.PushSnapshot(new UsageSnapshot("claude", 2, "oauth", "ok", "Max",
            new List<Gauge>
            {
                new("session", "Claude 5-hour", 0.50, "50%", 1700003600, "5-hour limit"),
            }));

        Assert.Single(vm.Gauges);
        Assert.Equal("session", vm.Gauges[0].Id);
        Assert.Equal(0.50, vm.Gauges[0].Utilization);
    }

    [Fact]
    public void PlanAndHealthUpdateOnEachSnapshot()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());

        client.PushSnapshot(new UsageSnapshot("claude", 1, "oauth", "ok", "Max", new List<Gauge>()));
        Assert.Equal("ok", vm.Health);
        Assert.Equal("Max", vm.Plan);

        client.PushSnapshot(new UsageSnapshot("claude", 2, "jsonl", "needs-signin", "Pro", new List<Gauge>()));
        Assert.Equal("needs-signin", vm.Health);
        Assert.Equal("Pro", vm.Plan);
    }

    [Fact]
    public void LiveSnapshotsAreIgnoredWhileInDemoMode()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());

        vm.ApplyDemoSnapshot(new UsageSnapshot("claude", 1, "demo", "ok", "Demo",
            new List<Gauge> { new("session", "Claude 5-hour", 0.10, "10%", 1, "5-hour limit") }));
        vm.IsDemoMode = true;

        // A live snapshot arriving while demoing must NOT overwrite the gauges.
        client.PushSnapshot(new UsageSnapshot("claude", 2, "oauth", "degraded", "Max",
            new List<Gauge> { new("session", "Claude 5-hour", 0.99, "99%", 1, "5-hour limit") }));

        Assert.Equal(0.10, vm.Gauges[0].Utilization);
        Assert.Equal("Demo", vm.Plan);
    }

    [Fact]
    public void ApplyDemoSnapshotUpdatesGauges()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());

        vm.ApplyDemoSnapshot(new UsageSnapshot("claude", 1, "demo", "ok", "Demo",
            new List<Gauge>
            {
                new("session", "Claude 5-hour", 0.33, "33%", 1, "5-hour limit"),
                new("weekly_all", "Claude Weekly", 0.50, "50%", 1, "weekly limit"),
            }));

        Assert.Equal(2, vm.Gauges.Count);
        Assert.Equal(0.33, vm.Gauges[0].Utilization);
    }

    [Fact]
    public void LiveSnapshotsResumeAfterDemoModeOff()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());
        vm.IsDemoMode = true;
        vm.IsDemoMode = false;

        client.PushSnapshot(new UsageSnapshot("claude", 3, "oauth", "ok", "Max",
            new List<Gauge> { new("session", "Claude 5-hour", 0.77, "77%", 1, "5-hour limit") }));

        Assert.Equal(0.77, vm.Gauges[0].Utilization);
    }

    // -------------------------------------------------------------------------
    // Multi-provider (Groups) tests
    // -------------------------------------------------------------------------

    [Fact]
    public void MultipleProvidersCreateSeparateGroups()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());

        client.PushSnapshot(new UsageSnapshot("claude", 1, "oauth", "ok", "Max",
            new List<Gauge> { new("session", "Claude 5-hour", 0.42, "42%", 1, "5-hour") }));
        client.PushSnapshot(new UsageSnapshot("gemini", 1, "local", "ok", "Gemini",
            new List<Gauge> { new("daily", "Gemini Daily", 0.10, "10%", 1, "daily") }));

        Assert.Equal(2, vm.Groups.Count);
        Assert.Equal("claude", vm.Groups[0].ProviderId);
        Assert.Equal("gemini", vm.Groups[1].ProviderId);
    }

    [Fact]
    public void FlatGaugesContainsAllGroupGaugesInOrder()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());

        client.PushSnapshot(new UsageSnapshot("claude", 1, "oauth", "ok", "Max",
            new List<Gauge>
            {
                new("session", "Claude 5-hour", 0.42, "42%", 1, "5-hour"),
                new("weekly_all", "Claude Weekly", 0.61, "61%", 1, "weekly"),
            }));
        client.PushSnapshot(new UsageSnapshot("gemini", 1, "local", "ok", "Gemini",
            new List<Gauge> { new("daily", "Gemini Daily", 0.10, "10%", 1, "daily") }));

        // Flat Gauges = Claude's 2 + Gemini's 1
        Assert.Equal(3, vm.Gauges.Count);
        Assert.Equal("Claude 5-hour", vm.Gauges[0].Label);
        Assert.Equal("Claude Weekly", vm.Gauges[1].Label);
        Assert.Equal("Gemini Daily", vm.Gauges[2].Label);
    }

    [Fact]
    public void HealthRollupPicksWorstAcrossGroups()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());

        // Claude ok, Gemini degraded → rollup should be degraded.
        client.PushSnapshot(new UsageSnapshot("claude", 1, "oauth", "ok", "Max", new List<Gauge>()));
        client.PushSnapshot(new UsageSnapshot("gemini", 1, "local", "degraded", "Gemini", new List<Gauge>()));

        Assert.Equal("degraded", vm.Health);
    }

    [Fact]
    public void HealthRollupNeedsSigninOutranksDegraded()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());

        client.PushSnapshot(new UsageSnapshot("claude", 1, "jsonl", "degraded", "Max", new List<Gauge>()));
        client.PushSnapshot(new UsageSnapshot("gemini", 1, "local", "needs-signin", "Gemini", new List<Gauge>()));

        Assert.Equal("needs-signin", vm.Health);
    }

    [Fact]
    public void HealthRollupErrorOutranksNeedsSignin()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());

        client.PushSnapshot(new UsageSnapshot("claude", 1, "jsonl", "needs-signin", "Max", new List<Gauge>()));
        client.PushSnapshot(new UsageSnapshot("gemini", 1, "local", "error", "Gemini", new List<Gauge>()));

        Assert.Equal("error", vm.Health);
    }

    [Fact]
    public void SecondSnapshotSameProviderUpdatesGroupInPlace()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());

        client.PushSnapshot(new UsageSnapshot("claude", 1, "oauth", "ok", "Max",
            new List<Gauge> { new("session", "Claude 5-hour", 0.10, "10%", 1, "5-hour") }));
        // Only one group so far.
        Assert.Single(vm.Groups);

        client.PushSnapshot(new UsageSnapshot("claude", 2, "oauth", "ok", "Max",
            new List<Gauge> { new("session", "Claude 5-hour", 0.20, "20%", 1, "5-hour") }));
        // Still one group, updated value.
        Assert.Single(vm.Groups);
        Assert.Equal(0.20, vm.Groups[0].Gauges[0].Utilization);
    }

    [Fact]
    public void ProviderGroupTitleCapitalizesProviderId()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());

        client.PushSnapshot(new UsageSnapshot("gemini", 1, "local", "ok", "Gemini", new List<Gauge>()));

        Assert.Equal("Gemini", vm.Groups[0].Title);
    }

    [Fact]
    public void DemoSnapshotRoutedThroughGroupPath()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());

        vm.ApplyDemoSnapshot(new UsageSnapshot("claude", 1, "demo", "ok", "Demo",
            new List<Gauge>
            {
                new("session", "Claude 5-hour", 0.33, "33%", 1, "5-hour limit"),
            }));

        // Demo snapshot must create a "claude" group and populate its gauges.
        Assert.Single(vm.Groups);
        Assert.Equal("claude", vm.Groups[0].ProviderId);
        Assert.Single(vm.Groups[0].Gauges);
        Assert.Equal(0.33, vm.Groups[0].Gauges[0].Utilization);

        // Flat Gauges also reflects it.
        Assert.Single(vm.Gauges);
        Assert.Equal(0.33, vm.Gauges[0].Utilization);
    }

    // -------------------------------------------------------------------------
    // Staleness signal (Task 3)
    // -------------------------------------------------------------------------

    [Fact]
    public void TwoFailures_ThenEvaluate_SetsIsStaleAndDetail()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());

        client.PushStatus(new RefreshStatus("claude", null, 2, 180));
        vm.EvaluateStaleness(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));

        Assert.True(vm.IsStale);
        Assert.Contains("Claude", vm.StatusDetail);
    }

    [Fact]
    public void Success_KeepsIsStaleFalse()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher());
        var now = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);

        client.PushStatus(new RefreshStatus("claude", now, 0, 180));
        vm.EvaluateStaleness(now);

        Assert.False(vm.IsStale);
        Assert.Equal("", vm.StatusDetail);
    }

    [Fact]
    public void DemoMode_SuppressesStaleness()
    {
        var client = new FakeClient();
        var vm = new UsageViewModel(client, new SyncDispatcher()) { IsDemoMode = true };

        client.PushStatus(new RefreshStatus("claude", null, 5, 180));
        vm.EvaluateStaleness(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));

        Assert.False(vm.IsStale);
    }
}
