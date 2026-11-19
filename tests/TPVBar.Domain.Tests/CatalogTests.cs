using TPVBar.Domain.Catalog;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Floor;

namespace TPVBar.Domain.Tests;

public class CatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static Product NewProduct(decimal price = 1.80m) =>
        Product.Create(1, 1, "Caña", price, PreparationStation.Bar, "  P01 ", " desc ", 1, Now);

    [Fact]
    public void Product_Create_NormalizaTextos()
    {
        var product = NewProduct();

        Assert.Equal("P01", product.Sku);
        Assert.Equal("desc", product.Description);
        Assert.True(product.IsActive);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100000)]
    [InlineData(1.005)]
    public void Product_PrecioInvalido_Falla(double price) =>
        Assert.Throws<DomainException>(() => NewProduct((decimal)price));

    [Fact]
    public void Product_NombreVacioOLargo_Falla()
    {
        Assert.Throws<DomainException>(() => Product.Create(1, 1, " ", 1m, PreparationStation.Bar, null, null, 1, Now));
        Assert.Throws<DomainException>(() => Product.Create(1, 1, new string('x', 121), 1m, PreparationStation.Bar, null, null, 1, Now));
    }

    [Fact]
    public void Product_Update_DevuelveElPrecioAnterior()
    {
        var product = NewProduct(1.80m);

        var previous = product.Update(1, 1, "Caña", 2.00m, PreparationStation.Bar, null, null, 1, Now);

        Assert.Equal(1.80m, previous);
        Assert.Equal(2.00m, product.SalePrice);
    }

    [Fact]
    public void TaxRate_Vigencia()
    {
        var rate = TaxRate.Create("Reducido", 10m, new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31));

        Assert.False(rate.IsValidOn(new DateOnly(2023, 12, 31)));
        Assert.True(rate.IsValidOn(new DateOnly(2024, 1, 1)));
        Assert.True(rate.IsValidOn(new DateOnly(2024, 12, 31)));
        Assert.False(rate.IsValidOn(new DateOnly(2025, 1, 1)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100.1)]
    public void TaxRate_PorcentajeFueraDeRango_Falla(double rate) =>
        Assert.Throws<DomainException>(() => TaxRate.Create("X", (decimal)rate, new DateOnly(2024, 1, 1)));

    [Fact]
    public void TaxRate_FinAnteriorAlInicio_Falla() =>
        Assert.Throws<DomainException>(() => TaxRate.Create("X", 10m, new DateOnly(2024, 6, 1), new DateOnly(2024, 1, 1)));

    [Fact]
    public void DiningTable_CapacidadInvalida_Falla()
    {
        Assert.Throws<DomainException>(() => DiningTable.Create(1, "Mesa 1", 0, 1));
        Assert.Throws<DomainException>(() => DiningTable.Create(1, "Mesa 1", 100, 1));
    }

    [Fact]
    public void Zone_Y_Category_RequierenNombre()
    {
        Assert.Throws<DomainException>(() => Zone.Create(" ", 1, Now));
        Assert.Throws<DomainException>(() => Category.Create("", 1));
    }
}
