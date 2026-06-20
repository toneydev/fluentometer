using Fluentometer.Logic.Theming;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Fluentometer.Ui;

/// <summary>
/// Stateless factory for <see cref="LinearGradientBrush"/> instances used by gauge rendering.
///
/// Lives in app/Fluentometer/ (not Logic) because it depends on Windows.UI.Color and
/// Microsoft.UI.Xaml.Media — UI-project types that cannot be referenced from the
/// platform-neutral Logic assembly.
///
/// Two products:
///   BuildFill  — the bar fill gradient (direction-aware, endpoint parameterised).
///   BuildGlow  — the leading-edge glow overlay (horizontal, accent-derived).
///
/// GUARDRAIL — endpoint responsibility:
///   Dashboard fill  uses endpoint (1, 2)  — diagonal, caller passes explicitly.
///   Settings swatch uses endpoint (1, 0)  — horizontal, caller passes explicitly.
///   Do NOT collapse these into a shared constant; the distinction is intentional.
/// </summary>
internal static class GradientBrushFactory
{
    /// <summary>
    /// Builds a direction-aware fill brush from <paramref name="stops"/>.
    /// </summary>
    /// <param name="stops">Hex colour strings for the gradient stops (authored order).</param>
    /// <param name="direction">Controls whether stops are rendered bright→deep or deep→bright.</param>
    /// <param name="endPoint">
    /// The <see cref="LinearGradientBrush.EndPoint"/>. Pass <c>(1, 2)</c> for the dashboard
    /// diagonal fill and <c>(1, 0)</c> for the settings swatch horizontal fill.
    /// </param>
    public static LinearGradientBrush BuildFill(string[] stops, GradientDirection direction, Point endPoint)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = endPoint,
        };

        foreach (var (color, offset) in GradientStops.OrderedStops(stops, direction))
        {
            brush.GradientStops.Add(new GradientStop
            {
                Color = ColorParser.Parse(color),
                Offset = offset,
            });
        }

        return brush;
    }

    /// <summary>
    /// Builds the horizontal leading-edge glow brush from the theme's accent colour.
    /// The brush fades from fully transparent at offset 0 to 20% opaque accent at offset 1.
    /// </summary>
    /// <param name="accent">The accent colour for this theme/provider palette.</param>
    public static LinearGradientBrush BuildGlow(Color accent)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
        };

        brush.GradientStops.Add(new GradientStop
        {
            Color = Color.FromArgb(0x00, accent.R, accent.G, accent.B),
            Offset = 0,
        });
        brush.GradientStops.Add(new GradientStop
        {
            Color = Color.FromArgb(0x33, accent.R, accent.G, accent.B),
            Offset = 1,
        });

        return brush;
    }
}
