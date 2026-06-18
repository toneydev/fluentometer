using System.Collections.Generic;

namespace Fluentometer.Logic.Theming;

/// <summary>
/// Pure ordering of a theme's BarStops for the chosen fill direction. No WinUI dependency —
/// unit-tested in Fluentometer.Tests.
/// </summary>
public static class GradientStops
{
    /// <summary>
    /// Returns (colour, offset) pairs evenly spaced 0→1. For <see cref="GradientDirection.BrightToDeep"/>
    /// the stops keep their authored order; for <see cref="GradientDirection.DeepToBright"/> the colour
    /// order is reversed while the offsets stay ascending.
    /// </summary>
    public static IReadOnlyList<(string Color, double Offset)> OrderedStops(
        string[] barStops, GradientDirection direction)
    {
        var n = barStops.Length;
        var result = new List<(string, double)>(n);
        for (var i = 0; i < n; i++)
        {
            var color = direction == GradientDirection.DeepToBright ? barStops[n - 1 - i] : barStops[i];
            var offset = n == 1 ? 0.0 : (double)i / (n - 1);
            result.Add((color, offset));
        }
        return result;
    }
}
