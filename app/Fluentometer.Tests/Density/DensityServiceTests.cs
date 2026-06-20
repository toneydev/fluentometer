using System;
using System.IO;
using Fluentometer.Logic.Density;
using Fluentometer.Logic.Theming;
using Xunit;

namespace Fluentometer.Tests.Density;

// Mutates disk state in a temp dir — serialise like FileThemeStoreTests.
[CollectionDefinition(nameof(DensityServiceCollection), DisableParallelization = true)]
public sealed class DensityServiceCollection { }

[Collection(nameof(DensityServiceCollection))]
public sealed class DensityServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileThemeStore _store;

    public DensityServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DensityServiceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new FileThemeStore(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Initialize_defaults_to_comfortable_on_fresh_store()
    {
        var svc = new DensityService(_store);
        svc.Initialize();
        Assert.Equal(GaugeDensity.Comfortable, svc.Current);
    }

    [Fact]
    public void Initialize_loads_persisted_density()
    {
        var seed = _store.LoadAppSettings();
        seed.Density = "compact";
        _store.SaveAppSettings(seed);

        var svc = new DensityService(_store);
        svc.Initialize();
        Assert.Equal(GaugeDensity.Compact, svc.Current);
    }

    [Fact]
    public void Apply_persists_sets_current_and_raises_event()
    {
        var svc = new DensityService(_store);
        svc.Initialize();

        GaugeDensity? raised = null;
        svc.DensityChanged += d => raised = d;

        svc.Apply(GaugeDensity.Mini);

        Assert.Equal(GaugeDensity.Mini, svc.Current);
        Assert.Equal(GaugeDensity.Mini, raised);
        Assert.Equal("mini", _store.LoadAppSettings().Density);
    }

    [Fact]
    public void Apply_does_not_clobber_gradient_direction()
    {
        _store.SaveGradientDirection(GradientDirection.BrightToDeep);

        var svc = new DensityService(_store);
        svc.Initialize();
        svc.Apply(GaugeDensity.Compact);

        Assert.Equal(GradientDirection.BrightToDeep, _store.LoadGradientDirection());
    }
}
