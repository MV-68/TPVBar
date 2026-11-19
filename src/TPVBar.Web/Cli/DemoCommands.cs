using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Cash;
using TPVBar.Application.Features.Inventory;
using TPVBar.Application.Features.Orders;
using TPVBar.Domain.Cash;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Security;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Web.Cli;

/// <summary>
/// <c>--seed-demo</c>: carga un historial FICTICIO de ventas para enseñar la aplicación (tres turnos de caja cerrados,
/// una devolución y un turno abierto con mesas en uso). Se ejecuta en lugar de arrancar el servidor, solo fuera de
/// producción y solo si la base de datos aún no tiene cajas (es idempotente). Pasa por los mismos casos de uso que
/// la interfaz, así que respeta todas las reglas de negocio. Las fechas son las del momento de la carga.
/// </summary>
internal static class DemoCommands
{
    private const int Seed = 2026;
    private static readonly Permission[] AllPermissions =
    [
        Permission.TakeOrders, Permission.VoidLines, Permission.ReopenTables, Permission.UseKitchen,
        Permission.TakePayments, Permission.OpenCloseCash, Permission.PerformRefunds, Permission.ViewReports, Permission.AdjustStock,
    ];

    public static bool IsDemoCommand(string[] args) => args.Contains("--seed-demo", StringComparer.Ordinal);

    public static async Task<int> RunAsync(WebApplication app)
    {
        if (app.Environment.IsProduction())
        {
            Console.Error.WriteLine("--seed-demo no se puede ejecutar en producción.");
            return 2;
        }

        try
        {
            await DatabaseInitializer.MigrateAsync(app.Services);
            await DatabaseInitializer.SeedReferenceDataAsync(app.Services);
            await DatabaseInitializer.SeedDevelopmentDataAsync(app.Services);

            await using (var probe = app.Services.CreateAsyncScope())
            {
                var db = probe.ServiceProvider.GetRequiredService<TpvBarDbContext>();
                if (await db.CashSessions.AnyAsync())
                {
                    Console.WriteLine("La base de datos ya tiene turnos de caja: no se añade historial demo.");
                    return 0;
                }
            }

            var random = new Random(Seed);
            var (products, tables, cashId, cardId) = await LoadReferenceAsync(app.Services);
            var tableCursor = 0;
            var stocked = await RestockAsync(app.Services);

            // Tres turnos cerrados: el último con una diferencia de caja y una devolución.
            for (var shift = 1; shift <= 3; shift++)
            {
                decimal opening = 100m;
                await Run<CashService, long>(app.Services, c => c.OpenAsync(opening, CancellationToken.None));

                decimal cashTaken = 0m;
                long? lastCardPayment = null;
                var sales = 18 + random.Next(0, 8);
                for (var i = 0; i < sales; i++)
                {
                    var tableId = tables[tableCursor++ % tables.Count];
                    var orderId = await BuildOrderAsync(app.Services, random, products, tableId, send: true);
                    var total = await TotalOf(app.Services, orderId);
                    if (random.Next(100) < 55)
                    {
                        var tendered = Math.Ceiling(total / 5m) * 5m;
                        await Pay(app.Services, orderId, cashId, total, tendered);
                        cashTaken += total;
                    }
                    else
                    {
                        lastCardPayment = (await Pay(app.Services, orderId, cardId, total, null)).PaymentId;
                    }
                }

                if (shift == 3 && lastCardPayment is { } paymentId)
                {
                    await Run<PaymentService, long>(app.Services, p =>
                        p.RefundAsync(paymentId, 1.50m, "Cliente disconforme (demo)", Guid.NewGuid(), CancellationToken.None));
                }

                var counted = opening + cashTaken - (shift == 3 ? 0.50m : 0m);
                await Run<CashService, CloseResult>(app.Services, c => c.CloseAsync(counted, shift == 3 ? "Descuadre de 0,50 € (demo)" : null, CancellationToken.None));
                Console.WriteLine(string.Create(CultureInfo.CurrentCulture, $"Turno {shift}: {sales} ventas, caja cerrada."));
            }

            // Turno abierto con mesas en uso para la demostración en directo.
            await Run<CashService, long>(app.Services, c => c.OpenAsync(150m, CancellationToken.None));
            var paid = await BuildOrderAsync(app.Services, random, products, tables[0], send: true);
            await Pay(app.Services, paid, cashId, await TotalOf(app.Services, paid), 20m);

            for (var i = 0; i < 3; i++)
            {
                await BuildOrderAsync(app.Services, random, products, tables[tableCursor++ % tables.Count], send: i != 2);
            }

            var billed = await BuildOrderAsync(app.Services, random, products, tables[tableCursor++ % tables.Count], send: true);
            await Run<OrderService, OrderView>(app.Services, o => o.RequestBillAsync(billed, CancellationToken.None));

            // Dos productos quedan por debajo del mÃ­nimo para que se vean los avisos de stock bajo.
            foreach (var productId in stocked.Take(2))
            {
                await Run<StockService, int>(app.Services, async s => { await s.AdjustToCountAsync(productId, 1m, "Recuento demo", Guid.NewGuid(), CancellationToken.None); return 0; });
            }

            Console.WriteLine("Turno abierto con 3 mesas ocupadas y 1 pendiente de cobro.");
            Console.WriteLine("Datos de demostración cargados.");
            return 0;
        }
        catch (DomainException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<(List<int> Products, List<int> Tables, int CashId, int CardId)> LoadReferenceAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Id).Select(p => p.Id).ToListAsync();
        var tables = await db.Tables.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Id).Select(t => t.Id).ToListAsync();
        var cash = await db.PaymentMethods.AsNoTracking().Where(m => m.Code == "CASH").Select(m => m.Id).SingleAsync();
        var card = await db.PaymentMethods.AsNoTracking().Where(m => m.Code == "CARD").Select(m => m.Id).SingleAsync();
        if (products.Count == 0 || tables.Count == 0)
        {
            throw new DomainException("Faltan productos o mesas: no hay catálogo demo que vender.");
        }

