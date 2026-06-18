// app/Fluentometer.Tests/Demo/DemoDriverTests.cs
using System;
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

    [Fact]
    public void BeginEntersDemoModeAndPopulatesThreeGauges()
    {
        var (vm, _, driver) = Build();
        driver.Begin();
        Assert.True(vm.IsDemoMode);
        Assert.Equal(3, vm.Gauges.Count);
    }

    [Fact]
    public void AdvanceMovesTheGaugesForward()
    {
        var (vm, _, driver) = Build();
        driver.Begin();
        driver.Advance(15.0);
        Assert.True(vm.Gauges[1].Utilization > 0.0); // weekly has climbed
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
            new System.Collections.Generic.List<Gauge>
            { new("session", "Claude 5-hour", 0.5, "50%", 1, "5-hour limit") }));
        Assert.Single(vm.Gauges);
        Assert.Equal("Max", vm.Plan);
    }
}
