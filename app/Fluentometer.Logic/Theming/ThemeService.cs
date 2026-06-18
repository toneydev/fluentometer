using System;

namespace Fluentometer.Logic.Theming;

public sealed class ThemeService(IThemeStore store)
{
    public GradientTheme Current { get; private set; } = ThemeCatalog.Default;
    public GradientDirection Direction { get; private set; } = GradientDirection.DeepToBright;
    public event Action<GradientTheme>? ThemeChanged;

    public void Initialize()
    {
        var id = store.LoadThemeId();
        Current = (id is not null ? ThemeCatalog.ById(id) : null) ?? ThemeCatalog.Default;
        Direction = store.LoadGradientDirection();
    }

    public void Apply(string id)
    {
        var theme = ThemeCatalog.ById(id) ?? ThemeCatalog.Default;
        Current = theme;
        store.SaveThemeId(theme.Id);
        ThemeChanged?.Invoke(theme);
    }

    /// <summary>Sets and persists the gradient fill direction, then re-raises ThemeChanged so the UI re-renders.</summary>
    public void ApplyDirection(GradientDirection direction)
    {
        Direction = direction;
        store.SaveGradientDirection(direction);
        ThemeChanged?.Invoke(Current);
    }
}
