using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Fluentometer.Logic.ViewModels;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Fluentometer;

/// <summary>
/// Owns the system-tray icon for the lifetime of the application.
/// Left-click shows/activates MainWindow; context menu provides Open / Refresh / Quit.
/// Tooltip updates live from UsageViewModel property changes.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly MainWindow _window;
    private readonly UsageViewModel _vm;
    private readonly Action _quit;
    private readonly TaskbarIcon _trayIcon;
    private bool _disposed;

    // Named handler stored as a field so it can be unsubscribed in Dispose().
    private readonly NotifyCollectionChangedEventHandler _onGaugesChanged;

    public TrayIconManager(MainWindow window, UsageViewModel vm, Action quit)
    {
        _window = window;
        _vm = vm;
        _quit = quit;

        _trayIcon = BuildTrayIcon();

        // Update tooltip whenever VM properties (IsConnected, Health) change.
        _vm.PropertyChanged += OnVmPropertyChanged;

        // Update tooltip when gauges are added/removed.  On the first poll the
        // capture engine populates _vm.Gauges via CollectionChanged (the collection reference
        // itself never changes), so PropertyChanged never fires for the gauges data —
        // we must subscribe here to catch that initial population.
        // UsageViewModel marshals Gauges mutations to the UI thread via IUiDispatcher,
        // so CollectionChanged always arrives on the UI thread; no extra marshal needed.
        _onGaugesChanged = (_, _) => UpdateTooltip();
        _vm.Gauges.CollectionChanged += _onGaugesChanged;

        UpdateTooltip();
    }

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    private TaskbarIcon BuildTrayIcon()
    {
        // --- Context menu (MenuFlyout is the WinUI equivalent of ContextMenu) ---
        var openItem = new MenuFlyoutItem { Text = "Open" };
        AutomationProperties.SetName(openItem, "Open Fluentometer");
        openItem.Click += (_, _) => ShowWindow();

        var refreshItem = new MenuFlyoutItem { Text = "Refresh" };
        AutomationProperties.SetName(refreshItem, "Refresh usage data");
        refreshItem.Click += async (_, _) =>
        {
            if (_vm.RefreshCommand.CanExecute(null))
                await _vm.RefreshCommand.ExecuteAsync(null).ConfigureAwait(false);
        };

        var quitItem = new MenuFlyoutItem { Text = "Quit" };
        AutomationProperties.SetName(quitItem, "Quit Fluentometer");
        quitItem.Click += (_, _) =>
        {
            _window.AllowClose();
            _quit();
        };

        var flyout = new MenuFlyout();
        flyout.Items.Add(openItem);
        flyout.Items.Add(refreshItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(quitItem);

        // --- Branded tray icon loaded from Assets\tray.ico beside the exe ---
        // TaskbarIcon.IconSource accepts Microsoft.UI.Xaml.Media.ImageSource.
        // BitmapImage (a subclass of ImageSource) is the correct type for a file-based
        // .ico in an UNPACKAGED app: ms-appx:/// is unavailable unpackaged, so we build
        // an absolute file:// URI from AppContext.BaseDirectory instead.
        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
        var iconSource = new BitmapImage(new Uri(icoPath));

        // --- TaskbarIcon ---
        var icon = new TaskbarIcon
        {
            ToolTipText = "Fluentometer",
            ContextFlyout = flyout,
            // Left click shows the window; right click shows the context menu.
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
            IconSource = iconSource,
            LeftClickCommand = new ShowWindowCommand(ShowWindow),
        };

        // ForceCreate registers the icon with Windows Shell so it appears in
        // the tray even when the main window is hidden.
        icon.ForceCreate(enablesEfficiencyMode: false);

        return icon;
    }

    // -------------------------------------------------------------------------
    // Tooltip update
    // -------------------------------------------------------------------------

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        if (_trayIcon.IsDisposed) return;

        var sb = new StringBuilder();

        if (_vm.Gauges.Count > 0)
        {
            // Build "Claude 5-hour 42% · Claude Weekly 61%" style from whichever gauges are populated.
            var parts = _vm.Gauges
                .Select(g =>
                {
                    var pct = g.Utilization.HasValue
                        ? $"{(int)(g.Utilization.Value * 100)}%"
                        : g.UsedLabel;
                    return $"{g.Label} {pct}";
                });
            sb.Append(string.Join(" · ", parts));
        }
        else
        {
            sb.Append("Fluentometer");
        }

        if (!_vm.IsConnected)
            sb.Append(" (offline)");

        // Tooltip text is capped at 127 chars by the Windows Shell API.
        var text = sb.ToString();
        if (text.Length > 127)
            text = string.Concat(text.AsSpan(0, 124), "...");

        _trayIcon.ToolTipText = text;
    }

    // -------------------------------------------------------------------------
    // Show / activate main window
    // -------------------------------------------------------------------------

    private void ShowWindow()
    {
        _window.ShowWindow();
        _window.Activate();
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Gauges.CollectionChanged -= _onGaugesChanged;
        _trayIcon.Dispose();
    }

    // -------------------------------------------------------------------------
    // Helper ICommand for left-click
    // -------------------------------------------------------------------------

    /// <summary>
    /// Minimal ICommand that wraps an Action — used for TaskbarIcon.LeftClickCommand.
    /// </summary>
    private sealed class ShowWindowCommand(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
