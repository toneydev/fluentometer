using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Settings;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Builds the live list of <see cref="IUsageProvider"/> instances by running every
/// registered <see cref="IProviderDetector"/> and filtering to those that are both
/// <see cref="ProviderDetectionStatus.Detected"/> AND enabled in the
/// <see cref="IProviderStore"/>.
///
/// <para>
/// <b>Usage in App.xaml.cs:</b>
/// <code>
/// var registry = new ProviderRegistry(
///     new FileProviderStore(),
///     MakeClaudeProvider,
///     MakeChatGptProvider,
///     MakeGeminiProvider,
///     new ClaudeProviderDetector(),
///     new ChatGptProviderDetector(),
///     new GeminiProviderDetector());
/// var providers = await registry.BuildProvidersAsync(ct);
/// IUsageClient client = new LiveUsageClient(providers, new SnapshotCache());
/// </code>
/// </para>
///
/// <para>
/// The registry has no opinions about the order of providers — the caller receives
/// them in the order detectors are passed to the constructor.
/// </para>
/// </summary>
public sealed class ProviderRegistry
{
    private readonly IProviderStore _store;
    private readonly IReadOnlyList<IProviderDetector> _detectors;

    // Dependencies needed to construct concrete providers after detection.
    private readonly Func<IUsageProvider>? _claudeProviderFactory;
    private readonly Func<IUsageProvider>? _chatGptProviderFactory;
    private readonly Func<IUsageProvider>? _geminiProviderFactory;
    private readonly string _geminiOauthCredsPath;
    private readonly string _codexAuthPath;

    /// <summary>
    /// Creates a <see cref="ProviderRegistry"/> with the default provider factories.
    /// </summary>
    /// <param name="store">Provider enable/disable store.</param>
    /// <param name="claudeProviderFactory">
    /// Factory that returns a ready-to-use <see cref="ClaudeProvider"/> (captures
    /// the HttpClient + credentials that App.xaml.cs constructs).
    /// </param>
    /// <param name="detectors">All provider detectors, in priority order.</param>
    public ProviderRegistry(
        IProviderStore store,
        Func<IUsageProvider> claudeProviderFactory,
        params IProviderDetector[] detectors)
        : this(store, claudeProviderFactory, null, null, detectors)
    {
    }

    /// <summary>
    /// Creates a <see cref="ProviderRegistry"/> with Claude and ChatGPT provider factories.
    /// </summary>
    /// <param name="store">Provider enable/disable store.</param>
    /// <param name="claudeProviderFactory">
    /// Factory that returns a ready-to-use <see cref="ClaudeProvider"/>.
    /// </param>
    /// <param name="chatGptProviderFactory">
    /// Optional factory that returns a ready-to-use <see cref="ChatGptProvider"/>.
    /// Pass <c>null</c> to skip the ChatGPT provider entirely.
    /// </param>
    /// <param name="detectors">All provider detectors, in priority order.</param>
    public ProviderRegistry(
        IProviderStore store,
        Func<IUsageProvider> claudeProviderFactory,
        Func<IUsageProvider>? chatGptProviderFactory,
        params IProviderDetector[] detectors)
        : this(store, claudeProviderFactory, chatGptProviderFactory, null, detectors)
    {
    }

