using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Ipc;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Seam interface so <see cref="LiveUsageClient"/> can be unit-tested without
/// standing up real HTTP or credential files.
/// </summary>
public interface IUsageProvider
{
    /// <summary>
    /// Stable identifier for this provider (e.g. "claude", "gemini").
    /// Used as the per-provider cache key and to route snapshots in the ViewModel.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Minimum time between polls for this provider.
    /// <see cref="LiveUsageClient"/> clamps the shared timer to the MAX of all
    /// registered providers' floors so no provider is polled faster than it allows.
    /// </summary>
    TimeSpan MinPollInterval { get; }

    Task<UsageSnapshot> SnapshotAsync(long nowUnix, CancellationToken ct);
}

/// <summary>
/// Orchestrates credential read → OAuth fetch → JSONL fallback.
/// </summary>
public sealed class ClaudeProvider(
    string baseUrl,
    IClaudeCredentialReader creds,
    IOauthUsageClient oauth,
    IJsonlReader jsonl) : IUsageProvider
{
    /// <inheritdoc/>
    public string ProviderId => "claude";

    /// <inheritdoc/>
    /// <remarks>180 s — the endpoint is rate-limited; do not poll faster than this.</remarks>
    public TimeSpan MinPollInterval => TimeSpan.FromSeconds(180);

    /// <inheritdoc/>
    public async Task<UsageSnapshot> SnapshotAsync(long nowUnix, CancellationToken ct)
    {
        var result = creds.Read();

        switch (result.Status)
        {
            case CredentialStatus.NotFound:
                return MakeSnapshot("oauth", "needs-signin", "Not signed in", [], nowUnix);

            case CredentialStatus.ParseError:
                return MakeSnapshot("oauth", "error", "Unknown plan", [], nowUnix);
        }

        // CredentialStatus.Ok — credential is present.
        var cred = result.Credential!;

        if (cred.IsExpired(nowUnix * 1000))
            return MakeSnapshot("oauth", "needs-signin", "Session expired", [], nowUnix);

        var plan = PlanFromSubscription(cred.SubscriptionType);

        // SECURITY: AccessToken is wrapped in RedactedString. Expose() is called only
        // here for the Bearer call — never stored or logged.
        var usageResult = await oauth.FetchAsync(baseUrl, cred.AccessToken.Expose(), ct);

        return usageResult switch
        {
            UsageResult.Ok ok =>
                MakeSnapshot("oauth", "ok", plan, ok.Gauges, nowUnix),

            UsageResult.Unauthorized =>
                MakeSnapshot("oauth", "needs-signin", "Session expired", [], nowUnix),

            // RateLimited or Failed → degrade to local JSONL estimate.
            _ =>
                MakeSnapshot("jsonl", "degraded", plan, JsonlGauges(nowUnix), nowUnix),
        };
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a credential's <c>subscriptionType</c> string to the human-readable plan name
    /// shown in the UI.
    /// </summary>
    public static string PlanFromSubscription(string? subscriptionType) =>
        subscriptionType switch
        {
            string s when s.Equals("max", StringComparison.OrdinalIgnoreCase) => "Max",
            string s when s.Length > 0 => s,
            _ => "Claude",
        };

    private IReadOnlyList<Gauge> JsonlGauges(long nowUnix)
    {
        var events = jsonl.CollectEvents(ClaudePaths.ProjectsDir);
        var five = UsageWindow.Summarize(events, UsageWindow.FiveHourSecs, nowUnix);
        var week = UsageWindow.Summarize(events, UsageWindow.SevenDaySecs, nowUnix);

        return
        [
            new Gauge(
                Id: "session",
                Label: "Claude 5-hour",
                Utilization: null,
                UsedLabel: $"~{five.TokensInWindow} tokens",
                ResetsAt: five.ResetsAt,
                LimitLabel: "local estimate"),

            new Gauge(
                Id: "weekly_all",
                Label: "Claude Weekly",
                Utilization: null,
                UsedLabel: $"~{week.TokensInWindow} tokens",
                ResetsAt: week.ResetsAt,
                LimitLabel: "local estimate"),
        ];
    }

    private static UsageSnapshot MakeSnapshot(
        string source,
        string health,
        string plan,
        IReadOnlyList<Gauge> gauges,
        long nowUnix)
    {
        return new UsageSnapshot(
            Provider: "claude",
            CapturedAt: nowUnix,
            Source: source,
            Health: health,
            Plan: plan,
            Gauges: gauges);
    }
}
