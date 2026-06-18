using System;

namespace Fluentometer.Logic.Ui;

/// <summary>
/// Pure geometry for GaugeControl's wipe-reveal fill: maps a [0,1] value to the Composition
/// InsetClip RightInset and the leading-edge glow X offset for a given track width. No WinUI
/// dependency — unit-tested in Fluentometer.Tests.
/// </summary>
public static class GaugeMath
{
    /// <summary>Clamps <paramref name="value"/> to [0,1], snapping values below 0.001 to 0.</summary>
    public static double Fraction(double value)
    {
        var f = Math.Clamp(value, 0.0, 1.0);
        return f < 0.001 ? 0.0 : f;
    }

    /// <summary>InsetClip RightInset for a track of <paramref name="width"/>: 0 = full, width = empty.</summary>
    public static double RightInset(double value, double width) => width * (1.0 - Fraction(value));

    /// <summary>X offset placing a glow of <paramref name="glowWidth"/> with its right edge at the reveal tip.</summary>
    public static double GlowOffsetX(double value, double width, double glowWidth)
        => width * Fraction(value) - glowWidth;
}
