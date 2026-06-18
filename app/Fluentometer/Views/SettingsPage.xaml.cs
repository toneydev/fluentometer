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
/// Settings page: theme gallery, launch-on-login toggle, poll-interval slider.
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
    private AppSettings? _settings;

    // The grid that holds theme swatches, built in BuildThemeGallery().
    private Grid? _swatchGrid;

    // Guard against re-entrant value-changed handlers during setup.
    private bool _initializing;

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
        DemoModeController demoController)
    {
        _themeService = themeService;
        _client = client;
        _fileThemeStore = fileThemeStore;
        _launchOnLogin = launchOnLogin;
        _demoController = demoController;

        _settings = _fileThemeStore.LoadAppSettings();

        _initializing = true;
        try
        {
            BuildThemeGallery();
            SetupGradientDirection();
            SetupLaunchOnLogin();
            SetupPollInterval();
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

        // Populate the named host Grid directly. Because ThemeGalleryHost is a real
        // element from InitializeComponent (not resolved via .Parent), this works even
        // when Initialize runs before the page is loaded into the visual tree.
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

        // Horizontal gradient bar — a WYSIWYG preview of the dashboard bar, ordered by the
        // current fill direction so the swatch matches what the dashboard renders.
        var barBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
        };
        foreach (var (color, offset) in GradientStops.OrderedStops(theme.BarStops, _themeService.Direction))
        {
            barBrush.GradientStops.Add(new GradientStop { Color = ColorParser.Parse(color), Offset = offset });
        }

        var bar = new Border
        {
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = barBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };

        // Theme name in the default (theme-aware) text colour — the card is neutral.
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

        // Neutral mini-card. Uses semi-transparent greys (theme-agnostic, legible on
        // both light and dark) rather than resolving Card* ThemeResources in code.
        var inner = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            Child = content,
        };

        // Outer ring: accent border when selected, transparent otherwise.
        var ring = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(isSelected ? accentColor : Colors.Transparent),
            Child = inner,
            Tag = theme.Id,   // RefreshSelectionRings locates this element by id
        };

        ring.PointerPressed += (_, _) => SelectTheme(theme);

        return ring;
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
        // index 0 = BrightToDeep, index 1 = DeepToBright
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
        BuildThemeGallery(); // re-render swatches so the preview matches the new direction
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
        _settings.PollIntervalSeconds = seconds; // clamp already enforced by setter
        UpdatePollLabel(_settings.PollIntervalSeconds);
        _fileThemeStore.SaveAppSettings(_settings);

        // Send the new interval to the capture engine — fire-and-forget.  No await on
        // the UI thread; SendAsync returns immediately once the command is queued.
        _ = _client.SendAsync(ClientCommand.SetPollInterval(_settings.PollIntervalSeconds));
    }

    private void UpdatePollLabel(int seconds)
    {
        var minutes = seconds / 60;
        PollIntervalLabel.Text = minutes >= 1
            ? $"{minutes} min"
            : $"{seconds} s";
    }

    // -------------------------------------------------------------------------
    // Demonstration mode
    // -------------------------------------------------------------------------

    private void SetupDemoMode()
    {
        if (_demoController is null) return;
        DemoModeToggle.IsOn = _demoController.IsEnabled; // reflect current state on return
        DemoModeToggle.Toggled += OnDemoModeToggled;
    }

    private void OnDemoModeToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _demoController is null) return;
        if (DemoModeToggle.IsOn) _demoController.Enable();
        else _demoController.Disable();
    }

}
