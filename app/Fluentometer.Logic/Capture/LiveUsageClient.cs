using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.Store;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// In-process <see cref="IUsageClient"/> that replaces the former named-pipe / sidecar seam.
///
/// <para>
/// The poll loop:
/// <list type="bullet">
///   <item>Warms up from the per-provider snapshot cache on start.</item>
///   <item>Fires one <see cref="IUsageClient.SnapshotReceived"/> event per provider on warm-start.</item>
///   <item>Performs an immediate first refresh across all providers.</item>
///   <item>Polls on a configurable interval (default 300 s).</item>
///   <item>The timer is clamped to the MAX of all providers' <see cref="IUsageProvider.MinPollInterval"/>
///         floors so no provider is polled faster than it allows.</item>
///   <item>Handles <see cref="ClientCommand"/> (GetSnapshot, RefreshNow, SetPollInterval).</item>
///   <item>One provider throwing does NOT skip the others' cycle or kill the loop.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Poll scheduling decision:</b> one shared <see cref="PeriodicTimer"/> drives all providers.
/// The user-chosen interval is clamped to the maximum of all providers'
/// <see cref="IUsageProvider.MinPollInterval"/> floors (currently 180 s for Claude).
/// This is simpler and safer than per-provider timers and preserves the Claude ≥180 s guarantee.
/// </para>
///
/// <para>
/// <b>RefreshNow floor semantics:</b> a single global <c>lastRefresh</c> gate is used, set to
/// the largest of all providers' <see cref="IUsageProvider.MinPollInterval"/> values.
/// Sending <c>refreshNow</c> within that floor is silently ignored — identical to the single-
/// provider behaviour callers already expect.
/// </para>
///
/// <para>
/// <see cref="ConnectionChanged"/> is raised once with <c>true</c> on start and never
/// raised with <c>false</c> — in-process has no connectivity concept; degraded health is
/// carried by <see cref="UsageSnapshot.Health"/>.
/// </para>
/// </summary>
public sealed class LiveUsageClient : IUsageClient
{
    public const long DefaultIntervalSecs = 300;

    /// <summary>
    /// Legacy constant kept for the tests that assert its value and clamp logic.
    /// In the multi-provider world the effective floor is the max of all registered
    /// providers' <see cref="IUsageProvider.MinPollInterval"/>; for a Claude-only list
    /// that is still 180 s, matching this constant exactly.
    /// </summary>
    public const long MinIntervalSecs = 180;

    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly ISnapshotCache _cache;

    /// <summary>
    /// Per-provider latest snapshot, keyed by <see cref="IUsageProvider.ProviderId"/>.
    /// </summary>
    private readonly Dictionary<string, UsageSnapshot> _latest = new();

    public event Action<UsageSnapshot>? SnapshotReceived;
    public event Action<bool>? ConnectionChanged;

    // Commands flow through an unbounded channel so SendAsync never blocks.
    private readonly Channel<ClientCommand> _commands =
        Channel.CreateUnbounded<ClientCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

    /// <summary>
    /// Creates a <see cref="LiveUsageClient"/> with a list of providers.
    /// </summary>
    public LiveUsageClient(IReadOnlyList<IUsageProvider> providers, ISnapshotCache cache)
    {
        _providers = providers;
        _cache = cache;
    }

