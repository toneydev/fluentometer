namespace Fluentometer.Logic.Density;

/// <summary>
/// Single source of truth mapping a <see cref="GaugeDensity"/> to its
/// <see cref="DensityMetrics"/>, and parsing/serialising the persisted id.
/// Comfortable equals today's hard-coded card values (176 / 120 / 40 / 20,14).
/// MinItemWidth is intentionally NOT here — it stays 160 in XAML at every density
/// (div-by-zero guard, CLAUDE.md Gotcha 1).
/// </summary>
public static class DensityCatalog
{
    public static DensityMetrics For(GaugeDensity density) => density switch
    {
        GaugeDensity.Compact => new DensityMetrics(120, 96, 28, 16, 12, 16, 12, ShowCountdown: true),
        GaugeDensity.Mini => new DensityMetrics(84, 72, 22, 12, 10, 12, 10, ShowCountdown: false),
        _ => new DensityMetrics(176, 120, 40, 20, 14, 20, 14, ShowCountdown: true),
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
