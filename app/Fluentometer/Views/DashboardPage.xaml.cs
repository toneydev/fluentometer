using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Fluentometer; // DemoModeController
using Fluentometer.Controls;
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.Settings;
using Fluentometer.Logic.Theming;
using Fluentometer.Logic.Ui;
using Fluentometer.Logic.ViewModels;
using Fluentometer.Settings;
using Fluentometer.Ui;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Fluentometer.Views;

/// <summary>
/// Dashboard page showing per-gauge gradient panels bound to UsageViewModel.
///
/// Accepts ViewModel and ThemeService via SetViewModel so Task 9 can inject them
/// through the DI composition root without modifying this class.
/// </summary>
public sealed partial class DashboardPage : Page
{
    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private UsageViewModel? _vm;
    private ThemeService? _themeService;
    private DispatcherTimer? _countdownTimer;
    private bool _wired;

    // Settings dependencies (injected via SetSettingsDependencies).
    private IUsageClient? _client;
    private FileThemeStore? _fileThemeStore;
    private ILaunchOnLogin? _launchOnLogin;
    private DemoModeController? _demoController;

    // Brush cache: keyed by (themeId, GradientDirection) to avoid reallocating brushes
    // on every ApplyTheme call or ItemsRepeater ElementPrepared event.
    // LinearGradientBrush is safely shareable across multiple elements in WinUI 3.
    private readonly Dictionary<(string, GradientDirection), (LinearGradientBrush Fill, LinearGradientBrush Glow)>
        _brushCache = new();

    // Window visibility hook — stops the 1 Hz timer while the window is hidden to tray.
    private MainWindow? _mainWindow;
    private Action<bool>? _visibilityHandler;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------
    public DashboardPage()
    {
        InitializeComponent();
        // Keep this instance alive across Frame navigation (Settings round-trip),
        // so the once-injected ViewModel isn't lost when the user returns.
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        GaugeRepeater.ElementPrepared += GaugeRepeater_ElementPrepared;
        Loaded += DashboardPage_Loaded;
        Unloaded += DashboardPage_Unloaded;
    }

    // -------------------------------------------------------------------------
    // Public injection point (used by Task 9 DI; placeholder in MainWindow)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Injects the view model and theme service.  Call before the page is loaded,
    /// or call once after construction — the page will re-bind itself.
    /// </summary>
    public void SetViewModel(UsageViewModel vm, ThemeService themeService)
    {
        TearDown();                       // detach from any previous VM
        _vm = vm;
        _themeService = themeService;
        RefreshButton.Command = vm.RefreshCommand;

        // If the page is already in the visual tree, wire up now; otherwise
        // DashboardPage_Loaded will call WireUp once it loads.
        if (IsLoaded) WireUp();
    }

    /// <summary>
    /// Subscribe to VM/theme events and apply current data. Idempotent and safe to
    /// call on every Loaded (e.g. after returning from the cached Settings page).
    /// </summary>
    private void WireUp()
    {
        if (_vm is null || _themeService is null || _wired) return;

        _vm.Gauges.CollectionChanged += Gauges_CollectionChanged;
        _vm.PropertyChanged += Vm_PropertyChanged;
        _themeService.ThemeChanged += ThemeService_ThemeChanged;

        GaugeRepeater.ItemsSource = _vm.Gauges;
        UpdateHeader();
        ApplyTheme(_themeService.Current);
        _wired = true;
    }

    private void TearDown()
    {
        if (_vm is not null)
        {
            _vm.Gauges.CollectionChanged -= Gauges_CollectionChanged;
            _vm.PropertyChanged -= Vm_PropertyChanged;
        }
        if (_themeService is not null)
        {
            _themeService.ThemeChanged -= ThemeService_ThemeChanged;
        }
        // Unsubscribe the window-visibility handler to avoid a leak when the cached
        // page is torn down or the window reference changes.
        if (_mainWindow is not null && _visibilityHandler is not null)
        {
            _mainWindow.WindowVisibilityChanged -= _visibilityHandler;
            _visibilityHandler = null;
        }
        _wired = false;
    }

    /// <summary>
    /// Wires the dashboard to the hosting <see cref="MainWindow"/> so the countdown
    /// timer pauses while the window is hidden to the tray.
    /// Called once from <see cref="MainWindow.InjectDependencies"/> after navigation.
    /// </summary>
    public void SetWindow(MainWindow window)
    {
        // Unsubscribe any previous handler in case this is called more than once.
        if (_mainWindow is not null && _visibilityHandler is not null)
            _mainWindow.WindowVisibilityChanged -= _visibilityHandler;

        _mainWindow = window;
        _visibilityHandler = OnWindowVisibilityChanged;
        _mainWindow.WindowVisibilityChanged += _visibilityHandler;
    }

