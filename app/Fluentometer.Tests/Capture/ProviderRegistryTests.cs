using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Capture;
using Fluentometer.Logic.Settings;
using Xunit;

namespace Fluentometer.Tests.Capture;

// ── Fakes ─────────────────────────────────────────────────────────────────────

internal sealed class FakeDetector : IProviderDetector
{
    private readonly ProviderDetectionResult _result;

    public FakeDetector(string providerId, ProviderDetectionStatus status,
        string? displayName = null)
    {
        ProviderId = providerId;
        _result = new ProviderDetectionResult(status, displayName ?? providerId);
    }

    public string ProviderId { get; }
    public Task<ProviderDetectionResult> DetectAsync(CancellationToken ct = default)
        => Task.FromResult(_result);
}

internal sealed class ThrowingDetector : IProviderDetector
{
    public string ProviderId => "throws";
    public Task<ProviderDetectionResult> DetectAsync(CancellationToken ct = default)
        => throw new InvalidOperationException("Detector intentionally threw");
}

internal sealed class FakeProviderStore : IProviderStore
{
    private readonly Dictionary<string, bool> _enabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Default return value for unknown provider IDs (mirrors FileProviderStore: true).
    /// </summary>
    public bool DefaultEnabled { get; set; } = true;

    public bool IsEnabled(string providerId) =>
        _enabled.TryGetValue(providerId, out var v) ? v : DefaultEnabled;

    public void SetEnabled(string providerId, bool enabled) =>
        _enabled[providerId] = enabled;

    public IReadOnlySet<string> Seen() => _seen;

    public void MarkSeen(string providerId) => _seen.Add(providerId);
}

// ── Tests ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Tests for <see cref="ProviderRegistry.BuildProvidersAsync"/>.
/// </summary>
public sealed class ProviderRegistryTests
{
    private static IUsageProvider MakeFakeProvider(string id) =>
        new FakeUsageProvider(new Fluentometer.Logic.Ipc.UsageSnapshot(
            id, 1_700_000_000L, "local", "ok", "TestPlan",
            Array.Empty<Fluentometer.Logic.Ipc.Gauge>()));

    // ────────────────────────────────────────────────────────────────────────
    // 1. Detected + enabled → included in results
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildProviders_DetectedAndEnabled_IsIncluded()
    {
        var store = new FakeProviderStore();
        var claudeProvider = MakeFakeProvider("claude");
        var registry = new ProviderRegistry(
            store,
            () => claudeProvider,
            new FakeDetector("claude", ProviderDetectionStatus.Detected, "Claude Code"));

        var providers = await registry.BuildProvidersAsync();

        Assert.Single(providers);
        Assert.Equal("claude", providers[0].ProviderId);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 2. Detected but disabled → excluded from results
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildProviders_DetectedButDisabled_IsExcluded()
    {
        var store = new FakeProviderStore();
        store.SetEnabled("claude", false);

        var registry = new ProviderRegistry(
            store,
            () => MakeFakeProvider("claude"),
            new FakeDetector("claude", ProviderDetectionStatus.Detected));

        var providers = await registry.BuildProvidersAsync();

        Assert.Empty(providers);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 3. Not detected → excluded regardless of enabled state
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildProviders_NotDetected_IsExcluded()
    {
        var store = new FakeProviderStore();
        // Claude is enabled by default.
        var registry = new ProviderRegistry(
            store,
            () => MakeFakeProvider("claude"),
            new FakeDetector("claude", ProviderDetectionStatus.NotFound));

        var providers = await registry.BuildProvidersAsync();

        Assert.Empty(providers);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 4. Error status → excluded (treated as NotFound for activation purposes)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildProviders_DetectorReturnsError_IsExcluded()
    {
        var store = new FakeProviderStore();
        var registry = new ProviderRegistry(
            store,
            () => MakeFakeProvider("claude"),
            new FakeDetector("claude", ProviderDetectionStatus.Error));

        var providers = await registry.BuildProvidersAsync();

        Assert.Empty(providers);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 5. Throwing detector → swallowed (G-11), other detectors still run
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildProviders_ThrowingDetector_DoesNotKillOtherDetectors()
    {
        var store = new FakeProviderStore();
        var claudeProvider = MakeFakeProvider("claude");

        var registry = new ProviderRegistry(
            store,
            () => claudeProvider,
            new ThrowingDetector(),                         // first: throws
            new FakeDetector("claude", ProviderDetectionStatus.Detected)); // second: succeeds

        var providers = await registry.BuildProvidersAsync();

        // The throwing detector is skipped; Claude must still be included.
        Assert.Single(providers);
        Assert.Equal("claude", providers[0].ProviderId);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 6. Empty detector list → empty provider list (no crash)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildProviders_NoDetectors_ReturnsEmpty()
    {
        var store = new FakeProviderStore();
        var registry = new ProviderRegistry(store, () => MakeFakeProvider("claude"));

        var providers = await registry.BuildProvidersAsync();

        Assert.Empty(providers);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 7. Claude factory fallback: registry uses the supplied factory for "claude"
    //    (not null); factory is invoked exactly when claude is detected+enabled.
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildProviders_ClaudeDetected_UsesSuppliedFactory()
    {
        var store = new FakeProviderStore();
        int factoryCallCount = 0;
        var registry = new ProviderRegistry(
            store,
            () => { factoryCallCount++; return MakeFakeProvider("claude"); },
            new FakeDetector("claude", ProviderDetectionStatus.Detected));

        var providers = await registry.BuildProvidersAsync();

        Assert.Equal(1, factoryCallCount);
        Assert.Single(providers);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 8. Cancellation during build → partial results, no throw
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildProviders_Cancelled_ReturnsPartialOrEmpty_DoesNotThrow()
    {
        var store = new FakeProviderStore();

        // Pre-cancelled token.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var registry = new ProviderRegistry(
            store,
            () => MakeFakeProvider("claude"),
            new FakeDetector("claude", ProviderDetectionStatus.Detected));

        // Must not throw; returns partial (empty is fine).
        var ex = await Record.ExceptionAsync(
            () => registry.BuildProvidersAsync(cts.Token));
        Assert.Null(ex);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 9. Unknown provider id from a detector → BuildProvider returns null → skipped
    //    (The registry only knows "claude" and "gemini"; unknown IDs are swallowed.)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildProviders_UnknownProviderId_IsSkipped()
    {
        var store = new FakeProviderStore();
        // A detector reporting a novel provider ID not in the registry's factory map.
        var registry = new ProviderRegistry(
            store,
            () => MakeFakeProvider("claude"),
            new FakeDetector("future-provider", ProviderDetectionStatus.Detected));

        var providers = await registry.BuildProvidersAsync();

        // future-provider is detected+enabled but the registry has no factory for it.
        Assert.Empty(providers);
    }

    // 10. chatgpt detected+enabled → chatgpt provider is included
    [Fact]
    public async Task BuildProviders_ChatGptDetectedAndEnabled_IsIncluded()
    {
        var store = new FakeProviderStore();
        var chatGptProvider = MakeFakeProvider("chatgpt");
        var registry = new ProviderRegistry(
            store,
            () => MakeFakeProvider("claude"),
            () => chatGptProvider,     // chatGptProviderFactory
            new FakeDetector("claude", ProviderDetectionStatus.NotFound),
            new FakeDetector("chatgpt", ProviderDetectionStatus.Detected, "ChatGPT"));

        var providers = await registry.BuildProvidersAsync();

        Assert.Single(providers);
        Assert.Equal("chatgpt", providers[0].ProviderId);
    }
}