    /// <summary>
    /// Creates a <see cref="ProviderRegistry"/> with Claude, ChatGPT, and Gemini provider factories.
    /// </summary>
    /// <param name="store">Provider enable/disable store.</param>
    /// <param name="claudeProviderFactory">
    /// Factory that returns a ready-to-use <see cref="ClaudeProvider"/>.
    /// </param>
    /// <param name="chatGptProviderFactory">
    /// Optional factory that returns a ready-to-use <see cref="ChatGptProvider"/>.
    /// Pass <c>null</c> to skip the ChatGPT provider entirely.
    /// </param>
    /// <param name="geminiProviderFactory">
    /// Optional factory that returns a ready-to-use <see cref="GeminiProvider"/>.
    /// Pass <c>null</c> to skip the Gemini provider entirely.
    /// </param>
    /// <param name="detectors">All provider detectors, in priority order.</param>
    public ProviderRegistry(
        IProviderStore store,
        Func<IUsageProvider> claudeProviderFactory,
        Func<IUsageProvider>? chatGptProviderFactory,
        Func<IUsageProvider>? geminiProviderFactory,
        params IProviderDetector[] detectors)
    {
        _store = store;
        _claudeProviderFactory = claudeProviderFactory;
        _chatGptProviderFactory = chatGptProviderFactory;
        _geminiProviderFactory = geminiProviderFactory;
        _detectors = detectors;
        _geminiOauthCredsPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini",
            "oauth_creds.json");
        _codexAuthPath = ResolveCodexAuthPath();
    }

    /// <summary>
    /// Resolves the Codex auth.json path by delegating to the shared
    /// <see cref="CodexPaths.ResolveAuthPath"/> resolver (honors <c>CODEX_HOME</c>).
    /// The env-var value is never logged.
    /// </summary>
    private static string ResolveCodexAuthPath() => CodexPaths.ResolveAuthPath();

    /// <summary>
    /// Runs all detectors, filters to detected + enabled, and returns the provider list.
    /// Returns an empty list (not null) if nothing is detected/enabled.
    /// Never throws — detection errors are swallowed per detector (G-11).
    /// </summary>
    public async Task<IReadOnlyList<IUsageProvider>> BuildProvidersAsync(
        CancellationToken ct = default)
    {
        var providers = new List<IUsageProvider>();

        foreach (var detector in _detectors)
        {
            if (ct.IsCancellationRequested) break;

            ProviderDetectionResult result;
            try
            {
                result = await detector.DetectAsync(ct);
            }
            catch
            {
                // G-11: detection must not throw; swallow and continue.
                continue;
            }

            if (result.Status != ProviderDetectionStatus.Detected)
                continue;

            if (!_store.IsEnabled(detector.ProviderId))
                continue;

            var provider = BuildProvider(detector.ProviderId);
            if (provider is not null)
                providers.Add(provider);
        }

        return providers;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private IUsageProvider? BuildProvider(string providerId)
    {
        return providerId switch
        {
            "claude" => _claudeProviderFactory?.Invoke(),
            "gemini" => BuildGeminiProvider(),
            "chatgpt" => BuildChatGptProvider(),
            _ => null,
        };
    }

    private IUsageProvider? BuildGeminiProvider()
    {
        // Defense-in-depth (P1-B / G-6): detection probed settings.json; the provider reads
        // oauth_creds.json. Repeat the reparse-point guard here on the credential file before
        // the factory's reader touches it, mirroring BuildChatGptProvider.
        try
        {
            FileAttributes attrs;
            try
            {
                attrs = File.GetAttributes(_geminiOauthCredsPath);
            }
            catch
            {
                // oauth_creds.json absent — let the factory build; the reader returns NotFound
                // → provider emits needs-signin (honest "not signed in" state).
                return _geminiProviderFactory?.Invoke();
            }

            if (attrs.HasFlag(FileAttributes.ReparsePoint))
                return null; // symlink/junction → skip provider

            return _geminiProviderFactory?.Invoke();
        }
        catch
        {
            return null;
        }
    }

    private IUsageProvider? BuildChatGptProvider()
    {
        // Re-read the Codex auth file path (G-2: structural presence only).
        // Detection verified the file was not a reparse point, but time has passed —
        // repeat the check here as defense-in-depth against a race where a symlink
        // is swapped in after detection (P1-B, mirrors BuildGeminiProvider's pattern).
        try
        {
            // P1-B (defense-in-depth): single GetAttributes call collapses existence
            // check and reparse-point check to avoid a TOCTOU race (G-6).
            FileAttributes attrs;
            try
            {
                attrs = File.GetAttributes(_codexAuthPath);
            }
            catch
            {
                // File disappeared since detection — provider will emit needs-signin health.
                // Fall through to factory invocation (factory handles the missing file case
                // via CodexCredentialReader.Read() → NotFound).
                return _chatGptProviderFactory?.Invoke();
            }

            if (attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                // Symlink/junction appeared after detection — skip provider.
                return null;
            }

            return _chatGptProviderFactory?.Invoke();
        }
        catch
        {
            // Any other unexpected error — skip provider gracefully.
            return null;
        }
    }
}