    private void OnWindowVisibilityChanged(bool isVisible)
    {
        // Runs on the UI thread (fired by MainWindow on show/hide).
        if (isVisible)
        {
            // Immediately refresh so the countdown isn't stale for up to 1s.
            RefreshCountdowns();
            StartCountdownTimer();
        }
        else
        {
            StopCountdownTimer();
        }
    }

    /// <summary>
    /// Injects the settings-layer dependencies needed to navigate to SettingsPage.
    /// Called from App.xaml.cs after DI setup.
    /// </summary>
    public void SetSettingsDependencies(
        IUsageClient client,
        FileThemeStore fileThemeStore,
        ILaunchOnLogin launchOnLogin,
        DemoModeController demoController)
    {
        _client = client;
        _fileThemeStore = fileThemeStore;
        _launchOnLogin = launchOnLogin;
        _demoController = demoController;

        SettingsButton.Click += (_, _) => NavigateToSettings();
    }

    private void NavigateToSettings()
    {
        if (_themeService is null || _client is null ||
            _fileThemeStore is null || _launchOnLogin is null || _demoController is null) return;

        Frame.Navigate(typeof(SettingsPage));
        if (Frame.Content is SettingsPage settings)
        {
            settings.Initialize(_themeService, _client, _fileThemeStore, _launchOnLogin, _demoController);
        }
    }

    // -------------------------------------------------------------------------
    // Page lifecycle
    // -------------------------------------------------------------------------

    private void DashboardPage_Loaded(object sender, RoutedEventArgs e)
    {
        StartCountdownTimer();
        // (Re)wire after the page loads — including when the cached instance is
        // restored on returning from Settings (Frame.GoBack).
        WireUp();
    }

    private void DashboardPage_Unloaded(object sender, RoutedEventArgs e)
    {
        StopCountdownTimer();
        TearDown();
    }

    // -------------------------------------------------------------------------
    // Theme
    // -------------------------------------------------------------------------

    private void ThemeService_ThemeChanged(GradientTheme theme)
    {
        // ThemeChanged fires from the Logic layer — may be on any thread.
        DispatcherQueue.TryEnqueue(() => ApplyTheme(theme));
    }

    /// <summary>
    /// Rebuilds the bar fill gradient and leading-edge glow on every realized gauge.
    /// Value text stays white (fixed in XAML); the card background is a fixed dark charcoal.
    /// Called on load and on every ThemeService.ThemeChanged event.
    /// </summary>
    private void ApplyTheme(GradientTheme theme)
    {
        if (_vm is null || _themeService is null) return;

        var accentColor = ColorParser.Parse(theme.Accent);
        var direction = _themeService.Direction;

        for (var i = 0; i < _vm.Gauges.Count; i++)
        {
            var container = GaugeRepeater.TryGetElement(i);
            if (container is null) continue;
            ApplyThemeToPanel(container, theme, accentColor, direction);
        }
    }

    private void ApplyThemeToPanel(
        UIElement container, GradientTheme theme, Color accentColor, GradientDirection direction)
    {
        if (container is not Border panel || panel.Child is not Grid grid) return;

        var gauge = FindNamedChild<GaugeControl>(grid, "GaugeBar");
        if (gauge is null) return;

        var (fill, glow) = GetOrBuildBrushes(theme, accentColor, direction);
        gauge.Fill = fill;
        gauge.Glow = glow;
        // Value text stays white (set in XAML).
    }

