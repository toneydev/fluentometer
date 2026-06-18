// app/Fluentometer/DemoModeController.cs
using System;
using Fluentometer.Logic.Demo;
using Microsoft.UI.Xaml;

namespace Fluentometer;

/// <summary>
/// App-layer owner of the demo-mode clock. Wraps a 250 ms DispatcherTimer (4 Hz) that
/// advances the <see cref="DemoDriver"/>; GaugeControl spring-animates between frames,
/// so 4 Hz renders as smooth motion. Must be constructed on the UI thread.
/// </summary>
public sealed class DemoModeController
{
    private const double TickSeconds = 0.25;

    private readonly DemoDriver _driver;
    private readonly DispatcherTimer _timer;

    public DemoModeController(DemoDriver driver)
    {
        _driver = driver;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSeconds) };
        _timer.Tick += (_, _) => _driver.Advance(TickSeconds);
    }

    public bool IsEnabled { get; private set; }

    public void Enable()
    {
        if (IsEnabled) return;
        IsEnabled = true;
        _driver.Begin();
        _timer.Start();
    }

    public void Disable()
    {
        if (!IsEnabled) return;
        IsEnabled = false;
        _timer.Stop();
        _driver.End();
    }
}
