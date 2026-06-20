namespace Fluentometer.Logic.Settings;

/// <summary>
/// Application settings persisted to %LOCALAPPDATA%\Fluentometer\settings.json.
/// The same JSON file is shared with FileThemeStore — the schema is a superset
/// of the ThemeId field that FileThemeStore previously owned.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Minimum allowed poll interval in seconds (3 minutes).</summary>
    public const int MinPollSeconds = 180;

    /// <summary>Theme identifier, defaults to "aurora".</summary>
    public string ThemeId { get; set; } = "aurora";

    /// <summary>
    /// Gauge density id — "comfortable" (default), "compact", or "mini".
    /// Parsed via DensityCatalog.Parse; unknown values fall back to "comfortable".
    /// </summary>
    public string Density { get; set; } = "comfortable";

    /// <summary>
    /// If true, the app reads only the locally cached usage file produced by
    /// the capture engine and never triggers an online refresh.
    /// </summary>
    public bool OfflineOnly { get; set; }

    private int _pollIntervalSeconds = 300;

    /// <summary>
    /// How often the capture engine polls for fresh usage data, in seconds.
    /// Values below <see cref="MinPollSeconds"/> are clamped to that floor.
    /// </summary>
    public int PollIntervalSeconds
    {
        get => _pollIntervalSeconds;
        set => _pollIntervalSeconds = value < MinPollSeconds ? MinPollSeconds : value;
    }
}
