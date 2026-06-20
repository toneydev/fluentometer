using System;
using System.Collections.Generic;

namespace Fluentometer.Logic.Theming;

public static class ThemeCatalog
{
    /// <summary>
    /// Sentinel theme ID. When the active theme is this, the dashboard paints each
    /// provider's bar from <see cref="BrandPalette"/> (per-provider) instead of from
    /// this theme's own stops. This entry's own BarStops/Accent are used only for the
    /// Settings gallery selection ring and as an inert catalog representative.
    /// </summary>
    public const string BrandId = "brand";

    // Bar gradients drawn from spice-controller's saturated palette family.
    // 4 are spice palettes verbatim; 4 are extensions in the same range.
    public static IReadOnlyList<GradientTheme> All { get; } =
    [
        new GradientTheme("aurora", "Aurora",
            ["#6D28D9", "#DB2777", "#06B6D4"], "#DB2777"),   // spice Aurora
        new GradientTheme("ember", "Ember",
            ["#FF7A18", "#D63A2F", "#7A1F3D"], "#E25B2A"),   // spice Ember
        new GradientTheme("nebula", "Nebula",
            ["#9D2BDB", "#7C3AED", "#4F46E5"], "#7C3AED"),   // spice IndigoViolet (bright→deep, like the others)
        new GradientTheme("glacier", "Glacier",
            ["#0EA5A5", "#1668C7", "#1E3A8A"], "#22D3EE"),   // spice TealDeepSea
        new GradientTheme("verdant", "Verdant",
            ["#34D399", "#10B981", "#047857"], "#6EE7B7"),   // extension (emerald)
        new GradientTheme("sunset", "Sunset",
            ["#FB923C", "#F43F5E", "#BE185D"], "#FB7185"),   // extension (amber→rose)
        new GradientTheme("mono", "Mono",
            ["#94A3B8", "#64748B", "#334155"], "#CBD5E1"),   // extension (slate)
        new GradientTheme("porcelain", "Porcelain",
            ["#A5B4FC", "#818CF8", "#6366F1"], "#818CF8"),   // extension (soft indigo)
        new GradientTheme("brand", "Brand colors",
            ["#C15F3C", "#74AA9C", "#4796E3"], "#C15F3C"),   // sentinel — real bars come from BrandPalette
    ];

    // Build once at startup — O(1) lookup by ID instead of O(n) LINQ scan per call.
    private static readonly Dictionary<string, GradientTheme> s_byId =
        BuildIndex();

    private static Dictionary<string, GradientTheme> BuildIndex()
    {
        var d = new Dictionary<string, GradientTheme>(All.Count, StringComparer.Ordinal);
        foreach (var t in All) d[t.Id] = t;
        return d;
    }

    public static GradientTheme Default => s_byId["aurora"];

    public static GradientTheme? ById(string id) =>
        s_byId.TryGetValue(id, out var t) ? t : null;
}
