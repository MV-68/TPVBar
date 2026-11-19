using TPVBar.Application.Common;
using TPVBar.Application.Features.Catalog;
using TPVBar.Application.Features.Orders;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Tests;

public class FloorAndCatalogServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Floor_MuestraLibreOcupadaYPendienteDeCobro()
    {
        var f = new SalesFixture();
        var order1 = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        await f.Floor().OpenTableAsync(f.Mesa2.Id, 4, Ct);
        await f.Orders().AddLineAsync(order1, f.Cana.Id, 1, null, Guid.NewGuid(), Ct);
        await f.Orders().SendToKitchenAsync(order1, Guid.NewGuid(), Ct);
        await f.Orders().RequestBillAsync(order1, Ct);

        var floor = await f.Floor().GetFloorAsync(Ct);

        var tables = floor.Zones.Single().Tables;
        Assert.Equal(TableStatus.PendingPayment, tables.Single(t => t.Id == f.Mesa1.Id).Status);
        Assert.Equal(TableStatus.Occupied, tables.Single(t => t.Id == f.Mesa2.Id).Status);
        Assert.Equal(1.80m, tables.Single(t => t.Id == f.Mesa1.Id).Total);
    }

    [Fact]
    public async Task Floor_NoMuestraMesasOZonasInactivas()
    {
        var f = new SalesFixture();
        f.Mesa2.SetActive(false);

        var floor = await f.Floor().GetFloorAsync(Ct);

        Assert.Equal([f.Mesa1.Id], floor.Zones.Single().Tables.Select(t => t.Id));
    }

    [Fact]
    public async Task OpenTable_DosVecesDevuelveLaMismaComanda()
    {
        var f = new SalesFixture();

        var first = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        var second = await f.Floor().OpenTableAsync(f.Mesa1.Id, 5, Ct);

        Assert.Equal(first, second);
        Assert.Single(f.Data.OrderList);
        Assert.Single(f.Store.Audit, e => e.Action == AuditActions.OrderOpened);
    }

    [Fact]
    public async Task OpenTable_MesaInactivaOInexistente_Falla()
    {
        var f = new SalesFixture();
        f.Mesa1.SetActive(false);

        await Assert.ThrowsAsync<DomainException>(() => f.Floor().OpenTableAsync(f.Mesa1.Id, 1, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Floor().OpenTableAsync(999, 1, Ct));
    }

    [Fact]
    public async Task OpenTable_SinPermiso_EsDenegado()
    {
        var f = new SalesFixture(Permission.ViewReports);

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Floor().OpenTableAsync(f.Mesa1.Id, 1, Ct));
    }

    [Fact]
    public async Task Menu_SoloProductosActivosConImpuestoVigente()
    {
        var f = new SalesFixture();
        f.Tortilla.SetActive(false, SalesFixture.Start);

        var menu = await f.Menu().GetMenuAsync(Ct);

        Assert.Equal(["Bebidas"], menu.Categories.Select(c => c.Name));

        f.General.EndValidity(new DateOnly(2026, 11, 1));
        var sinImpuesto = await f.Menu().GetMenuAsync(Ct);
        Assert.Empty(sinImpuesto.Categories);
    }

    private static ProductInput Input(SalesFixture f, decimal price) =>
        new(f.Cana.CategoryId, f.General.Id, "Caña", price, PreparationStation.Bar, null, null, 1);

    [Fact]
    public async Task Catalog_SinManageCatalog_EsDenegado()
    {
        var f = new SalesFixture(Permission.TakeOrders);

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Catalog().ListProductsAsync(Ct));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Catalog().CreateZoneAsync("Patio", 4, Ct));
    }

    [Fact]
    public async Task Catalog_CambiarPrecio_ExigeModifyPricesYAudita()
    {
        var f = new SalesFixture(Permission.ManageCatalog);

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Catalog().UpdateProductAsync(f.Cana.Id, Input(f, 2.00m), Ct));
        Assert.Equal(1.80m, f.Cana.SalePrice);

        f.User.Set(f.UserId, [Permission.ManageCatalog, Permission.ModifyPrices], "test");
        await f.Catalog().UpdateProductAsync(f.Cana.Id, Input(f, 2.00m), Ct);

        Assert.Equal(2.00m, f.Cana.SalePrice);
        var audit = Assert.Single(f.Store.Audit, e => e.Action == AuditActions.PriceChanged);
        Assert.Contains("1.8", audit.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("2", audit.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catalog_EditarSinTocarElPrecio_NoExigeModifyPrices()
    {
        var f = new SalesFixture(Permission.ManageCatalog);

        await f.Catalog().UpdateProductAsync(f.Cana.Id, Input(f, 1.80m) with { Name = "Caña de barril" }, Ct);

        Assert.Equal("Caña de barril", f.Cana.Name);
        Assert.DoesNotContain(f.Store.Audit, e => e.Action == AuditActions.PriceChanged);
    }

    [Fact]
    public async Task Catalog_NoPermiteDesactivarMesaConComandaAbierta()
    {
        var f = new SalesFixture(Permission.ManageCatalog, Permission.TakeOrders);
        await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);

        await Assert.ThrowsAsync<DomainException>(() => f.Catalog().SetTableActiveAsync(f.Mesa1.Id, false, Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Catalog().SetZoneActiveAsync(f.Salon.Id, false, Ct));

        await f.Catalog().SetTableActiveAsync(f.Mesa2.Id, false, Ct);
        Assert.False(f.Mesa2.IsActive);
    }

    [Fact]
    public async Task Catalog_ProductoConCategoriaOImpuestoInexistente_Falla()
    {
        var f = new SalesFixture(Permission.ManageCatalog);

        await Assert.ThrowsAsync<DomainException>(() =>
            f.Catalog().CreateProductAsync(Input(f, 1m) with { CategoryId = 999 }, Ct));
        await Assert.ThrowsAsync<DomainException>(() =>
            f.Catalog().CreateProductAsync(Input(f, 1m) with { TaxRateId = 999 }, Ct));
    }

    [Fact]
    public async Task Catalog_CrearZonaYMesa_QuedaAuditado()
    {
        var f = new SalesFixture(Permission.ManageCatalog);

        var zoneId = await f.Catalog().CreateZoneAsync("Patio", 4, Ct);
        await f.Catalog().CreateTableAsync(zoneId, "Patio 1", 4, 1, Ct);

        Assert.Equal(2, f.Store.Audit.Count(e => e.Action == AuditActions.CatalogCreated));
    }
}
