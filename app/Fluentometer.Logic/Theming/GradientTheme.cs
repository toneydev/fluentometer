namespace Fluentometer.Logic.Theming;

public sealed record GradientTheme(
    string Id,
    string Name,
    string[] BarStops,   // rich gradient painted ALONG the bar fill, left→right, #RRGGBB
    string Accent);      // representative solid: value text, selection ring, derived glow
