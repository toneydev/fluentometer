using System;
using System.Globalization;
using Microsoft.UI;
using Windows.UI;

namespace Fluentometer.Ui;

/// <summary>
/// Shared hex-colour parsing helper used by DashboardPage and SettingsPage.
/// Parses #AARRGGBB or #RRGGBB strings into <see cref="Color"/>;
/// falls back to transparent on any parse failure.
/// Lives in app/Fluentometer/ because it depends on Windows.UI.Color (UI-project type).
/// </summary>
internal static class ColorParser
{
    public static Color Parse(string hex)
    {
        try
        {
            var s = hex.TrimStart('#');
            if (s.Length == 8)
            {
                var a = byte.Parse(s[0..2], NumberStyles.HexNumber);
                var r = byte.Parse(s[2..4], NumberStyles.HexNumber);
                var g = byte.Parse(s[4..6], NumberStyles.HexNumber);
                var b = byte.Parse(s[6..8], NumberStyles.HexNumber);
                return Color.FromArgb(a, r, g, b);
            }
            else if (s.Length == 6)
            {
                var r = byte.Parse(s[0..2], NumberStyles.HexNumber);
                var g = byte.Parse(s[2..4], NumberStyles.HexNumber);
                var b = byte.Parse(s[4..6], NumberStyles.HexNumber);
                return Color.FromArgb(0xFF, r, g, b);
            }
        }
        catch { /* fall through */ }
        return Colors.Transparent;
    }
}
