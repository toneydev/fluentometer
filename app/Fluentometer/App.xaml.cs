using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Capture;
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.Settings;
using Fluentometer.Logic.Store;
using Fluentometer.Logic.Theming;
using Fluentometer.Logic.ViewModels;
using Fluentometer.Settings;
using Fluentometer.Ui;
using Fluentometer.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Fluentometer;

/// <summary>
/// Composition root for Fluentometer.  Wires DI, starts the in-process
/// <see cref="LiveUsageClient"/> on a background thread, and initialises the tray icon.
/// </summary>
public partial class App : Application
{
    // -------------------------------------------------------------------------
    // DI container + long-lived services
    // -------------------------------------------------------------------------
    private IServiceProvider? _services;
    private TrayIconManager? _tray;
    private MainWindow? _window;
    private CancellationTokenSource? _cts;

    public App()
    {
        InitializeComponent();

        // ------------------------------------------------------------------
        // Crash diagnostics: log any unhandled exception to a file so resize/
        // layout crashes (which surface as a stowed exception and otherwise
        // leave no managed stack) can be diagnosed post-mortem.
        // FLUENTOMETER_DIAG=1 keeps the process alive after logging so a
        // reproduction harness can capture the failing condition.
        // ------------------------------------------------------------------
        UnhandledException += (_, e) =>
        {
            LogCrash("XAML.UnhandledException", e.Exception);
            if (Environment.GetEnvironmentVariable("FLUENTOMETER_DIAG") == "1")
                e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Fluentometer");
            System.IO.Directory.CreateDirectory(dir);
            var line = $"[{DateTimeOffset.Now:o}] {source}\n{ex}\n\n";
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"), line);
        }
        catch { /* diagnostics must never throw */ }
    }

    // -------------------------------------------------------------------------
    // Launch
    // -------------------------------------------------------------------------
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _cts = new CancellationTokenSource();

        // ------------------------------------------------------------------
        // 1. Create window first (so we can capture its DispatcherQueue for
        //    WinUiDispatcher before building the DI container).
        // ------------------------------------------------------------------
        _window = new MainWindow();

        // ------------------------------------------------------------------
        // 2. Build the DI container.
        //    WinUiDispatcher wraps the window's DispatcherQueue.  Because the
        //    window is already created we can capture the queue now.
        // ------------------------------------------------------------------
        var queue = _window.DispatcherQueue;
        var dispatcher = new WinUiDispatcher(queue);
        var themeStore = new FileThemeStore();
        var themeService = new ThemeService(themeStore);
        themeService.Initialize();

        var densityService = new Fluentometer.Logic.Density.DensityService(themeStore);
        densityService.Initialize();

        // ------------------------------------------------------------------
        // Build in-process client (replaces sidecar + named pipe):
        //   HttpClient Timeout = 30 s — OauthUsageClient relies on this.
        //   TLS is always verified — no custom ServerCertificateCustomValidationCallback.
        //
        // ProviderRegistry runs detectors → builds live IReadOnlyList<IUsageProvider>.
        // Phase 1: Claude-only. Phase 2 adds Gemini alongside Claude.
        // ------------------------------------------------------------------
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // ClaudeProvider factory: captured here so ProviderRegistry can construct it
        // on demand after detection, with the shared HttpClient already configured.
        IUsageProvider MakeClaudeProvider() => new ClaudeProvider(
            "https://api.anthropic.com",
            new ClaudeCredentialReader(),
            new OauthUsageClient(http),
            new JsonlReader());

        // ChatGptProvider factory: shares the same HttpClient instance as ClaudeProvider.
        IUsageProvider MakeChatGptProvider() => new ChatGptProvider(
            new CodexCredentialReader(),
            new WhamUsageClient(http));

        // GeminiProvider factory: server-truth via the Code Assist backend; shares the HttpClient.
        IUsageProvider MakeGeminiProvider() => new GeminiProvider(
            new GeminiCredentialReader(),
            new CloudCodeUsageClient(http));

        var providerStore = new FileProviderStore();
        var registry = new ProviderRegistry(
            providerStore,
            MakeClaudeProvider,
            MakeChatGptProvider,
            MakeGeminiProvider,
            new ClaudeProviderDetector(),
            new ChatGptProviderDetector(),
            new GeminiProviderDetector());

        // BuildProvidersAsync runs detectors synchronously (tiny file reads) off the
        // UI thread via Task.Run in step 4 below. Block here is acceptable: we need
        // the provider list before creating LiveUsageClient for DI registration.
        // Detection is bounded (G-10) and fast (~1 ms).
        IReadOnlyList<IUsageProvider> providers =
            registry.BuildProvidersAsync().GetAwaiter().GetResult();

        // Fallback: if no providers detected (e.g. fresh machine), use Claude provider
        // anyway — it will emit needs-signin health until the user signs in.
        if (providers.Count == 0)
            providers = [MakeClaudeProvider()];

        IUsageClient client = new LiveUsageClient(providers, new SnapshotCache());

        _services = new ServiceCollection()
            .AddSingleton<IUsageClient>(_ => client)
            .AddSingleton<IThemeStore>(_ => themeStore)
            .AddSingleton<ThemeService>(_ => themeService)
            .AddSingleton<UsageViewModel>(_ => new UsageViewModel(client, dispatcher))
            .BuildServiceProvider();

        // ------------------------------------------------------------------
        // 3. Inject view model + theme into the dashboard page, then wire
        //    the settings-page dependencies so the gear button can navigate.
        // ------------------------------------------------------------------
        var vm = _services.GetRequiredService<UsageViewModel>();
        var launchOnLogin = new RegistryLaunchOnLogin();

        // Demo mode (session-only): a DispatcherTimer-driven controller that feeds
        // synthetic snapshots into the same VM the live client drives. Constructed on
        // the UI thread (OnLaunched) because DispatcherTimer requires it.
        var demoDriver = new Fluentometer.Logic.Demo.DemoDriver(
            vm, client, () => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var demoController = new DemoModeController(demoDriver);

        // Navigate the root frame to the dashboard and inject all dependencies from the
        // composition root.  providerStore was constructed above for ProviderRegistry; it
        // is threaded to the Settings page so the "Monitored services" section can read/write it.
        _window.RootFrame.Navigate(typeof(DashboardPage));
        if (_window.RootFrame.Content is DashboardPage page)
        {
            page.SetViewModel(vm, themeService);
            page.SetSettingsDependencies(client, themeStore, launchOnLogin, demoController, providerStore, registry.DetectedProviderIds, densityService);
            page.SetWindow(_window);
        }

        // ------------------------------------------------------------------
        // 4. Start the in-process client on a background task.
        // ------------------------------------------------------------------
        _ = Task.Run(() => client.StartAsync(_cts.Token), _cts.Token);

        // ------------------------------------------------------------------
        // 5. Wire the tray icon (show/hide on close, context menu).
        // ------------------------------------------------------------------
        _tray = new TrayIconManager(_window, vm, () => Quit());

        // ------------------------------------------------------------------
        // 6. Show the window.
        // ------------------------------------------------------------------
        _window.Activate();
    }

    // -------------------------------------------------------------------------
    // Quit — called by the tray "Quit" menu item.  Tears down everything.
    // -------------------------------------------------------------------------
    internal void Quit()
    {
        _cts?.Cancel();
        _tray?.Dispose();
        _window?.Close();
        // Allow the window close to propagate before exiting.
        Environment.Exit(0);
    }
}
