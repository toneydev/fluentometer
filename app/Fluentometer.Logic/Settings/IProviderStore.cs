using System.Collections.Generic;

namespace Fluentometer.Logic.Settings;

/// <summary>
/// Persists per-provider enable/disable state and the "seen" set used by the
/// first-detection teaching tip in the Settings UI.
///
/// <para>
/// Storage: <c>%LOCALAPPDATA%\Fluentometer\providers.json</c> — a SEPARATE file from
/// <c>settings.json</c> (never touches <see cref="AppSettings"/> or
/// <c>FileThemeStore</c>).
/// </para>
///
/// <para>
/// Default state: every provider is enabled until explicitly disabled.
/// </para>
/// </summary>
public interface IProviderStore
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="providerId"/> is enabled (default).
    /// Unknown provider IDs return <c>true</c> (allow-by-default).
    /// </summary>
    bool IsEnabled(string providerId);

    /// <summary>
    /// Persists the enabled/disabled state for <paramref name="providerId"/>.
    /// </summary>
    void SetEnabled(string providerId, bool enabled);

    /// <summary>
    /// Returns the set of provider IDs that have been seen at least once
    /// (used to show / suppress the first-detection teaching tip).
    /// </summary>
    IReadOnlySet<string> Seen();

    /// <summary>
    /// Records that <paramref name="providerId"/> has been seen.
    /// Idempotent.
    /// </summary>
    void MarkSeen(string providerId);
}
