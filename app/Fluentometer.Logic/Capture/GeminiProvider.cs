using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Ipc;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Local-estimate usage provider for Google Gemini CLI.
///
/// <para>
/// This is a LOCAL-ESTIMATE ONLY provider — Gemini has no real-time usage endpoint
/// accessible to third-party clients. Snapshots are labelled with
/// <c>Source = "local"</c> and <c>Utilization = null</c> (no real %) on all gauges.
/// </para>
///
/// <para>
/// Architecture: analogous to the JSONL fallback path in <see cref="ClaudeProvider"/>
/// — it gives the user visibility that Gemini is active without claiming to know
/// exact utilization. The UI must never present this as server-authoritative data
/// (the <c>IsEstimate</c> / "local estimate" label path handles this).
/// </para>
///
/// <para>
/// No network calls are made — <see cref="MinPollInterval"/> is therefore shorter
/// than Claude's 180 s OAuth floor (60 s is sufficient; there's nothing to fetch).
/// The shared timer in <see cref="LiveUsageClient"/> is clamped to the MAX of all
/// registered providers' floors, so adding Gemini alone does NOT reduce Claude's
/// effective 180 s floor.
/// </para>
/// </summary>
public sealed class GeminiProvider : IUsageProvider
{
    private readonly string _authType;

    /// <summary>
    /// Creates a <see cref="GeminiProvider"/> for the given auth type
    /// (read from <c>settings.json</c> by <see cref="GeminiProviderDetector"/>).
    /// </summary>
    /// <param name="authType">
    /// The <c>selectedAuthType</c> value from Gemini's settings.json.
    /// Used as the <c>Plan</c> label in snapshots (e.g. "oauth-personal").
    /// </param>
    public GeminiProvider(string authType)
    {
        _authType = authType;
    }

    /// <inheritdoc/>
    public string ProviderId => "gemini";

    /// <inheritdoc/>
    /// <remarks>
    /// 60 s — local-only, no rate-limit concern.  The shared timer is clamped to the
    /// MAX of all providers' floors, so this does not undercut Claude's 180 s limit.
    /// </remarks>
    public TimeSpan MinPollInterval => TimeSpan.FromSeconds(60);

    /// <inheritdoc/>
    public Task<UsageSnapshot> SnapshotAsync(long nowUnix, CancellationToken ct)
    {
        // No network call — build entirely from local context.
        var plan = PlanFromAuthType(_authType);

        var gauges = new List<Gauge>
        {
            new Gauge(
                Id: "gemini_session",
                Label: "Gemini Usage",
                // Utilization is null — no real endpoint, so no real percent.
                Utilization: null,
                UsedLabel: "local estimate",
                ResetsAt: null,
                LimitLabel: "local estimate"),
        };

        var snapshot = new UsageSnapshot(
            Provider: "gemini",
            CapturedAt: nowUnix,
            Source: "local",
            Health: "ok",
            Plan: plan,
            Gauges: gauges);

        return Task.FromResult(snapshot);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps the Gemini CLI <c>selectedAuthType</c> to a human-readable plan/tier string.
    /// </summary>
    public static string PlanFromAuthType(string? authType) =>
        authType switch
        {
            "oauth-personal" => "Gemini (Personal)",
            "oauth-workspace" => "Gemini (Workspace)",
            "api-key" => "Gemini (API Key)",
            "vertex-ai" => "Gemini (Vertex AI)",
            string s when !string.IsNullOrWhiteSpace(s) => $"Gemini ({s})",
            _ => "Gemini",
        };
}
