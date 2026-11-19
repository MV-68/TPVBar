using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Catalog;
using TPVBar.Application.Features.Orders;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Sales;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class SalesPersistenceTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<T> Waiter<TService, T>(IServiceProvider sp, Func<TService, Task<T>> op)
        where TService : notnull => SalesHelpers.RunAsync(sp, SalesHelpers.Waiter, op);

    private static Task<T> Manager<TService, T>(IServiceProvider sp, Func<TService, Task<T>> op)
        where TService : notnull => SalesHelpers.RunAsync(sp, SalesHelpers.Manager, op);

    [Fact]
    public async Task Migraciones_IncluyenLasTablasDeVentas()
    {
        await using var services = fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();

        var tablas = await db.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'tpv'")
            .ToListAsync(Ct);

        foreach (var esperada in new[] { "zones", "dining_tables", "categories", "tax_rates", "products", "orders", "order_lines", "kitchen_tickets", "kitchen_ticket_lines", "idempotency_records" })
        {
            Assert.Contains(esperada, tablas);
        }
    }

    [Fact]
    public async Task AbrirLaMismaMesaDesdeDiezTerminales_CreaUnaSolaComanda()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);

        var ids = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => Waiter<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct)), Ct)));

        Assert.Single(ids.Distinct());
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        Assert.Equal(1, await db.Orders.CountAsync(o => o.TableId == world.TableId, Ct));
    }

    [Fact]
    public async Task LaBaseDeDatos_RechazaUnaSegundaComandaVivaEnLaMesa()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        await Waiter<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        db.Orders.Add(Order.Open(world.TableId, Guid.NewGuid(), 1, DateTimeOffset.UtcNow));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [Fact]
    public async Task LaBaseDeDatos_RechazaTotalesIncoherentes()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        var orderId = await Waiter<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE tpv.orders SET grand_total = 10, subtotal = 1, tax_total = 1 WHERE id = {0}", [orderId], Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Totales_SePersistenConIvaIncluidoYCuadran()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        var orderId = await Waiter<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));

        await Waiter<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.CanaId, 3, null, Guid.NewGuid(), Ct));
        var view = await Waiter<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.TortillaId, 2, null, Guid.NewGuid(), Ct));

        Assert.Equal(12.40m, view.GrandTotal);
        Assert.Equal(10.82m, view.Subtotal);
        Assert.Equal(1.58m, view.TaxTotal);

        // Se vuelve a leer de PostgreSQL con otra conexión: lo guardado es lo mismo.
        var reread = await Waiter<OrderService, OrderView>(services, o => o.GetAsync(orderId, Ct));
        Assert.Equal(12.40m, reread.GrandTotal);
        Assert.Equal(2, reread.Lines.Count);
    }

    [Fact]
    public async Task AddLine_MismaClave_ProcesaUnaSolaVezAunqueSeEnvieEnParalelo()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        var orderId = await Waiter<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));
        var key = Guid.NewGuid();

        var resultados = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            try
            {
                await Waiter<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.CanaId, 1, null, key, Ct));
                return true;
            }
            catch (DomainException)
            {
                return false; // otra terminal ganó la carrera con la misma clave
            }
        }, Ct)));

        Assert.NotEmpty(resultados);
        var view = await Waiter<OrderService, OrderView>(services, o => o.GetAsync(orderId, Ct));
        Assert.Equal(1, Assert.Single(view.Lines).Quantity);
    }

    [Fact]
    public async Task SeisTerminalesAnadiendoALaVez_NoPierdenNingunaUnidad()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        var orderId = await Waiter<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(
            () => Waiter<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.CanaId, 1, null, Guid.NewGuid(), Ct)), Ct)));

        var view = await Waiter<OrderService, OrderView>(services, o => o.GetAsync(orderId, Ct));
        Assert.Equal(6, view.Lines.Sum(l => l.Quantity));
        Assert.Equal(10.80m, view.GrandTotal);
    }

    [Fact]
    public async Task CambiarElPrecioDespues_NoAlteraLoYaVendido()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        var orderId = await Waiter<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));
        await Waiter<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.CanaId, 1, null, Guid.NewGuid(), Ct));

        await Manager<CatalogService, bool>(services, async c =>
        {
            var current = (await c.ListProductsAsync(Ct)).Single(p => p.Id == world.CanaId);
            await c.UpdateProductAsync(world.CanaId, new ProductInput(current.CategoryId, current.TaxRateId, "Caña renombrada", 2.50m, current.Station, null, null, 1), Ct);
            return true;
        });
        var view = await Waiter<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.CanaId, 1, null, Guid.NewGuid(), Ct));

        Assert.Equal(2, view.Lines.Count);
        var old = view.Lines[0];
        Assert.StartsWith("Caña ", old.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("renombrada", old.Name, StringComparison.Ordinal);
        Assert.Equal(1.80m, old.UnitPrice);
        Assert.Equal(2.50m, view.Lines[^1].UnitPrice);
        Assert.Equal(4.30m, view.GrandTotal);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.PriceChanged && e.EntityId == world.CanaId.ToString(System.Globalization.CultureInfo.InvariantCulture), Ct));
    }

    [Fact]
    public async Task EnviarACocina_CreaTicketsPorDestinoYLaClaveRepetidaNoLosDuplica()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        var orderId = await Waiter<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));
        await Waiter<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.CanaId, 2, null, Guid.NewGuid(), Ct));
        await Waiter<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.TortillaId, 1, "sin cebolla", Guid.NewGuid(), Ct));
        var key = Guid.NewGuid();

        var first = await Waiter<OrderService, SendResult>(services, o => o.SendToKitchenAsync(orderId, key, Ct));
        var second = await Waiter<OrderService, SendResult>(services, o => o.SendToKitchenAsync(orderId, key, Ct));

        Assert.False(first.AlreadyProcessed);
        Assert.True(second.AlreadyProcessed);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var tickets = await db.KitchenTickets.Include(t => t.Lines).Where(t => t.OrderId == orderId).ToListAsync(Ct);
        Assert.Equal(2, tickets.Count);
        Assert.Contains(tickets, t => t.Station == PreparationStation.Kitchen && t.Lines.Single().Notes == "sin cebolla" && t.TableLabel.EndsWith("Mesa A", StringComparison.Ordinal));
        Assert.Contains(tickets, t => t.Station == PreparationStation.Bar && t.Lines.Single().Quantity == 2);
        Assert.True(await db.OrderLines.Where(l => l.OrderId == orderId).AllAsync(l => l.Status == OrderLineStatus.Sent, Ct));
    }

    [Fact]
    public async Task DosEnviosSimultaneosDeLoMismo_GeneranUnSoloJuegoDeTickets()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        var orderId = await Waiter<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));
        await Waiter<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.CanaId, 1, null, Guid.NewGuid(), Ct));
        await Waiter<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.TortillaId, 1, null, Guid.NewGuid(), Ct));

        var resultados = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try
            {
                await Waiter<OrderService, SendResult>(services, o => o.SendToKitchenAsync(orderId, Guid.NewGuid(), Ct));
                return true;
            }
            catch (DomainException)
            {
                return false; // "No hay líneas pendientes": otro puesto ya las envió
            }
        }, Ct)));

        Assert.Equal(1, resultados.Count(r => r));
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        Assert.Equal(2, await db.KitchenTickets.CountAsync(t => t.OrderId == orderId, Ct));
    }

    [Fact]
    public async Task AnularLinea_ExigePermisoConservaLaLineaYAudita()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        var orderId = await Waiter<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));
        var view = await Waiter<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.CanaId, 2, null, Guid.NewGuid(), Ct));
        await Waiter<OrderService, SendResult>(services, o => o.SendToKitchenAsync(orderId, Guid.NewGuid(), Ct));
        var lineId = view.Lines.Single().Id;

        await Assert.ThrowsAsync<ForbiddenException>(() => Waiter<OrderService, OrderView>(services, o => o.VoidLineAsync(orderId, lineId, "error", Ct)));
        var voided = await Manager<OrderService, OrderView>(services, o => o.VoidLineAsync(orderId, lineId, "Pedido por error", Ct));

        Assert.Equal(0, voided.GrandTotal);
        Assert.Equal(OrderLineStatus.Cancelled, Assert.Single(voided.Lines).Status);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var audit = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.OrderLineVoided && e.EntityId == orderId.ToString(System.Globalization.CultureInfo.InvariantCulture), Ct);
        Assert.Contains("Pedido por error", audit.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelarComanda_LiberaLaMesaYSePuedeVolverAAbrir()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        var first = await Waiter<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));

        await Waiter<OrderService, bool>(services, async o =>
        {
            await o.CancelAsync(first, Ct);
            return true;
        });
        var second = await Waiter<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Plano_ReflejaElEstadoDeCadaMesa()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        var orderId = await Waiter<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));
        await Waiter<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.CanaId, 1, null, Guid.NewGuid(), Ct));

        var floor = await Waiter<FloorService, FloorView>(services, f => f.GetFloorAsync(Ct));

        var tables = floor.Zones.Single(z => z.Id == world.ZoneId).Tables;
        Assert.Equal(TableStatus.Occupied, tables.Single(t => t.Id == world.TableId).Status);
        Assert.Equal(TableStatus.Free, tables.Single(t => t.Id == world.Table2Id).Status);
    }

    [Fact]
    public async Task NombresDuplicados_SeTraducenAErroresDeNegocio()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);

        var zones = await Manager<CatalogService, IReadOnlyList<ZoneRow>>(services, c => c.ListZonesAsync(Ct));
        var name = zones.Single(z => z.Id == world.ZoneId).Name;

        await Assert.ThrowsAsync<DomainException>(() => Manager<CatalogService, int>(services, c => c.CreateZoneAsync(name, 9, Ct)));
        await Assert.ThrowsAsync<DomainException>(() => Manager<CatalogService, int>(services, c => c.CreateTableAsync(world.ZoneId, "Mesa A", 4, 9, Ct)));
    }
}
