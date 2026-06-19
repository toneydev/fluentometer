// SECURITY: https://chatgpt.com/backend-api/wham/usage is an internal OpenAI endpoint
// with no public API contract or SLA — it can change without notice.  This is the same
// posture as ClaudeProvider calling /api/oauth/usage: we read credentials the user already
// holds, call that vendor's endpoint on behalf of that user, and display the result only to
// that user.  TLS is always verified.  Declared in SECURITY.md §Endpoint Transparency.
// CTO acknowledgment required before shipping (E-6 from 2026-06-19 security review).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Ipc;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Orchestrates Codex credential read → Wham HTTP fetch → UsageSnapshot.
/// <para>
/// Source is always <c>"oauth"</c> — ChatGPT has no local JSONL fallback.
/// On non-ok states the gauge list is <c>Array.Empty&lt;Gauge&gt;()</c>, never null (P2-B).
/// </para>
/// </summary>
public sealed class ChatGptProvider(ICodexCredentialReader creds, IWhamUsageClient wham) : IUsageProvider
{
    /// <inheritdoc/>
    public string ProviderId => "chatgpt";

    /// <inheritdoc/>
    /// <remarks>180 s — the Wham endpoint is rate-limited; do not poll faster than this.</remarks>
    public TimeSpan MinPollInterval => TimeSpan.FromSeconds(180);

    /// <inheritdoc/>
    public async Task<UsageSnapshot> SnapshotAsync(long nowUnix, CancellationToken ct)
    {
        var result = creds.Read();

        switch (result.Status)
        {
            case CodexCredentialStatus.NotFound:
                return MakeSnapshot("oauth", "needs-signin", "ChatGPT", Array.Empty<Gauge>(), nowUnix);

            case CodexCredentialStatus.ParseError:
                return MakeSnapshot("oauth", "error", "ChatGPT", Array.Empty<Gauge>(), nowUnix);
        }

        // CodexCredentialStatus.Ok — credential is present.
        var cred = result.Credential!;

        var plan = PlanFromPlanType(cred.PlanType);

        // SECURITY: AccessToken and AccountId are wrapped in RedactedString.
        // Expose() is called only here, inline as arguments — never stored in a local
        // variable, never logged, never captured into a closure. (P1-C)
        var whamResult = await wham.FetchAsync(
            "https://chatgpt.com/backend-api",
            cred.AccessToken.Expose(),
            cred.AccountId.Expose(),
            ct);

        return whamResult switch
        {
            WhamResult.Ok ok =>
                MakeSnapshot("oauth", "ok", plan, ok.Gauges, nowUnix),

            WhamResult.Unauthorized =>
                MakeSnapshot("oauth", "needs-signin", plan, Array.Empty<Gauge>(), nowUnix),

            // RateLimited or Failed → degraded; no JSONL fallback for ChatGPT (P2-B)
            _ =>
                MakeSnapshot("oauth", "degraded", plan, Array.Empty<Gauge>(), nowUnix),
        };
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a credential's <c>chatgpt_plan_type</c> string to the human-readable plan name
    /// shown in the UI. Analogous to <see cref="ClaudeProvider.PlanFromSubscription"/>.
    /// </summary>
    public static string PlanFromPlanType(string? planType) =>
        planType switch
        {
            string s when s.Equals("plus", StringComparison.OrdinalIgnoreCase) => "ChatGPT Plus",
            string s when s.Equals("pro", StringComparison.OrdinalIgnoreCase) => "ChatGPT Pro",
            string s when s.Length > 0 => s,
            _ => "ChatGPT",
        };

    private static UsageSnapshot MakeSnapshot(
        string source,
        string health,
        string plan,
        IReadOnlyList<Gauge> gauges,
        long nowUnix)
    {
        return new UsageSnapshot(
            Provider: "chatgpt",
            CapturedAt: nowUnix,
            Source: source,
            Health: health,
            Plan: plan,
            Gauges: gauges);
    }
}
