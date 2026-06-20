using System;
using System.Collections.Generic;

namespace Fluentometer.Logic.Theming;

/// <summary>
/// One provider's brand bar appearance: a 3-stop gradient (light → primary → dark)
/// plus the glow accent (the provider's canonical primary brand color).
/// </summary>
public readonly record struct BrandColors(string[] BarStops, string Accent);

/// <summary>
/// Maps a provider ID to its brand bar colors for the "Brand colors" theme.
/// Each gradient is anchored on the provider's canonical primary brand color as
/// the middle stop (which is also the glow accent), with a derived lighter and
/// darker stop. Gemini keeps its blue→purple identity via a purple darker stop.
///
/// Unknown / future providers fall back to a neutral slate gradient so brand mode
/// never throws or renders a blank bar. No WinUI dependency — unit-tested in
/// Fluentometer.Tests.
/// </summary>
public static class BrandPalette
{
    // Neutral slate, reused for any unrecognised provider.
    private static readonly BrandColors Fallback =
        new(new[] { "#94A3B8", "#64748B", "#334155" }, "#64748B");

    private static readonly Dictionary<string, BrandColors> s_byId =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = new(new[] { "#E59072", "#C15F3C", "#93421F" }, "#C15F3C"),
            ["chatgpt"] = new(new[] { "#9AC9BD", "#74AA9C", "#527E72" }, "#74AA9C"),
            ["gemini"] = new(new[] { "#74B3EE", "#4796E3", "#8E63C5" }, "#4796E3"),
        };

    /// <summary>
    /// Returns the brand colors for <paramref name="providerId"/> (case-insensitive),
    /// or the neutral slate fallback for an unknown provider.
    /// </summary>
    public static BrandColors For(string providerId) =>
        s_byId.TryGetValue(providerId, out var c) ? c : Fallback;
}
