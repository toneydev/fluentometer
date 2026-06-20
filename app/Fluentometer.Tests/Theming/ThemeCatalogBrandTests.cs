using Fluentometer.Logic.Theming;
using Xunit;

public class ThemeCatalogBrandTests
{
    [Fact]
    public void BrandIdConstantIsBrand()
    {
        Assert.Equal("brand", ThemeCatalog.BrandId);
    }

    [Fact]
    public void CatalogContainsBrandTheme()
    {
        var brand = ThemeCatalog.ById(ThemeCatalog.BrandId);
        Assert.NotNull(brand);
        Assert.Equal("Brand colors", brand!.Name);
    }

    [Fact]
    public void DefaultIsStillAurora()
    {
        Assert.Equal("aurora", ThemeCatalog.Default.Id);
    }

    [Fact]
    public void CatalogHasNineThemes()
    {
        Assert.Equal(9, ThemeCatalog.All.Count);
    }
}
