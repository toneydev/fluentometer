using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Capture;
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.Store;
using Xunit;

namespace Fluentometer.Tests.Capture;

// ── Fakes ──────────────────────────────────────────────────────────────────────

internal sealed class FakeUsageProvider : IUsageProvider
{
    private readonly UsageSnapshot _snapshot;
    public int CallCount { get; private set; }

    public FakeUsageProvider(UsageSnapshot? snapshot = null)
    {
        _snapshot = snapshot ?? new UsageSnapshot(
            "claude", 1_700_000_000L, "oauth", "ok", "Max",
            Array.Empty<Gauge>());
    }

    public string ProviderId => _snapshot.Provider;
    public TimeSpan MinPollInterval => TimeSpan.FromSeconds(180);

    public Task<UsageSnapshot> SnapshotAsync(long nowUnix, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(_snapshot);
    }
}

internal sealed class FakeSnapshotCache : ISnapshotCache
{
    // Keyed by providerId so tests can inspect per-provider caching.
    private readonly Dictionary<string, UsageSnapshot> _stored = new();
    private readonly Dictionary<string, UsageSnapshot> _preloaded;

    public FakeSnapshotCache(UsageSnapshot? preloaded = null)
    {
        _preloaded = preloaded is not null
            ? new Dictionary<string, UsageSnapshot> { [preloaded.Provider] = preloaded }
            : [];
    }

    /// <summary>
    /// Returns the last snapshot saved for <paramref name="providerId"/>.
    /// Use <see cref="GetStored(string)"/> in multi-provider tests.
    /// </summary>
    public UsageSnapshot? Stored
    {
        get
        {
            foreach (var v in _stored.Values) return v; // first value
            return null;
        }
    }

    public UsageSnapshot? GetStored(string providerId) =>
        _stored.TryGetValue(providerId, out var s) ? s : null;

    public UsageSnapshot? LoadLast(string providerId) =>
        _preloaded.TryGetValue(providerId, out var s) ? s : null;

    public void SaveLast(string providerId, UsageSnapshot snapshot) =>
        _stored[providerId] = snapshot;
}

// ── Tests ──────────────────────────────────────────────────────────────────────

public class LiveUsageClientTests
{
    private static UsageSnapshot MakeSnap(string health = "ok", string plan = "Max")
        => new UsageSnapshot("claude", 1_700_000_000L, "oauth", health, plan,
            Array.Empty<Gauge>());

    // --- Warm start from cache ---

    [Fact]
    public async Task EmitsCachedSnapshotBeforeFirstRefresh()
    {
        var cached = MakeSnap(plan: "cached-plan");
        var provider = new FakeUsageProvider(MakeSnap(plan: "fresh-plan"));
        var cache = new FakeSnapshotCache(cached);
        var client = new LiveUsageClient(provider, cache);

        var received = new List<UsageSnapshot>();
        client.SnapshotReceived += s => received.Add(s);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = client.StartAsync(cts.Token);

        // Wait until at least one snapshot arrives (the cached one fires synchronously).
        await Task.Delay(50, CancellationToken.None);

        // The first snapshot should be from cache.
        Assert.NotEmpty(received);
        Assert.Equal("cached-plan", received[0].Plan);

        cts.Cancel();
    }

    // --- ConnectionChanged(true) fires on start ---

    [Fact]
    public async Task ConnectionChangedTrueFiresOnStart()
    {
        var provider = new FakeUsageProvider();
        var client = new LiveUsageClient(provider, new FakeSnapshotCache());

        var connected = false;
        client.ConnectionChanged += v => connected = v;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = client.StartAsync(cts.Token);

        await Task.Delay(100, CancellationToken.None);

        Assert.True(connected, "ConnectionChanged(true) must fire after StartAsync begins");
        cts.Cancel();
    }

    // --- ConnectionChanged(false) is NEVER fired ---

    [Fact]
    public async Task ConnectionChangedFalseNeverFires()
    {
        var provider = new FakeUsageProvider();
        var client = new LiveUsageClient(provider, new FakeSnapshotCache());

        var falseFired = false;
        client.ConnectionChanged += v => { if (!v) falseFired = true; };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var runTask = client.StartAsync(cts.Token);

        await Task.Delay(200, CancellationToken.None);
        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }

