using TPVBar.Application.Common;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Sales;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Tests;

public class OrderServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<long> OpenAsync(SalesFixture f) => await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);

    [Fact]
    public async Task AddLine_UsaPrecioEImpuestoDeLaBaseDeDatos()
    {
        var f = new SalesFixture();
        var orderId = await OpenAsync(f);

        var view = await f.Orders().AddLineAsync(orderId, f.Cana.Id, 3, null, Guid.NewGuid(), Ct);

        var line = Assert.Single(view.Lines);
        Assert.Equal(1.80m, line.UnitPrice);
        Assert.Equal(5.40m, view.GrandTotal);
        Assert.Equal(4.46m, view.Subtotal);
        Assert.Equal(0.94m, view.TaxTotal);
    }

    [Fact]
    public async Task AddLine_MismaClaveDeIdempotencia_NoDuplica()
    {
        var f = new SalesFixture();
        var orderId = await OpenAsync(f);
        var key = Guid.NewGuid();

        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, key, Ct);
        var again = await f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, key, Ct);

        Assert.Equal(1, Assert.Single(again.Lines).Quantity);
        Assert.Single(f.Data.IdempotencyList);
    }

    [Fact]
    public async Task AddLine_ClavesDistintas_SumanUnidades()
    {
        var f = new SalesFixture();
        var orderId = await OpenAsync(f);

        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, Guid.NewGuid(), Ct);
        var view = await f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, Guid.NewGuid(), Ct);

        Assert.Equal(2, Assert.Single(view.Lines).Quantity);
    }

    [Fact]
    public async Task AddLine_ProductoInactivo_SeRechaza()
    {
        var f = new SalesFixture();
        var orderId = await OpenAsync(f);
        f.Cana.SetActive(false, SalesFixture.Start);

        await Assert.ThrowsAsync<DomainException>(() => f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task AddLine_SinTipoImpositivoVigente_SeRechaza()
    {
        var f = new SalesFixture();
        var orderId = await OpenAsync(f);
        f.General.EndValidity(new DateOnly(2026, 11, 1));

        var ex = await Assert.ThrowsAsync<DomainException>(() => f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, Guid.NewGuid(), Ct));

        Assert.Contains("impositivo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddLine_SinPermisoTakeOrders_EsDenegado()
    {
        var f = new SalesFixture(Permission.ViewReports);

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Orders().AddLineAsync(1, f.Cana.Id, 1, null, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task AddLine_AuditaLaOperacion()
    {
        var f = new SalesFixture();
        var orderId = await OpenAsync(f);

        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, Guid.NewGuid(), Ct);

        Assert.Contains(f.Store.Audit, e => e.Action == AuditActions.OrderLineAdded && e.UserId == f.UserId && e.Workstation == "test");
    }

    [Fact]
    public async Task SendToKitchen_CreaUnTicketPorDestinoYMarcaLasLineas()
    {
        var f = new SalesFixture();
        var orderId = await OpenAsync(f);
        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 2, null, Guid.NewGuid(), Ct);
        await f.Orders().AddLineAsync(orderId, f.Tortilla.Id, 1, "poco hecha", Guid.NewGuid(), Ct);

        var result = await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        Assert.False(result.AlreadyProcessed);
        Assert.Equal(2, f.Data.TicketList.Count);
        var bar = f.Data.TicketList.Single(t => t.Station == PreparationStation.Bar);
        var kitchen = f.Data.TicketList.Single(t => t.Station == PreparationStation.Kitchen);
        Assert.Equal("Salón · Mesa 1", bar.TableLabel);
        Assert.Equal("Caña", Assert.Single(bar.Lines).ProductNameSnapshot);
        Assert.Equal("poco hecha", Assert.Single(kitchen.Lines).Notes);
        Assert.All(f.Data.OrderList.Single().Lines, l => Assert.Equal(OrderLineStatus.Sent, l.Status));
        Assert.Contains(f.Store.Audit, e => e.Action == AuditActions.OrderSentToKitchen);
    }

    [Fact]
    public async Task SendToKitchen_RepetirLaClave_NoCreaTicketsNuevos()
    {
        var f = new SalesFixture();
        var orderId = await OpenAsync(f);
        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, Guid.NewGuid(), Ct);
        var key = Guid.NewGuid();

        await f.Orders().SendToKitchenAsync(orderId, key, Ct);
        var again = await f.Orders().SendToKitchenAsync(orderId, key, Ct);

        Assert.True(again.AlreadyProcessed);
        Assert.Single(f.Data.TicketList);
    }

    [Fact]
    public async Task SendToKitchen_SinLineasPendientes_Falla()
    {
        var f = new SalesFixture();
        var orderId = await OpenAsync(f);

        await Assert.ThrowsAsync<DomainException>(() => f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task SendToKitchen_SegundoEnvio_SoloLlevaLoNuevo()
    {
        var f = new SalesFixture();
        var orderId = await OpenAsync(f);
        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, Guid.NewGuid(), Ct);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        await f.Orders().AddLineAsync(orderId, f.Tortilla.Id, 1, null, Guid.NewGuid(), Ct);

        var second = await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        Assert.Equal(PreparationStation.Kitchen, Assert.Single(second.Tickets).Station);
        Assert.Equal(2, f.Data.TicketList.Count);
    }

    [Fact]
    public async Task VoidLine_ExigePermisoYMotivo()
    {
        var f = new SalesFixture(Permission.TakeOrders);
        var orderId = await OpenAsync(f);
        var view = await f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, Guid.NewGuid(), Ct);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        var lineId = view.Lines.Single().Id;

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Orders().VoidLineAsync(orderId, lineId, "error", Ct));

        f.User.Set(f.UserId, [Permission.TakeOrders, Permission.VoidLines], "test");
        await Assert.ThrowsAsync<DomainException>(() => f.Orders().VoidLineAsync(orderId, lineId, " ", Ct));
        var voided = await f.Orders().VoidLineAsync(orderId, lineId, "Cliente se fue", Ct);

        Assert.Equal(0, voided.GrandTotal);
        Assert.Equal(OrderLineStatus.Cancelled, voided.Lines.Single().Status);
        Assert.Contains(f.Store.Audit, e => e.Action == AuditActions.OrderLineVoided && e.DetailsJson.Contains("Cliente se fue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reopen_ExigePermisoReopenTables()
    {
        var f = new SalesFixture(Permission.TakeOrders);
        var orderId = await OpenAsync(f);
        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, Guid.NewGuid(), Ct);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        await f.Orders().RequestBillAsync(orderId, Ct);

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Orders().ReopenAsync(orderId, Ct));

        f.User.Set(f.UserId, [Permission.TakeOrders, Permission.ReopenTables], "test");
        var reopened = await f.Orders().ReopenAsync(orderId, Ct);
        Assert.Equal(OrderStatus.Open, reopened.Status);
    }

    [Fact]
    public async Task Cancel_LiberaLaMesaYAudita()
    {
        var f = new SalesFixture();
        var orderId = await OpenAsync(f);

        await f.Orders().CancelAsync(orderId, Ct);

        var floor = await f.Floor().GetFloorAsync(Ct);
        Assert.Equal(TPVBar.Application.Features.Orders.TableStatus.Free, floor.Zones.Single().Tables.First(t => t.Id == f.Mesa1.Id).Status);
        Assert.Contains(f.Store.Audit, e => e.Action == AuditActions.OrderCancelled);
    }

    [Fact]
    public async Task Get_ComandaInexistente_EsNotFound()
    {
        var f = new SalesFixture();

        await Assert.ThrowsAsync<NotFoundException>(() => f.Orders().GetAsync(999, Ct));
    }
}
