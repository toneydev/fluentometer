using System.Threading;
using System.Threading.Tasks;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Status of a provider detection attempt.
/// </summary>
public enum ProviderDetectionStatus
{
    /// <summary>The provider is installed and appears to be signed in.</summary>
    Detected,

    /// <summary>
    /// The provider's credential or configuration file was not found.
    /// This is the normal state on machines that don't have the provider installed.
    /// </summary>
    NotFound,

    /// <summary>
    /// An unexpected I/O or parse error occurred while probing.
    /// Treat as <see cref="NotFound"/> for activation purposes.
    /// </summary>
    Error,
}

/// <summary>
/// The result of a provider detection probe.
///
/// <para>
/// SECURITY (G-8): this is a discriminated union, never a raw credential.
/// Detectors must NOT read secret-bearing fields (G-2) or carry them in the result.
/// </para>
/// </summary>
/// <param name="Status">Whether the provider was detected.</param>
/// <param name="ProviderDisplayName">
/// Human-readable provider name (e.g. "Claude Code"), or <c>null</c> when not detected.
/// </param>
public sealed record ProviderDetectionResult(
    ProviderDetectionStatus Status,
    string? ProviderDisplayName);

/// <summary>
/// Probes a well-known location to determine whether a provider is installed
/// and appears to be signed in, WITHOUT reading any secret values.
///
/// <para>
/// Security invariants (G-1…G-12 from the multi-provider security model):
/// <list type="bullet">
///   <item>G-1: single try/catch read — no TOCTOU File.Exists + ReadAllText.</item>
///   <item>G-2: secret-bearing fields are never read during detection.</item>
///   <item>G-4: detectors never write to any probed location.</item>
///   <item>G-6: reparse-point (symlink/junction) check before any read.</item>
///   <item>G-7: fixed explicit candidate paths — no recursive directory walks.</item>
///   <item>G-8: result is a discriminated union, never a raw credential.</item>
///   <item>G-9: all detection I/O runs off the UI thread (async).</item>
///   <item>G-10: bounded — no retry loop, no sleep.</item>
///   <item>G-11: catch blocks return NotFound, never log secret-bearing messages.</item>
/// </list>
/// </para>
/// </summary>
public interface IProviderDetector
{
    /// <summary>
    /// The stable provider identifier this detector is responsible for (e.g. "claude").
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Probes the candidate path(s) and returns a detection result.
    /// Never throws; error cases are mapped to <see cref="ProviderDetectionStatus.Error"/>.
    /// Runs on the poll thread (G-9) — never call on the UI thread.
    /// </summary>
    Task<ProviderDetectionResult> DetectAsync(CancellationToken ct = default);
}