    /// <summary>
    /// Returns cached brush pair for the given theme + direction, building and
    /// caching them on first access.  Both brushes depend only on theme and direction,
    /// so the (themeId, direction) key is sufficient for correctness.
    /// </summary>
    private (LinearGradientBrush Fill, LinearGradientBrush Glow) GetOrBuildBrushes(
        GradientTheme theme, Color accentColor, GradientDirection direction)
    {
        var key = (theme.Id, direction);
        if (_brushCache.TryGetValue(key, out var cached)) return cached;

        // Angled blend (~22deg on wide cards); colour order set by the direction option.
        var fill = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 2),
        };
        foreach (var (color, offset) in GradientStops.OrderedStops(theme.BarStops, direction))
        {
            fill.GradientStops.Add(new GradientStop { Color = ColorParser.Parse(color), Offset = offset });
        }

        // Leading-edge glow: a soft transparent→accent bloom across the glow band (GaugeControl.GlowWidth).
        var glow = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
        };
        glow.GradientStops.Add(new GradientStop
        {
            Color = Color.FromArgb(0x00, accentColor.R, accentColor.G, accentColor.B),
            Offset = 0,
        });
        glow.GradientStops.Add(new GradientStop
        {
            Color = Color.FromArgb(0x33, accentColor.R, accentColor.G, accentColor.B),
            Offset = 1,
        });

        var pair = (fill, glow);
        _brushCache[key] = pair;
        return pair;
    }

    // -------------------------------------------------------------------------
    // ItemsRepeater events — keep panel UI in sync with GaugeViewModel
    // -------------------------------------------------------------------------

    private void GaugeRepeater_ElementPrepared(
        ItemsRepeater sender,
        ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Border panel) return;

        // The gauge value/bar/detail/estimate are data-bound (x:Bind) on this panel,
        // so they always follow the panel's current data item — no imperative push,
        // no leaked PropertyChanged subscription. We only do two things here that
        // x:Bind can't: paint the theme brushes, and seed the time-based countdown
        // so it isn't blank until the next 1s tick.
        if (_themeService is not null)
        {
            var accentColor = ColorParser.Parse(_themeService.Current.Accent);
            ApplyThemeToPanel(panel, _themeService.Current, accentColor, _themeService.Direction);
        }

        if (_vm is not null && args.Index >= 0 && args.Index < _vm.Gauges.Count
            && panel.Child is Grid grid)
        {
            RefreshCountdown(grid, _vm.Gauges[args.Index]);
        }
    }

    private void Gauges_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Rebind countdowns when the collection changes.
        RefreshCountdowns();
    }

    // -------------------------------------------------------------------------
    // Countdown refresh helpers (time-based; cannot be a static x:Bind, so the
    // 1s timer + index-mapped lookup drive these — both map current index → current
    // GaugeViewModel, so they cannot desync the way the old value path did).
    // -------------------------------------------------------------------------

    private void RefreshCountdown(Grid grid, GaugeViewModel gauge)
    {
        var countdown = FindNamedChild<TextBlock>(grid, "CountdownText");
        if (countdown is not null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            countdown.Text = Format.ResetCountdown(gauge.ResetsAt, now);
        }
    }

    private void RefreshCountdowns()
    {
        if (_vm is null) return;
        for (var i = 0; i < _vm.Gauges.Count; i++)
        {
            var container = GaugeRepeater.TryGetElement(i);
            if (container is Border panel && panel.Child is Grid grid)
                RefreshCountdown(grid, _vm.Gauges[i]);
        }
    }

    // -------------------------------------------------------------------------
    // Header binding (IsConnected, Health)
    // -------------------------------------------------------------------------

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UsageViewModel.IsConnected)
                           or nameof(UsageViewModel.Health)
                           or nameof(UsageViewModel.IsDemoMode))
        {
            UpdateHeader();
        }
    }

    private void UpdateHeader()
    {
        if (_vm is null) return;

        // The header title is a static app name (see DashboardPage.xaml); the
        // subscription type is deliberately not surfaced.

        // Connection dot: bright green when live, grey when offline.
        ConnectionDot.Fill = _vm.IsConnected
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0x6F, 0xCF, 0x6F))  // #6FCF6F
            : new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x88, 0x88)); // #888888

        // Health banners.
        DegradedBanner.IsOpen = _vm.Health == "degraded";
        NeedsSignInBanner.IsOpen = _vm.Health == "needs-signin";

        // Demonstration-mode banner.
        DemoModeBanner.Visibility = _vm.IsDemoMode ? Visibility.Visible : Visibility.Collapsed;
    }

    // -------------------------------------------------------------------------
    // 1-second countdown timer
    // -------------------------------------------------------------------------

    private void StartCountdownTimer()
    {
        // Guard against double-start: if the timer is already running, do nothing.
        // This can happen when Loaded fires while the window-visibility handler has
        // already started the timer (e.g. window shown immediately after page load).
        if (_countdownTimer is { IsEnabled: true }) return;

        // Stop+null the previous instance to avoid adding duplicate Tick handlers.
        StopCountdownTimer();

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => RefreshCountdowns();
        _countdownTimer.Start();
    }

    private void StopCountdownTimer()
    {
        _countdownTimer?.Stop();
        _countdownTimer = null;
    }

    // -------------------------------------------------------------------------
    // Utilities
    // -------------------------------------------------------------------------

    /// <summary>
    /// Depth-first search of the visual subtree for a named element of type T.
    /// </summary>
    private static T? FindNamedChild<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T fe && fe.Name == name) return fe;
            var found = FindNamedChild<T>(child, name);
            if (found is not null) return found;
        }
        return null;
    }

}
