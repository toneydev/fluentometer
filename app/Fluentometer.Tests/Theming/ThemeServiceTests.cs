using Fluentometer.Logic.Theming;
using Xunit;

public class ThemeServiceTests
{
    private sealed class MemoryStore : IThemeStore
    {
        public string? Saved;
        public GradientDirection Direction = GradientDirection.DeepToBright;
        public string? LoadThemeId() => Saved;
        public void SaveThemeId(string id) => Saved = id;
        public GradientDirection LoadGradientDirection() => Direction;
        public void SaveGradientDirection(GradientDirection direction) => Direction = direction;
    }

    [Fact]
    public void CatalogHasAtLeastSixThemesAllWithValidColors()
    {
        Assert.True(ThemeCatalog.All.Count >= 6);
        foreach (var t in ThemeCatalog.All)
        {
            Assert.NotEmpty(t.BarStops);
            Assert.StartsWith("#", t.Accent);
            foreach (var stop in t.BarStops) Assert.StartsWith("#", stop);
        }
    }

    [Fact]
    public void InitializeUsesSavedThemeWhenPresent()
    {
        var store = new MemoryStore { Saved = "ember" };
        var svc = new ThemeService(store);
        svc.Initialize();
        Assert.Equal("ember", svc.Current.Id);
    }

    [Fact]
    public void InitializeFallsBackToDefaultWhenUnsetOrUnknown()
    {
        var svc = new ThemeService(new MemoryStore { Saved = "does-not-exist" });
        svc.Initialize();
        Assert.Equal(ThemeCatalog.Default.Id, svc.Current.Id);
    }

    [Fact]
    public void ApplyPersistsAndRaisesEvent()
    {
        var store = new MemoryStore();
        var svc = new ThemeService(store);
        svc.Initialize();
        GradientTheme? raised = null;
        svc.ThemeChanged += t => raised = t;
        svc.Apply("glacier");
        Assert.Equal("glacier", svc.Current.Id);
        Assert.Equal("glacier", store.Saved);
        Assert.Equal("glacier", raised?.Id);
    }

    // --- Gap coverage: unknown id lookup, Apply unknown id fallback ---

    [Fact]
    public void CatalogByIdUnknownReturnsNull()
    {
        var result = ThemeCatalog.ById("does-not-exist");
        Assert.Null(result);
    }

    [Fact]
    public void ApplyUnknownIdFallsBackToDefaultAndStillPersistsAndRaisesEvent()
    {
        // Documented behavior: Apply with an unknown id falls back to ThemeCatalog.Default,
        // persists the default's id (not the unknown id), and still raises ThemeChanged.
        var store = new MemoryStore();
        var svc = new ThemeService(store);
        svc.Initialize();
        GradientTheme? raised = null;
        svc.ThemeChanged += t => raised = t;

        svc.Apply("no-such-theme");

        Assert.Equal(ThemeCatalog.Default.Id, svc.Current.Id);
        Assert.Equal(ThemeCatalog.Default.Id, store.Saved);
        Assert.NotNull(raised);
        Assert.Equal(ThemeCatalog.Default.Id, raised!.Id);
    }

    [Fact]
    public void InitializeWithNullStoreFallsBackToDefault()
    {
        // When the store has never saved a theme (returns null), Initialize picks Default.
        var svc = new ThemeService(new MemoryStore { Saved = null });
        svc.Initialize();
        Assert.Equal(ThemeCatalog.Default.Id, svc.Current.Id);
    }

    [Fact]
    public void DirectionDefaultsToDeepToBright()
    {
        var svc = new ThemeService(new MemoryStore());
        svc.Initialize();
        Assert.Equal(GradientDirection.DeepToBright, svc.Direction);
    }

    [Fact]
    public void InitializeLoadsSavedDirection()
    {
        var svc = new ThemeService(new MemoryStore { Direction = GradientDirection.BrightToDeep });
        svc.Initialize();
        Assert.Equal(GradientDirection.BrightToDeep, svc.Direction);
    }

    [Fact]
    public void ApplyDirectionPersistsAndRaisesThemeChanged()
    {
        var store = new MemoryStore();
        var svc = new ThemeService(store);
        svc.Initialize();
        GradientTheme? raised = null;
        svc.ThemeChanged += t => raised = t;

        svc.ApplyDirection(GradientDirection.BrightToDeep);

        Assert.Equal(GradientDirection.BrightToDeep, svc.Direction);
        Assert.Equal(GradientDirection.BrightToDeep, store.Direction);
        Assert.NotNull(raised);
    }
}
