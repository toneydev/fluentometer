namespace Fluentometer.Logic.Density;

/// <summary>
/// Single source of truth mapping a <see cref="GaugeDensity"/> to its
/// <see cref="DensityMetrics"/>, and parsing/serialising the persisted id.
/// Comfortable is today's Compact scaled up ~20% (144 / 116 / 34 / 20,14); Compact
/// reuses the old Mini values; Mini is the new slim-track-bar level. MinItemWidth is
/// intentionally NOT here — it stays 160 in XAML at every density (div-by-zero guard,
/// CLAUDE.md Gotcha 1).
/// </summary>
public static class DensityCatalog
{
    public static DensityMetrics For(GaugeDensity density) => density switch
    {
        GaugeDensity.Compact => new DensityMetrics(84, 72, 22, 12, 10, 12, 10, ShowCountdown: true, BarLayout: GaugeBarLayout.Wipe, MiniInline: false),
        GaugeDensity.Mini => new DensityMetrics(44, 40, 17, 12, 8, 12, 8, ShowCountdown: false, BarLayout: GaugeBarLayout.Track, MiniInline: true),
        _ => new DensityMetrics(144, 116, 34, 20, 14, 20, 14, ShowCountdown: true, BarLayout: GaugeBarLayout.Wipe, MiniInline: false),
    };

    public static GaugeDensity Parse(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "compact" => GaugeDensity.Compact,
        "mini" => GaugeDensity.Mini,
        _ => GaugeDensity.Comfortable,
    };

    public static string ToId(GaugeDensity density) => density switch
    {
        GaugeDensity.Compact => "compact",
        GaugeDensity.Mini => "mini",
        _ => "comfortable",
    };
}
