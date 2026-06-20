using System;
using System.Collections.Generic;
using Fluentometer; // DemoModeController
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.Settings;
using Fluentometer.Logic.Theming;
using Fluentometer.Ui;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Fluentometer.Views;

/// <summary>
/// Settings page: theme gallery, launch-on-login toggle, poll-interval slider,
/// monitored services with per-provider toggles, and demonstration mode.
///
/// Dependencies are injected via Initialize() so the page works with the
/// DI composition root in App.xaml.cs without any App.Current coupling.
/// </summary>
public sealed partial class SettingsPage : Page
{
    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private ThemeService? _themeService;
    private IUsageClient? _client;
    private FileThemeStore? _fileThemeStore;
    private ILaunchOnLogin? _launchOnLogin;
    private DemoModeController? _demoController;
    private IProviderStore? _providerStore;
    private AppSettings? _settings;

    // The grid that holds theme swatches, built in BuildThemeGallery().
    private Grid? _swatchGrid;

    // Guard against re-entrant value-changed handlers during setup.
    private bool _initializing;

    // Provider IDs currently listed in the settings page (for the toggle subscriptions).
    private readonly List<(string ProviderId, ToggleSwitch Toggle)> _providerToggles = new();

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------
    public SettingsPage()
    {
        InitializeComponent();
        BackButton.Click += (_, _) => Frame.GoBack();
    }

    // -------------------------------------------------------------------------
    // Injection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Wires all dependencies.  Call immediately after Frame.Navigate().
    /// </summary>
    public void Initialize(
        ThemeService themeService,
        IUsageClient client,
        FileThemeStore fileThemeStore,
        ILaunchOnLogin launchOnLogin,
        DemoModeController demoController,
        IProviderStore providerStore)
    {
        _themeService = themeService;
        _client = client;
        _fileThemeStore = fileThemeStore;
        _launchOnLogin = launchOnLogin;
        _demoController = demoController;
        _providerStore = providerStore;

        _settings = _fileThemeStore.LoadAppSettings();

        _initializing = true;
        try
        {
            BuildThemeGallery();
            SetupGradientDirection();
            SetupLaunchOnLogin();
            SetupPollInterval();
            SetupMonitoredServices();
            SetupDemoMode();
        }
        finally
        {
            _initializing = false;
        }
    }

    // -------------------------------------------------------------------------
    // Theme gallery
    // -------------------------------------------------------------------------

    private void BuildThemeGallery()
    {
        if (_themeService is null) return;

        var items = ThemeCatalog.All;
        const int cols = 4;

        var grid = ThemeGalleryHost;
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();

        for (var c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rows = (items.Count + cols - 1) / cols;
        for (var r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });

        for (var i = 0; i < items.Count; i++)
        {
            var swatch = BuildSwatch(items[i]);
            Grid.SetRow(swatch, i / cols);
            Grid.SetColumn(swatch, i % cols);
            swatch.Margin = new Thickness(4);
            grid.Children.Add(swatch);
        }

        _swatchGrid = grid;
    }

    private Border BuildSwatch(GradientTheme theme)
    {
        if (_themeService is null) return new Border();

        var accentColor = ColorParser.Parse(theme.Accent);
        var isSelected = theme.Id == _themeService.Current.Id;

        FrameworkElement bar;
        if (theme.Id == ThemeCatalog.BrandId)
        {
            bar = BuildBrandSegmentBar();
        }
        else
        {
            var barBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0),
            };
            foreach (var (color, offset) in GradientStops.OrderedStops(theme.BarStops, _themeService.Direction))
            {
                barBrush.GradientStops.Add(new GradientStop { Color = ColorParser.Parse(color), Offset = offset });
            }