    /// <summary>
    /// Convenience constructor for a single provider (used in tests and App.xaml.cs).
    /// </summary>
    public LiveUsageClient(IUsageProvider provider, ISnapshotCache cache)
        : this([provider], cache) { }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken ct)
    {
        // ── Warm start from per-provider cache ────────────────────────────────
        foreach (var p in _providers)
        {
            var cached = _cache.LoadLast(p.ProviderId);
            if (cached is not null)
            {
                _latest[p.ProviderId] = cached;
                SnapshotReceived?.Invoke(cached);
            }
        }

        // In-process is always "connected". Health flows through the snapshot.
        ConnectionChanged?.Invoke(true);

        // ── Effective floor: max of all providers' MinPollInterval ────────────
        // This ensures no provider is polled faster than it allows, while keeping
        // a single shared timer (simple, no fan-out complexity).
        var effectiveFloorSecs = ComputeFloorSecs();

        // ── Initialise timing ─────────────────────────────────────────────────
        // Set lastRefresh far enough in the past that an immediate refresh is allowed.
        var lastRefresh = DateTimeOffset.UtcNow.AddSeconds(-effectiveFloorSecs);
        var intervalSecs = DefaultIntervalSecs;

        // Perform the first refresh immediately (matches tokio interval's immediate tick).
        await RefreshAllAsync(ct);
        lastRefresh = DateTimeOffset.UtcNow;

        // ── Poll loop with interval-change support ────────────────────────────
        // Each iteration of the outer while creates a fresh PeriodicTimer with the
        // current intervalSecs. SetPollInterval breaks the inner loop which causes the
        // outer loop to recreate the timer at the new interval.
        while (!ct.IsCancellationRequested)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSecs));
            bool intervalChanged = false;

            while (!ct.IsCancellationRequested && !intervalChanged)
            {
                // Race: interval tick vs. incoming command.
                var timerTask = timer.WaitForNextTickAsync(ct).AsTask();
                var commandTask = _commands.Reader.WaitToReadAsync(ct).AsTask();

                var winner = await Task.WhenAny(timerTask, commandTask).ConfigureAwait(false);

                if (ct.IsCancellationRequested)
                    break;

                if (winner == timerTask)
                {
                    await RefreshAllAsync(ct);
                    lastRefresh = DateTimeOffset.UtcNow;
                }
                else
                {
                    // Drain all queued commands before waiting again.
                    while (_commands.Reader.TryRead(out var cmd))
                    {
                        switch (cmd.Type)
                        {
                            case "getSnapshot":
                                // Re-emit ALL providers' latest snapshots, one event each.
                                foreach (var snap in _latest.Values)
                                    SnapshotReceived?.Invoke(snap);
                                break;

                            case "refreshNow":
                                // Honor the global floor (max of all providers' floors).
                                var elapsed = (DateTimeOffset.UtcNow - lastRefresh).TotalSeconds;
                                if (elapsed >= effectiveFloorSecs)
                                {
                                    await RefreshAllAsync(ct);
                                    lastRefresh = DateTimeOffset.UtcNow;
                                }
                                // else: within the floor — silently ignore.
                                break;

                            case "setPollInterval":
                                // Clamp to max of (user-requested, effective floor).
                                intervalSecs = Math.Max(
                                    cmd.Seconds ?? DefaultIntervalSecs,
                                    effectiveFloorSecs);
                                intervalChanged = true;
                                // Break drain — the outer loop will recreate the timer.
                                break;
                        }

                        if (intervalChanged) break;
                    }
                }
            }
            // If intervalChanged == true, the outer while restarts with a new PeriodicTimer.
        }
    }

    /// <inheritdoc/>
    public Task SendAsync(ClientCommand cmd)
    {
        _commands.Writer.TryWrite(cmd);
        return Task.CompletedTask;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the effective poll floor in seconds: the maximum of all registered
    /// providers' <see cref="IUsageProvider.MinPollInterval"/>, clamped to at least
    /// <see cref="MinIntervalSecs"/> (180 s) as a hard safety floor.
    /// </summary>
    private long ComputeFloorSecs()
    {
        var maxFloor = MinIntervalSecs;
        foreach (var p in _providers)
        {
            var secs = (long)Math.Ceiling(p.MinPollInterval.TotalSeconds);
            if (secs > maxFloor) maxFloor = secs;
        }
        return maxFloor;
    }

    /// <summary>
    /// Polls every registered provider. One provider throwing does NOT skip the others.
    /// Each successful snapshot is cached, stored in <c>_latest</c>, and emitted.
    /// </summary>
    private async Task RefreshAllAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var provider in _providers)
        {
            if (ct.IsCancellationRequested) return;

            UsageSnapshot snap;
            try
            {
                snap = await provider.SnapshotAsync(nowUnix, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // cancellation — stop all further polling this cycle
            }
            catch (Exception)
            {
                // Defensive: provider is expected to return an error snapshot, not throw.
                // If it does throw, skip this provider but continue with the others.
                continue;
            }

            // Best-effort cache write — swallow errors, no logging (credential-safe).
            try { _cache.SaveLast(provider.ProviderId, snap); } catch { }

            _latest[provider.ProviderId] = snap;
            SnapshotReceived?.Invoke(snap);
        }
    }
}
