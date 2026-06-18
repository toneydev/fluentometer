using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Fluentometer.Logic.Ipc;
using Fluentometer.Logic.Store;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// In-process <see cref="IUsageClient"/> that replaces the former named-pipe / sidecar seam.
/// The poll loop:
/// <list type="bullet">
///   <item>Warms up from the snapshot cache on start.</item>
///   <item>Fires one refresh immediately.</item>
///   <item>Polls on a configurable interval (default 300 s, floor 180 s).</item>
///   <item>Handles <see cref="ClientCommand"/> (GetSnapshot, RefreshNow, SetPollInterval).</item>
/// </list>
/// <para>
/// <see cref="ConnectionChanged"/> is raised once with <c>true</c> on start and never
/// raised with <c>false</c> — in-process has no connectivity concept; degraded health is
/// carried by <see cref="UsageSnapshot.Health"/>.
/// </para>
/// </summary>
public sealed class LiveUsageClient(IUsageProvider provider, ISnapshotCache cache) : IUsageClient
{
    public const long DefaultIntervalSecs = 300;
    public const long MinIntervalSecs = 180;

    public event Action<UsageSnapshot>? SnapshotReceived;
    public event Action<bool>? ConnectionChanged;

    // Commands flow through an unbounded channel so SendAsync never blocks.
    private readonly Channel<ClientCommand> _commands =
        Channel.CreateUnbounded<ClientCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

    // Latest snapshot: lets GetSnapshot re-emit without a network call.
    private UsageSnapshot? _latest;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken ct)
    {
        // ── Warm start from cache ─────────────────────────────────────────────
        var cached = cache.LoadLast();
        if (cached is not null)
        {
            _latest = cached;
            SnapshotReceived?.Invoke(cached);
        }

        // In-process is always "connected".  Health flows through the snapshot.
        ConnectionChanged?.Invoke(true);

        // ── Initialise timing ─────────────────────────────────────────────────
        // Set lastRefresh far enough in the past that an immediate refresh is allowed.
        var lastRefresh = DateTimeOffset.UtcNow.AddSeconds(-MinIntervalSecs);
        var intervalSecs = DefaultIntervalSecs;

        // Perform the first refresh immediately (matches tokio interval's immediate tick).
        await RefreshAsync(ct);
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
                    await RefreshAsync(ct);
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
                                if (_latest is not null)
                                    SnapshotReceived?.Invoke(_latest);
                                break;

                            case "refreshNow":
                                var elapsed = (DateTimeOffset.UtcNow - lastRefresh).TotalSeconds;
                                if (elapsed >= MinIntervalSecs)
                                {
                                    await RefreshAsync(ct);
                                    lastRefresh = DateTimeOffset.UtcNow;
                                }
                                // else: within the floor — silently ignore.
                                break;

                            case "setPollInterval":
                                intervalSecs = Math.Max(
                                    cmd.Seconds ?? DefaultIntervalSecs,
                                    MinIntervalSecs);
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

    private async Task RefreshAsync(CancellationToken ct)
    {
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        UsageSnapshot snap;
        try
        {
            snap = await provider.SnapshotAsync(nowUnix, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // cancellation — don't emit or cache
        }
        catch (Exception)
        {
            // Defensive: provider is expected to return UsageResult.Failed, not throw.
            // If it does throw, swallow and skip the cycle.
            return;
        }

        // Best-effort cache write — swallow errors, no logging (credential-safe).
        try { cache.SaveLast(snap); } catch { }

        _latest = snap;
        SnapshotReceived?.Invoke(snap);
    }
}
