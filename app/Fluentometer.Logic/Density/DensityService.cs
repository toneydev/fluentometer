using System;
using Fluentometer.Logic.Theming;

namespace Fluentometer.Logic.Density;

/// <summary>
/// Owns the current gauge density and persists it. Mirrors ThemeService: Apply
/// writes through FileThemeStore (touching only the Density field) and raises
/// DensityChanged so the dashboard re-applies live. Depends on the concrete
/// FileThemeStore because it needs Load/SaveAppSettings (not on IThemeStore).
/// </summary>
public sealed class DensityService(FileThemeStore store)
{
    public GaugeDensity Current { get; private set; } = GaugeDensity.Comfortable;

    public event Action<GaugeDensity>? DensityChanged;

    public void Initialize()
        => Current = DensityCatalog.Parse(store.LoadAppSettings().Density);

    public void Apply(GaugeDensity density)
    {
        Current = density;

        // Read-modify-write so only the Density field changes; other AppSettings
        // fields (poll interval, offline, theme id) are preserved.
        var settings = store.LoadAppSettings();
        settings.Density = DensityCatalog.ToId(density);
        store.SaveAppSettings(settings);

        DensityChanged?.Invoke(density);
    }
}
