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
///
/// <para>
/// <b>Multi-provider demo:</b> <see cref="DemoUsageSimulator.Sample"/> returns one
/// snapshot per demo-supported provider (Claude first, then Gemini).  <see cref="Begin"/>
/// and <see cref="Advance"/> push every snapshot through
/// <see cref="UsageViewModel.ApplyDemoSnapshot"/>, which routes each to its own provider
/// group via <c>Provider</c> — no XAML changes required for additional providers.
/// </para>
///
/// <para>
/// <b>Stale-group fix:</b> <see cref="End"/> calls
/// <see cref="UsageViewModel.ClearGroups"/> before requesting a refresh, so a demo-only
/// provider that is NOT installed (e.g. Gemini absent) does not linger as a stale section
/// after exiting demo mode.  Live data repopulates only the groups whose providers are
/// actually present.
/// </para>
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

    /// <summary>Enters demo mode, resets the clock, and pushes the first frame for all demo providers.</summary>
    public void Begin()
    {
        _elapsedSeconds = 0;
        _vm.IsDemoMode = true;
        PushAll(_elapsedSeconds);
    }

    /// <summary>Advances the simulated clock and pushes the next frame. No-op unless in demo mode.</summary>
    public void Advance(double deltaSeconds)
    {
        if (!_vm.IsDemoMode) return;
        _elapsedSeconds += deltaSeconds;
        PushAll(_elapsedSeconds);
    }

    /// <summary>
    /// Leaves demo mode, clears demo-only provider groups, and asks the capture engine
    /// for fresh live data.  Groups are cleared first so providers shown in demo but not
    /// actually installed do not persist stale on the dashboard.
    /// </summary>
    public void End()
    {
        _vm.IsDemoMode = false;

        // Clear ALL groups before refresh — live data repopulates only the groups whose
        // providers are genuinely present.  ClearGroups must run on the UI thread; we
        // post it through the same _dispatcher path that ApplyDemoSnapshot uses.
        // UsageViewModel.ClearGroups is internal and must be called on the UI thread;
        // PostClearGroups posts via the dispatcher the vm already holds.
        _vm.PostClearGroups();

        _ = _client.SendAsync(ClientCommand.RefreshNow());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void PushAll(double elapsed)
    {
        var now = _nowUnix();
        foreach (var snap in DemoUsageSimulator.Sample(elapsed, now))
            _vm.ApplyDemoSnapshot(snap);
    }
}
