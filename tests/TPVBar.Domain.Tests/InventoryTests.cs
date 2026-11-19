using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Inventory;

namespace TPVBar.Domain.Tests;

public class InventoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static Product Controlled(decimal initial = 10m, decimal minimum = 2m)
    {
        var product = Product.Create(1, 1, "Caña", 1.80m, PreparationStation.Bar, null, null, 1, Now);
        product.ConfigureStock(true, minimum, "unidad", Now);
        if (initial > 0)
        {
            product.ApplyStockDelta(initial, false, Now);
        }

        return product;
    }

    [Fact]
    public void Producto_NuevoNoLlevaControlDeStock()
    {
        var product = Product.Create(1, 1, "Caña", 1.80m, PreparationStation.Bar, null, null, 1, Now);

        Assert.False(product.StockControlled);
        Assert.Equal(0m, product.StockQuantity);
        Assert.Equal("unidad", product.StockUnit);
        Assert.Throws<DomainException>(() => product.ApplyStockDelta(1m, false, Now)); // sin control no se mueve stock
    }

    [Fact]
    public void ConfigurarElControl_NoCambiaLasExistencias()
    {
        var product = Controlled(10m, 2m);

        product.ConfigureStock(true, 5m, "botella", Now);

        Assert.Equal((10m, 5m, "botella"), (product.StockQuantity, product.StockMinimum, product.StockUnit));
    }

    [Theory]
    [InlineData(-1, "unidad")]
    [InlineData(1.0005, "unidad")]
    [InlineData(1, "una-unidad-muy-larga-de-mas")]
    public void ConfigurarElControl_ValoresInvalidos_Falla(double minimum, string unit)
    {
        var product = Controlled();

        Assert.Throws<DomainException>(() => product.ConfigureStock(true, (decimal)minimum, unit, Now));
    }

    [Fact]
    public void Movimiento_SumaYRestaDevolviendoElSaldo()
    {
        var product = Controlled(10m);

        Assert.Equal(7m, product.ApplyStockDelta(-3m, false, Now));
        Assert.Equal(12m, product.ApplyStockDelta(5m, false, Now));
        Assert.Equal(12m, product.StockQuantity);
    }

    [Fact]
    public void Movimiento_QueDejaElStockNegativo_SeRechazaSalvoAutorizacion()
    {
        var product = Controlled(2m);

        var ex = Assert.Throws<DomainException>(() => product.ApplyStockDelta(-3m, false, Now));

        Assert.Contains("Stock insuficiente", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2m, product.StockQuantity); // no cambia al rechazar
        Assert.Equal(-1m, product.ApplyStockDelta(-3m, true, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.0005)]
    public void Movimiento_CantidadCeroOConMasDeTresDecimales_Falla(double delta) =>
        Assert.Throws<DomainException>(() => Controlled().ApplyStockDelta((decimal)delta, false, Now));

    [Fact]
    public void MovimientoDeStock_ValidaCantidadMotivoYNotas()
    {
        var movement = StockMovement.Create(1, -2m, 8m, StockMovementReason.Sale, "order_line", "5", "  nota ", Guid.NewGuid(), Now);

        Assert.Equal("nota", movement.Notes);
        Assert.Equal((-2m, 8m), (movement.QuantityDelta, movement.QuantityAfter));
        Assert.Throws<DomainException>(() => StockMovement.Create(1, 0m, 8m, StockMovementReason.Entry, null, null, null, Guid.NewGuid(), Now));
        Assert.Throws<DomainException>(() => StockMovement.Create(1, 1m, 8m, (StockMovementReason)99, null, null, null, Guid.NewGuid(), Now));
        Assert.Throws<DomainException>(() => StockMovement.Create(1, 1m, 8m, StockMovementReason.Entry, null, null, new string('x', 501), Guid.NewGuid(), Now));
    }

    [Fact]
    public void Ajustes_SeGuardanComoTextoYSePuedenCambiar()
    {
        var setting = new AppSetting(AppSettingKeys.AllowNegativeStock, "false", null, Now);

        setting.Change("true", Guid.NewGuid(), Now.AddMinutes(1));

        Assert.Equal("true", setting.Value);
        Assert.NotNull(setting.UpdatedBy);
        Assert.Throws<DomainException>(() => new AppSetting(" ", "x", null, Now));
    }
}