            bar = new Border
            {
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = barBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
            };
        }

        var label = new TextBlock
        {
            Text = theme.Name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 8, 6),
        };

        var content = new Grid { RowSpacing = 6, Padding = new Thickness(0, 8, 0, 0) };
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(bar, 0);
        Grid.SetRow(label, 1);
        content.Children.Add(bar);
        content.Children.Add(label);

        var inner = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            Child = content,
        };

        var ring = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(isSelected ? accentColor : Colors.Transparent),
            Child = inner,
            Tag = theme.Id,
        };

        ring.PointerPressed += (_, _) => SelectTheme(theme);

        return ring;
    }

    /// <summary>
    /// Builds the brand-theme swatch bar: three side-by-side rounded segments using each
    /// supported provider's primary brand color (Claude, ChatGPT, Gemini). Honestly shows
    /// "each product gets its own color" rather than one blended gradient.
    /// </summary>
    private static Border BuildBrandSegmentBar()
    {
        var grid = new Grid { Height = 10, Margin = new Thickness(8, 0, 8, 0) };
        string[] providerIds = ["claude", "chatgpt", "gemini"];
        for (var i = 0; i < providerIds.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < providerIds.Length; i++)
        {
            var primary = BrandPalette.For(providerIds[i]).Accent; // primary = glow accent
            var seg = new Border { Background = new SolidColorBrush(ColorParser.Parse(primary)) };
            Grid.SetColumn(seg, i);
            grid.Children.Add(seg);
        }

        // Wrap so the row of segments has a single rounded outline (clip children to corners).
        return new Border
        {
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = grid,
        };
    }

    private void SelectTheme(GradientTheme theme)
    {
        if (_themeService is null || _settings is null || _fileThemeStore is null) return;

        _themeService.Apply(theme.Id);
        _settings.ThemeId = theme.Id;
        _fileThemeStore.SaveAppSettings(_settings);

        RefreshSelectionRings();
    }

    private void RefreshSelectionRings()
    {
        if (_themeService is null || _swatchGrid is null) return;

        var currentId = _themeService.Current.Id;

        foreach (var child in _swatchGrid.Children)
        {
            if (child is not Border ring || ring.Tag is not string id) continue;

            var theme = ThemeCatalog.ById(id);
            if (theme is null) continue;

            var accentColor = ColorParser.Parse(theme.Accent);
            ring.BorderBrush = new SolidColorBrush(
                id == currentId ? accentColor : Colors.Transparent);
        }
    }

    // -------------------------------------------------------------------------
    // Gradient direction
    // -------------------------------------------------------------------------

    private void SetupGradientDirection()
    {
        if (_themeService is null) return;
        GradientDirectionRadios.SelectedIndex =
            _themeService.Direction == GradientDirection.DeepToBright ? 1 : 0;
        GradientDirectionRadios.SelectionChanged += OnGradientDirectionChanged;
    }

    private void OnGradientDirectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _themeService is null) return;
        var dir = GradientDirectionRadios.SelectedIndex == 1
            ? GradientDirection.DeepToBright
            : GradientDirection.BrightToDeep;
        _themeService.ApplyDirection(dir);
        BuildThemeGallery();
    }

    // -------------------------------------------------------------------------
    // Launch on login
    // -------------------------------------------------------------------------

    private void SetupLaunchOnLogin()
    {
        if (_launchOnLogin is null) return;
        LaunchOnLoginToggle.IsOn = _launchOnLogin.IsEnabled;
        LaunchOnLoginToggle.Toggled += OnLaunchOnLoginToggled;
    }

    private void OnLaunchOnLoginToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _launchOnLogin is null) return;
        _launchOnLogin.Set(LaunchOnLoginToggle.IsOn);
    }

    // -------------------------------------------------------------------------
    // Poll interval
    // -------------------------------------------------------------------------

    private void SetupPollInterval()
    {
        if (_settings is null) return;
        PollIntervalSlider.Value = _settings.PollIntervalSeconds;
        UpdatePollLabel(_settings.PollIntervalSeconds);
        PollIntervalSlider.ValueChanged += OnPollIntervalChanged;
    }

    private void OnPollIntervalChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_initializing || _settings is null || _client is null || _fileThemeStore is null) return;

        var seconds = (int)PollIntervalSlider.Value;
        _settings.PollIntervalSeconds = seconds;
        UpdatePollLabel(_settings.PollIntervalSeconds);
        _fileThemeStore.SaveAppSettings(_settings);

        _ = _client.SendAsync(ClientCommand.SetPollInterval(_settings.PollIntervalSeconds));
    }

    private void UpdatePollLabel(int seconds)
    {
        var minutes = seconds / 60;
        PollIntervalLabel.Text = minutes >= 1 ? $"{minutes} min" : $"{seconds} s";
    }

    // -------------------------------------------------------------------------
    // Monitored services
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the "Monitored services" section. Each detected provider gets a row with a
    /// ToggleSwitch (right-aligned via HorizontalAlignment=Stretch) plus a subtle source
    /// hint for non-Claude providers ("Gemini — local estimate").
    ///
    /// The provider list is the set that was detected at startup by ProviderRegistry.
    /// Toggle changes take effect on restart (LiveUsageClient is not hot-swapped), so a
    /// quiet restart caption is shown when extra providers are present.
    ///
    /// When no extra providers beyond Claude are detected (the v1 steady state) a warm
    /// "we'll show them here" message is shown instead of an empty list.
    ///
    /// Newly detected non-Claude providers get a TeachingTip introduction, then MarkSeen
    /// is called to prevent re-showing.
    /// </summary>
    private void SetupMonitoredServices()
    {
        if (_providerStore is null) return;

        MonitoredServicesHost.Children.Clear();
        _providerToggles.Clear();

        // Collect the provider IDs that have been seen (i.e., detected at least once).
        var seen = _providerStore.Seen();
        var providerIds = new List<string>(seen);

        // Sort for stable display: Claude first, then others alphabetically.
        providerIds.Sort((a, b) =>
        {
            if (a == "claude") return -1;
            if (b == "claude") return 1;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

        // Track which newly-detected providers need a TeachingTip.
        var newlyDetected = new List<string>();

        foreach (var providerId in providerIds)
        {
            // Non-Claude providers detected in this session trigger the TeachingTip.
            // (Claude is always expected; other providers are new discoveries.)
            if (providerId != "claude")
                newlyDetected.Add(providerId);

            var row = BuildProviderRow(providerId);
            MonitoredServicesHost.Children.Add(row);
        }

        // Nothing-detected friendly state: if no extra providers beyond Claude detected,
        // show a warm informational message. Opacity 0.55 keeps it quiet — it's ambient
        // info, not an error or a call to action.
        if (providerIds.Count <= 1) // only Claude or nothing at all
        {
            var friendlyMsg = new TextBlock
            {
                Text = "Other services will appear here automatically once Fluentometer detects them.",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap,
            };
            MonitoredServicesHost.Children.Add(friendlyMsg);
        }
        else
        {
            // Extra providers are listed — show a quiet restart note so users aren't
            // surprised when a toggle doesn't take effect immediately.
            var restartNote = new TextBlock
            {
                Text = "Toggle changes take effect when you restart Fluentometer.",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Opacity = 0.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            };
            MonitoredServicesHost.Children.Add(restartNote);
        }

        // Show TeachingTip for newly detected non-Claude providers (first-detection).
        // Anchor it to the section header so the tip points at the relevant section.
        // MarkSeen is called inside ShowNewProviderTeachingTip to prevent re-showing.
        NewProviderTeachingTip.Target = MonitoredServicesHeader;
        if (newlyDetected.Count > 0)
        {
            ShowNewProviderTeachingTip(newlyDetected);
        }
    }

    /// <summary>
    /// Builds a provider row: a Grid with a ToggleSwitch (right-aligned) plus a
    /// subtle source/estimate hint for non-Claude providers.
    ///
    /// Layout: single-column Grid (full width).
    ///   Row 0: ToggleSwitch (HorizontalAlignment=Stretch puts the thumb on the right).
    ///   Row 1: subtle hint caption ("local estimate") for non-Claude providers only.
    /// </summary>
    private Grid BuildProviderRow(string providerId)
    {
        var displayName = providerId.Length > 0
            ? char.ToUpperInvariant(providerId[0]) + providerId[1..]
            : providerId;

        var toggle = new ToggleSwitch
        {
            Header = displayName,
            IsOn = _providerStore?.IsEnabled(providerId) ?? true,
            OnContent = "On",
            OffContent = "Off",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        // AutomationProperties is a static attached property in WinUI 3;
        // it cannot be set via an object initializer.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, $"Monitor {displayName}");

        toggle.Toggled += (_, _) =>
        {
            if (_initializing || _providerStore is null) return;
            _providerStore.SetEnabled(providerId, toggle.IsOn);
        };

        _providerToggles.Add((providerId, toggle));

        // Wrap in a container to add the source hint for non-Claude providers.
        var container = new Grid { RowSpacing = 2 };
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (providerId != "claude")
        {
            // Add a subtle source hint row so users understand the data source.
            container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var hintText = providerId switch
            {
                "chatgpt" => $"{displayName} — server data · requires Codex CLI",
                "gemini" => $"{displayName} — server data · requires Gemini CLI",
                _ => $"{displayName} — local estimate (no API key required)",
            };
            var hint = new TextBlock
            {
                Text = hintText,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Opacity = 0.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 0),
            };
            Grid.SetRow(hint, 1);
            container.Children.Add(hint);
        }

        Grid.SetRow(toggle, 0);
        container.Children.Add(toggle);
        return container;
    }

    /// <summary>
    /// Shows the first-detection TeachingTip for newly detected providers and marks
    /// them all as seen so the tip doesn't appear again.
    /// </summary>
    private void ShowNewProviderTeachingTip(List<string> newProviderIds)
    {
        if (_providerStore is null || newProviderIds.Count == 0) return;

        // Build the tip title and subtitle.
        string title, subtitle;
        if (newProviderIds.Count == 1)
        {
            var name = newProviderIds[0].Length > 0
                ? char.ToUpperInvariant(newProviderIds[0][0]) + newProviderIds[0][1..]
                : newProviderIds[0];
            title = $"{name} detected";
            subtitle = $"Fluentometer found {name} on this machine and will monitor its usage. "
                     + "You can disable it here. Changes take effect on restart.";
        }
        else
        {
            var names = string.Join(", ", newProviderIds.ConvertAll(id =>
                id.Length > 0 ? char.ToUpperInvariant(id[0]) + id[1..] : id));
            title = "New services detected";
            subtitle = $"Fluentometer found {names} on this machine and will monitor their usage. "
                     + "You can disable them here. Changes take effect on restart.";
        }

        NewProviderTeachingTip.Title = title;
        NewProviderTeachingTip.Subtitle = subtitle;
        NewProviderTeachingTip.IsOpen = true;

        // Mark all as seen — idempotent, so calling on each open is safe.
        foreach (var id in newProviderIds)
            _providerStore.MarkSeen(id);
    }

    // -------------------------------------------------------------------------
    // Demonstration mode
    // -------------------------------------------------------------------------

    private void SetupDemoMode()
    {
        if (_demoController is null) return;
        DemoModeToggle.IsOn = _demoController.IsEnabled;
        DemoModeToggle.Toggled += OnDemoModeToggled;
    }

    private void OnDemoModeToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _demoController is null) return;
        if (DemoModeToggle.IsOn) _demoController.Enable();
        else _demoController.Disable();
    }
}
