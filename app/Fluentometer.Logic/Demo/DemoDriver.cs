// app/Fluentometer.Logic/Demo/DemoDriver.cs
using System;
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.ViewModels;

namespace Fluentometer.Logic.Demo;

/// <summary>
/// Timer-agnostic driver for Demonstration Mode. Holds the elapsed-time state and
/// pushes simulated snapshots into the view model. A platform timer (the app-layer
/// DemoModeController) calls <see cref="Advance"/> on each tick; this type owns no
/// timer so it stays free of WinUI dependencies and is unit-testable.
/// </summary>
public sealed class DemoDriver
{
    private readonly UsageViewModel _vm;
    private readonly IUsageClient _client;
    private readonly Func<long> _nowUnix;
    private double _elapsedSeconds;

    public DemoDriver(UsageViewModel vm, IUsageClient client, Func<long> nowUnix)
    {
        _vm = vm;
        _client = client;
        _nowUnix = nowUnix;
    }

    /// <summary>Enters demo mode, resets the clock, and pushes the first frame.</summary>
    public void Begin()
    {
        _elapsedSeconds = 0;
        _vm.IsDemoMode = true;
        _vm.ApplyDemoSnapshot(DemoUsageSimulator.Sample(_elapsedSeconds, _nowUnix()));
    }

    /// <summary>Advances the simulated clock and pushes the next frame. No-op unless in demo mode.</summary>
    public void Advance(double deltaSeconds)
    {
        if (!_vm.IsDemoMode) return;
        _elapsedSeconds += deltaSeconds;
        _vm.ApplyDemoSnapshot(DemoUsageSimulator.Sample(_elapsedSeconds, _nowUnix()));
    }

    /// <summary>Leaves demo mode and asks the capture engine for fresh live data.</summary>
    public void End()
    {
        _vm.IsDemoMode = false;
        _ = _client.SendAsync(ClientCommand.RefreshNow());
    }
}
