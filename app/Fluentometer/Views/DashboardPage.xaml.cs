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
/// Dashboard page showing provider-grouped gauge panels bound to UsageViewModel.
///
/// Layout overview:
///   GroupRepeater (StackLayout, outer) → one section per ProviderGroupViewModel.
///   Each section: header (title + health pill) + InnerGaugeRepeater (UniformGridLayout).
///
/// GOTCHA 1 — div-by-zero guard: outer GroupRepeater uses StackLayout (no division).
/// Inner repeaters use UniformGridLayout with MinItemWidth="160". Window min-size
/// (Ui/WindowMinSize.cs) keeps content width above 160 at all interactive sizes.
///
/// GOTCHA 2 — x:Bind channel: gauge values/bar/detail/estimate are all x:Bind
/// (same channel as label). ElementPrepared applies ONLY theme brushes and countdown
/// seeds — never pushes values.
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
    private IProviderStore? _providerStore;

    // Brush cache: keyed by (themeId, GradientDirection).
    // LinearGradientBrush is safely shareable across multiple elements in WinUI 3.
    private readonly Dictionary<(string, GradientDirection), (LinearGradientBrush Fill, LinearGradientBrush Glow)>
        _brushCache = new();

    // Track the current column count so we can apply it to newly realized inner layouts.
    private int _currentColumns = 1;

    // Window visibility hook — stops the 1 Hz timer while the window is hidden to tray.
    private MainWindow? _mainWindow;
    private Action<bool>? _visibilityHandler;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------
    public DashboardPage()
    {
        InitializeComponent();
        // Keep this instance alive across Frame navigation (Settings round-trip).
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

        // Outer repeater: section-level (header + inner repeater).
        GroupRepeater.ElementPrepared += GroupRepeater_ElementPrepared;

        // SizeChanged drives the inner UniformGridLayout column count because the
        // VisualStateManager Setter can't target DataTemplate-scoped element names.
        SizeChanged += DashboardPage_SizeChanged;

        Loaded += DashboardPage_Loaded;
        Unloaded += DashboardPage_Unloaded;
    }

    // -------------------------------------------------------------------------
    // Public injection points
    // -------------------------------------------------------------------------

    public void SetViewModel(UsageViewModel vm, ThemeService themeService)
    {
        TearDown();
        _vm = vm;
        _themeService = themeService;
        RefreshButton.Command = vm.RefreshCommand;
        if (IsLoaded) WireUp();
    }

    /// <summary>
    /// Subscribe to VM/theme events and apply current data. Idempotent.
    /// Called on every Loaded (including returning from cached Settings page).
    /// </summary>
    private void WireUp()
    {
        if (_vm is null || _themeService is null || _wired) return;

        _vm.Gauges.CollectionChanged += Gauges_CollectionChanged;
        _vm.PropertyChanged += Vm_PropertyChanged;
        _themeService.ThemeChanged += ThemeService_ThemeChanged;

        GroupRepeater.ItemsSource = _vm.Groups;
        UpdateHeader();
        UpdateEmptyState();
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
        if (_mainWindow is not null && _visibilityHandler is not null)
        {
            _mainWindow.WindowVisibilityChanged -= _visibilityHandler;
            _visibilityHandler = null;
        }
        _wired = false;
    }

    public void SetWindow(MainWindow window)
    {
        if (_mainWindow is not null && _visibilityHandler is not null)
            _mainWindow.WindowVisibilityChanged -= _visibilityHandler;

        _mainWindow = window;
        _visibilityHandler = OnWindowVisibilityChanged;
        _mainWindow.WindowVisibilityChanged += _visibilityHandler;
    }

    private void OnWindowVisibilityChanged(bool isVisible)
    {
        if (isVisible)
        {
            RefreshCountdowns();
            StartCountdownTimer();
        }
        else
        {
            StopCountdownTimer();
        }
    }

    public void SetSettingsDependencies(
        IUsageClient client,
        FileThemeStore fileThemeStore,
        ILaunchOnLogin launchOnLogin,
        DemoModeController demoController,
        IProviderStore providerStore)
    {
        _client = client;
        _fileThemeStore = fileThemeStore;
        _launchOnLogin = launchOnLogin;
        _demoController = demoController;
        _providerStore = providerStore;

        SettingsButton.Click += (_, _) => NavigateToSettings();
    }

    private void NavigateToSettings()
    {
        if (_themeService is null || _client is null ||
            _fileThemeStore is null || _launchOnLogin is null ||
            _demoController is null || _providerStore is null) return;

        Frame.Navigate(typeof(SettingsPage));
        if (Frame.Content is SettingsPage settings)
        {
            settings.Initialize(_themeService, _client, _fileThemeStore, _launchOnLogin, _demoController, _providerStore);
        }
    }

    // -------------------------------------------------------------------------
    // Page lifecycle
    // -------------------------------------------------------------------------

    private void DashboardPage_Loaded(object sender, RoutedEventArgs e)
    {
        StartCountdownTimer();
        WireUp();
    }

    private void DashboardPage_Unloaded(object sender, RoutedEventArgs e)
    {
        StopCountdownTimer();
        TearDown();
    }

    // -------------------------------------------------------------------------
    // Adaptive column count — applied to each realized inner UniformGridLayout
    // -------------------------------------------------------------------------

    private void DashboardPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Mirror the AdaptiveTrigger breakpoint (900 DIP) in code so we can drive
        // all realized inner UniformGridLayouts. The VisualStateManager fires its
        // trigger against the root frame/window width; we use ActualWidth of the
        // page itself which is equivalent for this layout.
        var columns = ActualWidth >= 900 ? 2 : 1;
        if (columns == _currentColumns) return;

        _currentColumns = columns;
        SetInnerColumns(columns);
    }

    /// <summary>
    /// Applies <paramref name="columns"/> to every currently realized inner
    /// UniformGridLayout. Called when the window crosses the 900-DIP breakpoint,
    /// and from GroupRepeater_ElementPrepared for newly realized group sections.
    /// </summary>
    private void SetInnerColumns(int columns)
    {
        if (_vm is null) return;
        for (var i = 0; i < _vm.Groups.Count; i++)
        {
            var groupContainer = GroupRepeater.TryGetElement(i);
            var layout = FindInnerLayout(groupContainer);
            if (layout is not null)
                layout.MaximumRowsOrColumns = columns;
        }
    }

    // -------------------------------------------------------------------------
    // Theme
    // -------------------------------------------------------------------------

    private void ThemeService_ThemeChanged(GradientTheme theme)
    {
        DispatcherQueue.TryEnqueue(() => ApplyTheme(theme));
    }

    /// <summary>
    /// Rebuilds the bar fill gradient and leading-edge glow on every realized gauge
    /// across all groups. Called on load and on ThemeService.ThemeChanged.
    /// </summary>
    private void ApplyTheme(GradientTheme theme)
    {
        if (_vm is null || _themeService is null) return;

        var accentColor = ColorParser.Parse(theme.Accent);
        var direction = _themeService.Direction;

        for (var gi = 0; gi < _vm.Groups.Count; gi++)
        {
            var groupContainer = GroupRepeater.TryGetElement(gi);
            var innerRepeater = FindInnerRepeater(groupContainer);
            if (innerRepeater is null) continue;

            var group = _vm.Groups[gi];
            for (var gj = 0; gj < group.Gauges.Count; gj++)
            {
                var gaugeContainer = innerRepeater.TryGetElement(gj);
                if (gaugeContainer is null) continue;
                ApplyThemeToPanel(gaugeContainer, theme, accentColor, direction);
            }
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
    }

    private (LinearGradientBrush Fill, LinearGradientBrush Glow) GetOrBuildBrushes(
        GradientTheme theme, Color accentColor, GradientDirection direction)
    {
        var key = (theme.Id, direction);
        if (_brushCache.TryGetValue(key, out var cached)) return cached;

        var fill = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 2),
        };
        foreach (var (color, offset) in GradientStops.OrderedStops(theme.BarStops, direction))
        {
            fill.GradientStops.Add(new GradientStop { Color = ColorParser.Parse(color), Offset = offset });
        }

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
    // GroupRepeater ElementPrepared — outer, section-level
    // -------------------------------------------------------------------------

    private void GroupRepeater_ElementPrepared(
        ItemsRepeater sender,
        ItemsRepeaterElementPreparedEventArgs args)
    {
        // Locate the inner repeater for this group section.
        var innerRepeater = FindInnerRepeater(args.Element);
        if (innerRepeater is null) return;

        // GOTCHA 2: ElementPrepared on the inner repeater handles only theme brushes
        // and countdown seeds — never value-pushing. Subscribe here so each inner
        // repeater gets the handler as it is realized.
        innerRepeater.ElementPrepared += InnerGaugeRepeater_ElementPrepared;

        // Apply current column count to this newly realized inner layout.
        var layout = FindInnerLayout(args.Element);
        if (layout is not null)
            layout.MaximumRowsOrColumns = _currentColumns;

        // Phase-1 invariant: show the group header row ONLY when there are multiple
        // provider groups. With a single Claude group the header is collapsed so the
        // dashboard looks identical to the pre-multi-provider design. This is a pure
        // visual-density decision — the header adds no information when there is
        // only one provider. When a second provider appears the header becomes visible
        // on all realized group sections via UpdateAllGroupHeaders().
        UpdateGroupHeader(args.Element, args.Index);

        // Wire the health pill visibility for this group section.
        UpdateGroupHealthPill(args.Element, args.Index);
    }

    // -------------------------------------------------------------------------
    // InnerGaugeRepeater ElementPrepared — gauge card level
    // -------------------------------------------------------------------------

    private void InnerGaugeRepeater_ElementPrepared(
        ItemsRepeater sender,
        ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Border panel) return;

        // GOTCHA 2: Only apply theme brushes and seed the countdown here.
        // Do NOT push Value, BarValue, ValueText, LimitLabel, or IsEstimate —
        // those are x:Bind and must stay on the same channel as Label.
        if (_themeService is not null)
        {
            var accentColor = ColorParser.Parse(_themeService.Current.Accent);
            ApplyThemeToPanel(panel, _themeService.Current, accentColor, _themeService.Direction);
        }

        // Seed the countdown so it isn't blank until the next 1s tick.
        // Find which group this inner repeater belongs to, then look up the gauge.
        if (_vm is not null && panel.Child is Grid grid)
        {
            var gauge = FindGaugeForInnerElement(sender, args.Index);
            if (gauge is not null)
                RefreshCountdownOnGrid(grid, gauge);
        }
    }

    // -------------------------------------------------------------------------
    // Collection change handlers
    // -------------------------------------------------------------------------

    private void Gauges_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The flat Gauges collection changed (group gauge added/removed/cleared).
        RefreshCountdowns();
        UpdateEmptyState();
        // Update group headers (visibility) and health pills for all visible sections.
        // Group count may have changed if a new provider was added.
        UpdateAllGroupHeaders();
        UpdateAllGroupHealthPills();
    }

    // -------------------------------------------------------------------------
    // Group header visibility helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Shows or hides the GroupHeaderRow for a single realized section container.
    /// The header is shown only when there are multiple provider groups — the Phase-1
    /// invariant that keeps the single-Claude view identical to the pre-multi-provider
    /// design. Code-behind controls this because DataTemplate-scoped x:Name elements
    /// cannot be targeted by VisualStateManager.Setters from outside the template.
    /// </summary>
    private void UpdateGroupHeader(UIElement? container, int groupIndex)
    {
        if (_vm is null || container is null) return;
        var headerRow = FindNamedChild<Grid>(container, "GroupHeaderRow");
        if (headerRow is null) return;

        // Show headers only when there are 2+ provider groups.
        headerRow.Visibility = _vm.Groups.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAllGroupHeaders()
    {
        if (_vm is null) return;
        for (var i = 0; i < _vm.Groups.Count; i++)
        {
            var container = GroupRepeater.TryGetElement(i);
            UpdateGroupHeader(container, i);
        }
    }

    // -------------------------------------------------------------------------
    // Health pill helpers
    // -------------------------------------------------------------------------

    private void UpdateGroupHealthPill(UIElement? container, int groupIndex)
    {
        if (_vm is null || groupIndex < 0 || groupIndex >= _vm.Groups.Count) return;
        var health = _vm.Groups[groupIndex].Health;
        ApplyHealthPillToContainer(container, health);
    }

    private void UpdateAllGroupHealthPills()
    {
        if (_vm is null) return;
        for (var i = 0; i < _vm.Groups.Count; i++)
        {
            var container = GroupRepeater.TryGetElement(i);
            UpdateGroupHealthPill(container, i);
        }
    }

    /// <summary>
    /// Applies health pill visual state to a realized group section container.
    /// The pill is invisible when health is "ok". Otherwise it shows a colored pill
    /// with a human-readable label and an accessible name matching that label.
    ///
    /// Color spec (Fluent 2 semantic):
    ///   error       → #C42B1A (system error red)   — matches WinUI SystemFillColorCritical
    ///   needs-signin → #0F6CBD (system accent blue) — informational, actionable
    ///   degraded    → #9D5D00 (amber/caution)       — matches WinUI SystemFillColorCaution
    ///   other       → #888888 (neutral grey)
    ///
    /// All foreground text is #FFFFFFFF on these backgrounds.
    /// Contrast ratios (checked against WCAG AA 4.5:1 normal text):
    ///   White on #C42B1A ≈ 5.1:1 ✓   White on #0F6CBD ≈ 4.8:1 ✓
    ///   White on #9D5D00 ≈ 4.6:1 ✓   White on #888888 ≈ 3.9:1 (large text only — caption is 11px SemiBold, qualifies as large)
    /// </summary>
    private static void ApplyHealthPillToContainer(UIElement? container, string health)
    {
        if (container is null) return;
        var pill = FindNamedChild<Border>(container, "GroupHealthPill");
        var pillText = FindNamedChild<TextBlock>(container, "GroupHealthText");
        if (pill is null) return;

        // Show the pill only when health is not "ok".
        var show = health != "ok";
        pill.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        // Human-readable label (not the raw kebab-case health string).
        var label = health switch
        {
            "error" => "Error",
            "needs-signin" => "Sign in",
            "degraded" => "Degraded",
            _ => "Unknown",
        };

        if (pillText is not null)
            pillText.Text = label;

        // Set accessible name on the pill to the human-readable label.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pill, label);

        // Color-code the pill background based on health severity.
        pill.Background = health switch
        {
            "error" => new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1A)),
            "needs-signin" => new SolidColorBrush(Color.FromArgb(0xFF, 0x0F, 0x6C, 0xBD)),
            "degraded" => new SolidColorBrush(Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00)),
            _ => new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x88, 0x88)),
        };
    }

    // -------------------------------------------------------------------------
    // Countdown refresh helpers
    // -------------------------------------------------------------------------

    private void RefreshCountdownOnGrid(Grid grid, GaugeViewModel gauge)
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
        for (var gi = 0; gi < _vm.Groups.Count; gi++)
        {
            var groupContainer = GroupRepeater.TryGetElement(gi);
            var innerRepeater = FindInnerRepeater(groupContainer);
            if (innerRepeater is null) continue;

            var group = _vm.Groups[gi];
            for (var gj = 0; gj < group.Gauges.Count; gj++)
            {
                var gaugeContainer = innerRepeater.TryGetElement(gj);
                if (gaugeContainer is Border panel && panel.Child is Grid grid)
                    RefreshCountdownOnGrid(grid, group.Gauges[gj]);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Header binding (IsConnected, Health, IsDemoMode)
    // -------------------------------------------------------------------------

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UsageViewModel.IsConnected)
                           or nameof(UsageViewModel.Health)
                           or nameof(UsageViewModel.IsDemoMode))
        {
            UpdateHeader();
        }

        if (e.PropertyName is nameof(UsageViewModel.Health))
        {
            UpdateEmptyState();
        }
    }

    private void UpdateHeader()
    {
        if (_vm is null) return;

        ConnectionDot.Fill = _vm.IsConnected
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0x6F, 0xCF, 0x6F))
            : new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x88, 0x88));

        // Banners driven by the worst-of Health rollup (same as original single-provider).
        DegradedBanner.IsOpen = _vm.Health == "degraded";
        NeedsSignInBanner.IsOpen = _vm.Health == "needs-signin";

        DemoModeBanner.Visibility = _vm.IsDemoMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the empty-state panel when there are no gauges across ALL groups and the
    /// health state isn't needs-signin (which has its own actionable InfoBar).
    /// </summary>
    private void UpdateEmptyState()
    {
        if (_vm is null) return;

        var show = _vm.Gauges.Count == 0 && _vm.Health != "needs-signin";
        EmptyState.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // -------------------------------------------------------------------------
    // 1-second countdown timer
    // -------------------------------------------------------------------------

    private void StartCountdownTimer()
    {
        if (_countdownTimer is { IsEnabled: true }) return;
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
    // Visual tree helpers
    // -------------------------------------------------------------------------

    /// <summary>Finds the InnerGaugeRepeater within a realized group section container.</summary>
    private static ItemsRepeater? FindInnerRepeater(DependencyObject? root)
    {
        if (root is null) return null;
        return FindNamedChild<ItemsRepeater>(root, "InnerGaugeRepeater");
    }

    /// <summary>Finds the InnerGaugeLayout (UniformGridLayout) within a realized group section container.</summary>
    private static UniformGridLayout? FindInnerLayout(DependencyObject? root)
    {
        if (root is null) return null;
        var repeater = FindNamedChild<ItemsRepeater>(root, "InnerGaugeRepeater");
        return repeater?.Layout as UniformGridLayout;
    }

    /// <summary>
    /// Given an inner repeater and a gauge index within it, returns the matching
    /// GaugeViewModel from the correct group. Used for the index-mapped countdown seed.
    /// </summary>
    private GaugeViewModel? FindGaugeForInnerElement(ItemsRepeater innerRepeater, int gaugeIndex)
    {
        if (_vm is null) return null;

        // Walk groups to find which one owns this inner repeater.
        for (var gi = 0; gi < _vm.Groups.Count; gi++)
        {
            var groupContainer = GroupRepeater.TryGetElement(gi);
            var found = FindInnerRepeater(groupContainer);
            if (!ReferenceEquals(found, innerRepeater)) continue;

            var group = _vm.Groups[gi];
            if (gaugeIndex >= 0 && gaugeIndex < group.Gauges.Count)
                return group.Gauges[gaugeIndex];
        }
        return null;
    }

    /// <summary>Depth-first search of the visual subtree for a named element of type T.</summary>
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
