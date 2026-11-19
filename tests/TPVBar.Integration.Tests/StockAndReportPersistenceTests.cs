using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Cash;
using TPVBar.Application.Features.Catalog;
using TPVBar.Application.Features.Inventory;
using TPVBar.Application.Features.Orders;
using TPVBar.Application.Features.Reports;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Inventory;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class StockAndReportPersistenceTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<T> Full<TService, T>(IServiceProvider sp, Func<TService, Task<T>> op)
        where TService : notnull => CashHelpers.Full(sp, op);

    private static async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    /// <summary>Mundo de pruebas con la caña controlada por stock y N mesas, para enviar pedidos en paralelo.</summary>
    private static async Task<(World World, int[] Tables)> StockWorldAsync(IServiceProvider sp, decimal stock, int tables)
    {
        var world = await SalesHelpers.CreateWorldAsync(sp);
        var ids = new List<int> { world.TableId, world.Table2Id };
        await Full<CatalogService, bool>(sp, async c =>
        {
            for (var i = 0; i < tables - 2; i++)
            {
                ids.Add(await c.CreateTableAsync(world.ZoneId, $"Extra {i}", 2, 10 + i, Ct));
            }

            var current = (await c.ListProductsAsync(Ct)).Single(p => p.Id == world.CanaId);
            await c.UpdateProductAsync(world.CanaId, new ProductInput(current.CategoryId, current.TaxRateId, current.Name, current.SalePrice, current.Station, null, null, 1, true, 2m, "unidad"), Ct);
            return true;
        });
        await Full<StockService, bool>(sp, async s =>
        {
            await s.RegisterEntryAsync(world.CanaId, stock, "stock inicial de prueba", Guid.NewGuid(), Ct);
            return true;
        });
        return (world, [.. ids.Take(tables)]);
    }

    private static async Task<long> OrderOfAsync(IServiceProvider sp, World world, int tableId, decimal quantity, bool send)
    {
        var orderId = await Full<FloorService, long>(sp, f => f.OpenTableAsync(tableId, 2, Ct));
        await Full<OrderService, OrderView>(sp, o => o.AddLineAsync(orderId, world.CanaId, quantity, null, Guid.NewGuid(), Ct));
        if (send)
        {
            await Full<OrderService, SendResult>(sp, o => o.SendToKitchenAsync(orderId, Guid.NewGuid(), Ct));
        }

        return orderId;
    }

    private static async Task<(decimal Quantity, decimal LedgerSum, int Movements)> LedgerOfAsync(IServiceProvider sp, int productId)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var quantity = await db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.StockQuantity).SingleAsync(Ct);
        var movements = await db.StockMovements.AsNoTracking().Where(m => m.ProductId == productId).ToListAsync(Ct);
        return (quantity, movements.Sum(m => m.QuantityDelta), movements.Count);
    }

    [Fact]
    public async Task Migraciones_IncluyenStockYAjustes()
    {
        await using var services = fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();

        var tablas = await db.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'tpv'")
            .ToListAsync(Ct);

        Assert.Contains("stock_movements", tablas);
        Assert.Contains("app_settings", tablas);
        var columnas = await db.Database
            .SqlQuery<string>($"SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'tpv' AND table_name = 'products'")
            .ToListAsync(Ct);
        foreach (var esperada in new[] { "stock_controlled", "stock_quantity", "stock_minimum", "stock_unit" })
        {
            Assert.Contains(esperada, columnas);
        }
    }

    [Fact]
    public async Task DoceTerminalesEnviandoUnaUnidadCadaUna_ConDiezDeStock_NuncaDejanElStockNegativo()
    {
        await using var services = fixture.BuildServices();
        var (world, tables) = await StockWorldAsync(services, stock: 10m, tables: 12);
        var orders = new List<long>();
        foreach (var table in tables)
        {
            orders.Add(await OrderOfAsync(services, world, table, 1m, send: false));
        }

        var results = await Task.WhenAll(orders.Select(id =>
            Task.Run(() => TryAsync(async () => await Full<OrderService, SendResult>(services, o => o.SendToKitchenAsync(id, Guid.NewGuid(), Ct))), Ct)));

        Assert.Equal(10, results.Count(r => r)); // diez salen, dos se rechazan por falta de stock
        var ledger = await LedgerOfAsync(services, world.CanaId);
        Assert.Equal(0m, ledger.Quantity);
        Assert.Equal(ledger.Quantity, ledger.LedgerSum); // el stock siempre es la suma de sus movimientos
    }

    [Fact]
    public async Task EnviosSimultaneosConStockNegativoPermitido_NoPierdenNingunDescuento()
    {
        await using var services = fixture.BuildServices();
        var (world, tables) = await StockWorldAsync(services, stock: 3m, tables: 6);
        await Full<StockService, bool>(services, async s =>
        {
            await s.SetAllowNegativeAsync(true, Ct);
            return true;
        });
        try
        {
            var orders = new List<long>();
            foreach (var table in tables)
            {
                orders.Add(await OrderOfAsync(services, world, table, 2m, send: false));
            }

            await Task.WhenAll(orders.Select(id =>
                Task.Run(() => Full<OrderService, SendResult>(services, o => o.SendToKitchenAsync(id, Guid.NewGuid(), Ct)), Ct)));

            var ledger = await LedgerOfAsync(services, world.CanaId);
            Assert.Equal(3m - (6 * 2m), ledger.Quantity); // 6 envíos de 2 uds: ninguno se pierde por la carrera
            Assert.Equal(ledger.Quantity, ledger.LedgerSum);
        }
        finally
        {
            await Full<StockService, bool>(services, async s =>
            {
                await s.SetAllowNegativeAsync(false, Ct);
                return true;
            });
        }
    }

    [Fact]
    public async Task ElMismoEnvioDosVeces_DescuentaUnaSolaVez_YLaBaseDeDatosImpideElMovimientoDuplicado()
    {
        await using var services = fixture.BuildServices();
        var (world, tables) = await StockWorldAsync(services, stock: 10m, tables: 2);
        var orderId = await OrderOfAsync(services, world, tables[0], 3m, send: false);
        var key = Guid.NewGuid();

        await Full<OrderService, SendResult>(services, o => o.SendToKitchenAsync(orderId, key, Ct));
        await Full<OrderService, SendResult>(services, o => o.SendToKitchenAsync(orderId, key, Ct));

        Assert.Equal(7m, (await LedgerOfAsync(services, world.CanaId)).Quantity);

        // Aunque algo intentara descontar la misma línea otra vez, el índice único lo rechaza.
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var sale = await db.StockMovements.AsNoTracking().SingleAsync(m => m.ProductId == world.CanaId && m.Reason == StockMovementReason.Sale, Ct);
        db.StockMovements.Add(StockMovement.Create(world.CanaId, -3m, 4m, StockMovementReason.Sale, sale.ReferenceType, sale.ReferenceId, "duplicado", Guid.NewGuid(), DateTimeOffset.UtcNow));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [Fact]
    public async Task AnularUnaLineaEnviada_ReponeElStockUnaSolaVez()
    {
        await using var services = fixture.BuildServices();
        var (world, tables) = await StockWorldAsync(services, stock: 10m, tables: 2);
        var orderId = await OrderOfAsync(services, world, tables[0], 4m, send: true);
        Assert.Equal(6m, (await LedgerOfAsync(services, world.CanaId)).Quantity);
        var lineId = (await Full<OrderService, OrderView>(services, o => o.GetAsync(orderId, Ct))).Lines.Single().Id;

        await Full<OrderService, OrderView>(services, o => o.VoidLineAsync(orderId, lineId, "Cliente se fue", Ct));
        await Assert.ThrowsAsync<DomainException>(() => Full<OrderService, OrderView>(services, o => o.VoidLineAsync(orderId, lineId, "otra vez", Ct)));

        var ledger = await LedgerOfAsync(services, world.CanaId);
        Assert.Equal(10m, ledger.Quantity);
        Assert.Equal(ledger.Quantity, ledger.LedgerSum);
    }

    [Fact]
    public async Task EntradasSalidasYAjustes_QuedanRegistradosAuditadosYCuadran()
    {
        await using var services = fixture.BuildServices();
        var (world, _) = await StockWorldAsync(services, stock: 10m, tables: 2);

        await Full<StockService, bool>(services, async s =>
        {
            await s.RegisterEntryAsync(world.CanaId, 24m, "Pedido", Guid.NewGuid(), Ct);
            await s.RegisterExitAsync(world.CanaId, 4m, "Rotura", Guid.NewGuid(), Ct);
            await s.AdjustToCountAsync(world.CanaId, 25m, "Recuento", Guid.NewGuid(), Ct);
            return true;
        });

        var ledger = await LedgerOfAsync(services, world.CanaId);
        Assert.Equal(25m, ledger.Quantity);
        Assert.Equal(ledger.Quantity, ledger.LedgerSum);
        var history = await Full<StockService, IReadOnlyList<StockMovementRow>>(services, s => s.ListMovementsAsync(world.CanaId, 50, Ct));
        Assert.Equal([StockMovementReason.Adjustment, StockMovementReason.Exit, StockMovementReason.Entry, StockMovementReason.Entry], history.Select(h => h.Reason));
        Assert.Equal(25m, history[0].After);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        Assert.Equal(4, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.StockMovementRegistered && e.EntityId == world.CanaId.ToString(CultureInfo.InvariantCulture), Ct));
    }

    [Fact]
    public async Task LosMovimientosDeStockSonInmutables()
    {
        await using var services = fixture.BuildServices();
        var (world, _) = await StockWorldAsync(services, stock: 5m, tables: 2);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();

        foreach (var sql in new[] { "UPDATE tpv.stock_movements SET quantity_delta = 999", "DELETE FROM tpv.stock_movements", "TRUNCATE tpv.stock_movements" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, Ct));
            Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
        }

        Assert.Equal(5m, (await LedgerOfAsync(services, world.CanaId)).Quantity);
    }

    [Fact]
    public async Task ElStockNoPuedeFijarseDirectamenteEnLaFicha_SoloConMovimientos()
    {
        await using var services = fixture.BuildServices();
        var (world, _) = await StockWorldAsync(services, stock: 5m, tables: 2);

        // Editar la ficha (otros campos, cambiar el mínimo) no toca las existencias.
        await Full<CatalogService, bool>(services, async c =>
        {
            var p = (await c.ListProductsAsync(Ct)).Single(x => x.Id == world.CanaId);
            await c.UpdateProductAsync(world.CanaId, new ProductInput(p.CategoryId, p.TaxRateId, p.Name + " (nueva)", p.SalePrice, p.Station, null, null, 1, true, 9m, "botella"), Ct);
            return true;
        });

        var ledger = await LedgerOfAsync(services, world.CanaId);
        Assert.Equal(5m, ledger.Quantity);
        Assert.Equal(ledger.Quantity, ledger.LedgerSum);
    }

    // ---------- Informes con PostgreSQL ----------

    [Fact]
    public async Task Informe_UnaVentaCobradaSumaExactamenteSusImportes()
    {
        await using var services = fixture.BuildServices();
        await CashHelpers.EnsureOpenAsync(services);
        var today = BusinessTime.Today(TimeProvider.System);
        var before = await Full<ReportService, SalesReport>(services, r => r.GetSalesReportAsync(today, today, Ct));

        var orderId = await CashHelpers.SentOrderAsync(services); // 2 cañas (21 %) + 1 tortilla (10 %) = 7,10
        var paid = await CashHelpers.PayAsync(services, orderId, "CASH", 7.10m);
        await Full<PaymentService, long>(services, p => p.RefundAsync(paid.PaymentId, 1.00m, "prueba de informe", Guid.NewGuid(), Ct));
        var after = await Full<ReportService, SalesReport>(services, r => r.GetSalesReportAsync(today, today, Ct));

        Assert.Equal(before.Totals.Tickets + 1, after.Totals.Tickets);
        Assert.Equal(before.Totals.Sales + 7.10m, after.Totals.Sales);
        Assert.Equal(before.Totals.BaseAmount + 6.16m, after.Totals.BaseAmount);
        Assert.Equal(before.Totals.Tax + 0.94m, after.Totals.Tax);
        Assert.Equal(before.Totals.Refunds + 1.00m, after.Totals.Refunds);
        Assert.Equal(after.Totals.Sales, after.Totals.BaseAmount + after.Totals.Tax);
        Assert.Equal(after.Totals.Sales - after.Totals.Refunds, after.Totals.NetAfterRefunds);
        var cash = after.Methods.Single(m => m.Name == "Efectivo");
        var cashBefore = before.Methods.SingleOrDefault(m => m.Name == "Efectivo");
        Assert.Equal((cashBefore?.Payments ?? 0) + 7.10m, cash.Payments);
        Assert.Equal((cashBefore?.Refunds ?? 0) + 1.00m, cash.Refunds);
        Assert.Equal(after.Days.Sum(d => d.Sales), after.Totals.Sales); // la serie diaria cuadra con el total
        Assert.Equal(after.Hours.Sum(h => h.Sales), after.Totals.Sales);
    }

    [Fact]
    public async Task Informe_ElDiaLocalDeHoyIncluyeLoCobradoAhora()
    {
        await using var services = fixture.BuildServices();
        await CashHelpers.EnsureOpenAsync(services);
        var orderId = await CashHelpers.SentOrderAsync(services);
        await CashHelpers.PayAsync(services, orderId, "CARD", 7.10m);
        var today = BusinessTime.Today(TimeProvider.System);

        var hoy = await Full<ReportService, SalesReport>(services, r => r.GetSalesReportAsync(today, today, Ct));
        var ayer = await Full<ReportService, SalesReport>(services, r => r.GetSalesReportAsync(today.AddDays(-1), today.AddDays(-1), Ct));

        Assert.True(hoy.Totals.Tickets >= 1);
        Assert.Equal(0, ayer.Totals.Tickets); // la base de pruebas se recrea en cada ejecución
        var hour = BusinessTime.ToLocal(DateTimeOffset.UtcNow).Hour;
        Assert.Contains(hoy.Hours, h => h.Hour == hour || h.Hour == (hour + 23) % 24);
    }
}
