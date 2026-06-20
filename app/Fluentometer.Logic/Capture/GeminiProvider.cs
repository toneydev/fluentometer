using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fluentometer.Logic.Ipc;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Server-truth usage provider for the Google Gemini CLI.
/// <para>
/// Reads the Gemini CLI OAuth token from <c>~/.gemini/oauth_creds.json</c> and polls the
/// Code Assist backend (<c>cloudcode-pa.googleapis.com</c>) for real quota — analogous to
/// <see cref="ClaudeProvider"/> and <see cref="ChatGptProvider"/>. There is NO local-estimate
/// fallback: on endpoint failure the snapshot is <c>degraded</c> with an empty gauge list (P2-B
/// posture). The old static "local estimate" gauge has been removed — it carried no real data.
/// </para>
/// </summary>
public sealed class GeminiProvider(IGeminiCredentialReader creds, ICloudCodeUsageClient client) : IUsageProvider
{
    /// <inheritdoc/>
    public string ProviderId => "gemini";

    /// <inheritdoc/>
    /// <remarks>180 s — the Code Assist endpoint is internal/rate-sensitive; poll conservatively.</remarks>
    public TimeSpan MinPollInterval => TimeSpan.FromSeconds(180);

    /// <inheritdoc/>
    public async Task<UsageSnapshot> SnapshotAsync(long nowUnix, CancellationToken ct)
    {
        var result = creds.Read();

        switch (result.Status)
        {
            case GeminiCredentialStatus.NotFound:
                return MakeSnapshot("needs-signin", "Gemini", Array.Empty<Gauge>(), nowUnix);
            case GeminiCredentialStatus.ParseError:
                return MakeSnapshot("error", "Gemini", Array.Empty<Gauge>(), nowUnix);
        }

        var cred = result.Credential!;

        // Token expiry is in Unix milliseconds; nowUnix is seconds.
        if (cred.IsExpired(nowUnix * 1000))
            return MakeSnapshot("needs-signin", "Gemini", Array.Empty<Gauge>(), nowUnix);

        // SECURITY: AccessToken is wrapped in RedactedString. Expose() is called only here,
        // inline as the argument — never stored, logged, or captured into a closure (P1-C).
        var fetch = await client.FetchAsync(cred.AccessToken.Expose(), ct);

        return fetch switch
        {
            CloudCodeResult.Ok ok =>
                MakeSnapshot("ok", ok.Plan, ok.Gauges, nowUnix),
            CloudCodeResult.Unauthorized =>
                MakeSnapshot("needs-signin", "Gemini", Array.Empty<Gauge>(), nowUnix),
            // RateLimited or Failed → degraded; no fallback for Gemini.
            _ =>
                MakeSnapshot("degraded", "Gemini", Array.Empty<Gauge>(), nowUnix),
        };
    }

    private static UsageSnapshot MakeSnapshot(string health, string plan, IReadOnlyList<Gauge> gauges, long nowUnix) =>
        new UsageSnapshot(
            Provider: "gemini",
            CapturedAt: nowUnix,
            Source: "oauth",
            Health: health,
            Plan: plan,
            Gauges: gauges);
}
