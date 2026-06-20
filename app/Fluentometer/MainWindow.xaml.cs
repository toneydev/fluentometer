using System;
using Fluentometer.Ui;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Fluentometer;

/// <summary>
/// Shell window.  Navigation and dependency injection are performed by
/// App.xaml.cs (the composition root) via <see cref="RootFrame"/> after the
/// DI container is built.
/// Close is intercepted: the window hides to the system tray rather than
/// exiting, so monitoring continues in the background.
/// </summary>
public sealed partial class MainWindow : Window
{
    // Set to true when the user explicitly picks Quit from the tray.
    private bool _reallyClose;

    public MainWindow()
    {
        InitializeComponent();

        // -----------------------------------------------------------------------
        // Mica backdrop (degrades gracefully to solid on unsupported systems)
        // -----------------------------------------------------------------------
        SystemBackdrop = new MicaBackdrop();

        // -----------------------------------------------------------------------
        // Custom title bar (extends content under caption buttons)
        // -----------------------------------------------------------------------
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);

        // -----------------------------------------------------------------------
        // Window size — a sensible default for a small dashboard widget.
        // -----------------------------------------------------------------------
        var appWindow = AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(720, 560));

        // -----------------------------------------------------------------------
        // Minimum window size (effective px). WinUI exposes no min-size API, so we
        // subclass the HWND (WindowMinSize). This is a crash guard, not just polish:
        // shrinking the dashboard below the gauge layout's MinItemWidth makes
        // UniformGridLayout divide by a zero items-per-line count, throwing
        // DivideByZeroException in MeasureOverride and killing the process.
        // -----------------------------------------------------------------------
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Ui.WindowMinSize.Apply(hwnd, minWidth: 280, minHeight: 260);

        // -----------------------------------------------------------------------
        // Window icon — must be set at runtime for unpackaged WinUI 3.
        // <ApplicationIcon> in the .csproj embeds the icon in the exe so that
        // Windows Explorer and the installer can display it, but AppWindow does NOT
        // automatically pick up the embedded resource at runtime.  Both steps are
        // required: embed (csproj) AND set here (runtime), or the title bar, taskbar,
        // and alt-tab switcher all show the default blank Windows icon.
        // AppContext.BaseDirectory resolves to the exe's directory, which is safe for
        // unpackaged apps (ms-appx:/// requires package identity and will not work).
        // -----------------------------------------------------------------------
        appWindow.SetIcon(System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "app.ico"));

        // -----------------------------------------------------------------------
        // Intercept close: hide to tray instead of exiting.
        // The Closed event fires *after* the window is destroyed, so we use
        // AppWindow.Closing which fires before destruction and is cancellable.
        // -----------------------------------------------------------------------
        appWindow.Closing += OnWindowClosing;
    }

    // -------------------------------------------------------------------------
    // Window visibility — surfaced for DashboardPage tray-hide optimisation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fired (on the UI thread) when the window transitions between visible and
    /// hidden.  Subscribers receive <c>true</c> when the window becomes visible
    /// and <c>false</c> when it is hidden to the tray.
    /// </summary>
    public event Action<bool>? WindowVisibilityChanged;

    // -------------------------------------------------------------------------
    // Close → hide to tray
    // -------------------------------------------------------------------------

    /// <summary>
    /// Marks this window so the next AppWindow.Closing event really closes.
    /// Called by TrayIconManager when the user picks "Quit".
    /// </summary>
    public void AllowClose()
    {
        _reallyClose = true;
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_reallyClose) return; // Quit was requested — let it close.

        // Hide to tray and cancel the close.
        args.Cancel = true;
        this.Hide(enableEfficiencyMode: false);

        // Notify listeners (e.g. DashboardPage) that the window is now hidden.
        WindowVisibilityChanged?.Invoke(false);
    }

    /// <summary>
    /// Shows the window and notifies visibility listeners.
    /// Called by TrayIconManager on left-click / "Open".
    /// </summary>
    public void ShowWindow()
    {
        this.Show(disableEfficiencyMode: false);
        WindowVisibilityChanged?.Invoke(true);
    }
}