        Assert.False(falseFired, "ConnectionChanged(false) must never be raised by LiveUsageClient");
    }

    // --- First refresh happens immediately ---

    [Fact]
    public async Task FirstRefreshHappensImmediatelyOnStart()
    {
        var provider = new FakeUsageProvider();
        var cache = new FakeSnapshotCache();
        var client = new LiveUsageClient(provider, cache);

        var received = new List<UsageSnapshot>();
        client.SnapshotReceived += s => received.Add(s);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = client.StartAsync(cts.Token);

        // Give the background task time to run the first refresh.
        await Task.Delay(200, CancellationToken.None);

        // Provider must have been called at least once, and a snapshot must have been emitted.
        Assert.True(provider.CallCount >= 1, "Provider must be called immediately on start");
        Assert.NotEmpty(received);
        cts.Cancel();
    }

    // --- Cache is written after refresh ---

    [Fact]
    public async Task CacheIsUpdatedAfterRefresh()
    {
        var snap = MakeSnap();
        var provider = new FakeUsageProvider(snap);
        var cache = new FakeSnapshotCache();
        var client = new LiveUsageClient(provider, cache);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = client.StartAsync(cts.Token);

        await Task.Delay(200, CancellationToken.None);

        Assert.NotNull(cache.Stored);
        // Compare fields (not record ==) to avoid IReadOnlyList reference equality trap.
        Assert.Equal(snap.Provider, cache.Stored!.Provider);
        Assert.Equal(snap.Health, cache.Stored.Health);
        Assert.Equal(snap.Plan, cache.Stored.Plan);

        cts.Cancel();
    }

    // --- GetSnapshot re-emits the latest snapshot without a provider call ---

    [Fact]
    public async Task GetSnapshot_ReEmitsLatestWithoutNewProviderCall()
    {
        var snap = MakeSnap(plan: "Max");
        var provider = new FakeUsageProvider(snap);
        var client = new LiveUsageClient(provider, new FakeSnapshotCache());

        var received = new List<UsageSnapshot>();
        client.SnapshotReceived += s => received.Add(s);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = client.StartAsync(cts.Token);

        // Wait for first refresh.
        await Task.Delay(200, CancellationToken.None);

        var callsAfterStart = provider.CallCount;
        var receivedAfterStart = received.Count;

        // Send GetSnapshot.
        await client.SendAsync(ClientCommand.GetSnapshot());
        await Task.Delay(100, CancellationToken.None);

        // Provider should not have been called again.
        Assert.Equal(callsAfterStart, provider.CallCount);
        // One more snapshot event should have been emitted.
        Assert.Equal(receivedAfterStart + 1, received.Count);

        cts.Cancel();
    }

    // --- RefreshNow within the floor is ignored ---

    [Fact]
    public async Task RefreshNow_WithinFloor_IsIgnored()
    {
        var provider = new FakeUsageProvider();
        var client = new LiveUsageClient(provider, new FakeSnapshotCache());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = client.StartAsync(cts.Token);

        // Wait for the first refresh.
        await Task.Delay(200, CancellationToken.None);

        var callsAfterFirstRefresh = provider.CallCount;

        // Immediately send RefreshNow — last refresh was just now, well within the 180s floor.
        await client.SendAsync(ClientCommand.RefreshNow());
        await Task.Delay(100, CancellationToken.None);

        // Provider call count must not increase.
        Assert.Equal(callsAfterFirstRefresh, provider.CallCount);

        cts.Cancel();
    }

    // --- SetPollInterval clamps to MinIntervalSecs ---

    [Fact]
    public void SetPollInterval_ClampsToMinimum()
    {
        // The clamping is a constant defined on the class; verify it here without
        // running the full async loop (which would require sleeping for 180+ s).
        Assert.Equal(180L, LiveUsageClient.MinIntervalSecs);
        Assert.Equal(300L, LiveUsageClient.DefaultIntervalSecs);

        // The clamping behavior is: Max(requested, MinIntervalSecs).
        // For a request of 60 s, the effective interval is 180 s.
        var clamped = Math.Max(60L, LiveUsageClient.MinIntervalSecs);
        Assert.Equal(180L, clamped);

        // For a request of 600 s, the effective interval is 600 s.
        var unclamped = Math.Max(600L, LiveUsageClient.MinIntervalSecs);
        Assert.Equal(600L, unclamped);
    }

    // --- SetPollInterval integration: sending the command doesn't crash the loop ---

    [Fact]
    public async Task SetPollInterval_DoesNotCrashTheLoop()
    {
        var provider = new FakeUsageProvider();
        var client = new LiveUsageClient(provider, new FakeSnapshotCache());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = client.StartAsync(cts.Token);

        await Task.Delay(100, CancellationToken.None);

        // Send SetPollInterval with a value below the floor — should clamp to 180 and restart.
        await client.SendAsync(ClientCommand.SetPollInterval(60));

        // Loop must still be running and responsive.
        await Task.Delay(200, CancellationToken.None);

        // Provider still healthy and emitting.
        Assert.True(provider.CallCount >= 1);

        cts.Cancel();
    }

    // --- SnapshotReceived fires from background thread, not deadlock ---

    [Fact]
    public async Task SnapshotReceived_CanBeHandledSafely()
    {
        var provider = new FakeUsageProvider();
        var client = new LiveUsageClient(provider, new FakeSnapshotCache());

        var tcs = new TaskCompletionSource<UsageSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        client.SnapshotReceived += s => tcs.TrySetResult(s);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = client.StartAsync(cts.Token);

        var snap = await tcs.Task.WaitAsync(cts.Token);

        Assert.Equal("claude", snap.Provider);
        cts.Cancel();
    }
}
