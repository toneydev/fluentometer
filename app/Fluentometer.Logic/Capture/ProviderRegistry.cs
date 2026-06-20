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

    // Provider IDs detected during the last BuildProvidersAsync run (regardless of
    // enabled state). Surfaced so the Settings page can render a per-provider
    // detected / "not detected" state without re-reading credentials from the UI layer.
    private readonly HashSet<string> _detectedProviderIds =
        new(StringComparer.OrdinalIgnoreCase);

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
    /// The set of provider IDs that detection reported as
    /// <see cref="ProviderDetectionStatus.Detected"/> during the last
    /// <see cref="BuildProvidersAsync"/> run — independent of whether each is enabled.
    /// </summary>
    public IReadOnlySet<string> DetectedProviderIds => _detectedProviderIds;

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

            _detectedProviderIds.Add(detector.ProviderId);

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
            "gemini" => BuildProviderWithReparseGuard(_geminiOauthCredsPath, _geminiProviderFactory),
            "chatgpt" => BuildProviderWithReparseGuard(_codexAuthPath, _chatGptProviderFactory),
            _ => null,
        };
    }

    /// <summary>
    /// Applies a P1-B / G-6 defense-in-depth reparse-point guard before invoking a
    /// provider factory that reads a credential file at <paramref name="credPath"/>.
    ///
    /// <para>
    /// Detection ran earlier but time may have passed; this single
    /// <see cref="File.GetAttributes"/> call collapses the existence check and the
    /// reparse-point check atomically to prevent a TOCTOU race (symlink swap after
    /// detection). If the file is absent we still invoke the factory — the credential
    /// reader inside the provider will return <c>NotFound</c> and the provider will
    /// emit a <c>needs-signin</c> health value, which is the honest "not signed in"
    /// state. If the file is a reparse point the provider is skipped entirely.
    /// </para>
    /// </summary>
    /// <param name="credPath">Absolute path to the credential file to guard.</param>
    /// <param name="factory">
    /// Factory to invoke when the file is absent or is a regular file (not a reparse
    /// point). Pass <c>null</c> to skip the provider unconditionally.
    /// </param>
    /// <returns>
    /// The constructed <see cref="IUsageProvider"/>, or <c>null</c> if the file is a
    /// reparse point or an unexpected error occurs.
    /// </returns>
    private static IUsageProvider? BuildProviderWithReparseGuard(
        string credPath,
        Func<IUsageProvider>? factory)
    {
        try
        {
            FileAttributes attrs;
            try
            {
                attrs = File.GetAttributes(credPath);
            }
            catch
            {
                // Credential file absent — let the factory build; the credential reader
                // inside the provider will return NotFound → provider emits needs-signin.
                return factory?.Invoke();
            }

            if (attrs.HasFlag(FileAttributes.ReparsePoint))
                return null; // symlink/junction appeared after detection — skip provider

            return factory?.Invoke();
        }
        catch
        {
            // Any other unexpected error — skip provider gracefully.
            return null;
        }
    }
}