        return (products, tables, cash, card);
    }

    /// <summary>Repone las existencias demo (se descuentan al enviar a cocina) para que el historial no se quede sin gÃ©nero.</summary>
    private static async Task<List<int>> RestockAsync(IServiceProvider services)
    {
        List<int> ids;
        await using (var scope = services.CreateAsyncScope())
        {
            ids = await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().Products.AsNoTracking()
                .Where(p => p.StockControlled).OrderBy(p => p.Id).Select(p => p.Id).ToListAsync();
        }

        foreach (var id in ids)
        {
            await Run<StockService, int>(services, async s => { await s.RegisterEntryAsync(id, 300m, "ReposiciÃ³n demo", Guid.NewGuid(), CancellationToken.None); return 0; });
        }

        return ids;
    }

    private static async Task<long> BuildOrderAsync(IServiceProvider services, Random random, List<int> products, int tableId, bool send)
    {
        var orderId = await Run<FloorService, long>(services, f => f.OpenTableAsync(tableId, random.Next(1, 5), CancellationToken.None));
        var wanted = random.Next(2, 6);
        var added = 0;
        for (var attempt = 0; attempt < wanted * 4 && added < wanted; attempt++)
        {
            var product = products[random.Next(products.Count)];
            var quantity = random.Next(1, 4);
            try
            {
                await Run<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, product, quantity, null, Guid.NewGuid(), CancellationToken.None));
                added++;
            }
            catch (DomainException)
            {
                // Sin existencias suficientes de ese producto: se prueba con otro.
            }
        }

        if (added == 0)
        {
            throw new DomainException("No se ha podido añadir ningún producto a la comanda demo (¿sin existencias?).");
        }

        if (send)
        {
            await Run<OrderService, SendResult>(services, o => o.SendToKitchenAsync(orderId, Guid.NewGuid(), CancellationToken.None));
        }

        return orderId;
    }

    private static async Task<decimal> TotalOf(IServiceProvider services, long orderId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().Orders.AsNoTracking()
            .Where(o => o.Id == orderId).Select(o => o.GrandTotal).SingleAsync();
    }

    private static Task<PayResult> Pay(IServiceProvider services, long orderId, int methodId, decimal amount, decimal? tendered) =>
        Run<PaymentService, PayResult>(services, p => p.PayAsync(new PayRequest(orderId, methodId, amount, tendered, null, Guid.NewGuid()), CancellationToken.None));

    /// <summary>Ejecuta un caso de uso en un ámbito nuevo con identidad de demostración; reintenta los conflictos de concurrencia.</summary>
    private static async Task<T> Run<TService, T>(IServiceProvider services, Func<TService, Task<T>> operation)
        where TService : notnull
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CurrentUserAccessor>().Set(Guid.NewGuid(), AllPermissions, "demo");
            try
            {
                return await operation(scope.ServiceProvider.GetRequiredService<TService>());
            }
            catch (ConcurrencyConflictException) when (attempt < 8)
            {
                await Task.Delay(10 * attempt);
            }
        }
    }
}
