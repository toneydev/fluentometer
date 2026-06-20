namespace Fluentometer.Logic.Density;

/// <summary>
/// Uniform per-card sizing the dashboard applies for a given density. Padding is
/// carried as four doubles (NOT a WinUI Thickness) so this type stays free of any
/// XAML dependency — the presentation layer builds the Thickness.
/// </summary>
public sealed record DensityMetrics(
    double ItemMinHeight,
    double CardMinHeight,
    double ValueFontSize,
    double PadLeft,
    double PadTop,
    double PadRight,
    double PadBottom,
    bool ShowCountdown);
