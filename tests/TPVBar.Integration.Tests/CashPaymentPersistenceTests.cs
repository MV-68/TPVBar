using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Cash;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Cash;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Sales;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class CashPaymentPersistenceTests(PostgresFixture fixture)
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

    [Fact]
    public async Task Migraciones_IncluyenCajaYPagos_ConMetodosYPermisosIniciales()
    {
        await using var services = fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();

        var tablas = await db.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'tpv'")
            .ToListAsync(Ct);
        foreach (var esperada in new[] { "payment_methods", "cash_sessions", "cash_movements", "payments", "payment_refunds" })
        {
            Assert.Contains(esperada, tablas);
        }

        var methods = await db.PaymentMethods.AsNoTracking().ToListAsync(Ct);
        Assert.Contains(methods, m => m.Code == "CASH" && m.AffectsCashDrawer && m.Name == "Efectivo");
        Assert.Contains(methods, m => m.Code == "CARD" && !m.AffectsCashDrawer);
        Assert.Contains(methods, m => m.Code == "OTHER");

        var roles = await db.Roles.Include(r => r.Permissions).ToListAsync(Ct);
        foreach (var name in new[] { "Administrador", "Encargado", "Camarero", "Caja" })
        {
            Assert.Contains(roles.Single(r => r.Name == name).Permissions, p => p.Permission == TPVBar.Domain.Security.Permission.TakePayments);
        }

        Assert.DoesNotContain(roles.Single(r => r.Name == "Cocina").Permissions, p => p.Permission == TPVBar.Domain.Security.Permission.TakePayments);
    }

    [Fact]
    public async Task CobroEnEfectivoConCambio_QuedaPersistidoYLaMesaLibre()
    {
        await using var services = fixture.BuildServices();
        await CashHelpers.EnsureOpenAsync(services);
        var orderId = await CashHelpers.SentOrderAsync(services);

        var result = await CashHelpers.PayAsync(services, orderId, "CASH", 7.10m, tendered: 10m);

        Assert.True(result.OrderPaid);
        Assert.Equal(2.90m, result.Change);
        var order = await CashHelpers.OrderAsync(services, orderId);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(7.10m, order.PaidTotal);
        var payment = Assert.Single(await CashHelpers.PaymentsOfAsync(services, orderId));
        Assert.Equal((7.10m, 10m, 2.90m), (payment.Amount, payment.TenderedAmount, payment.ChangeGiven));

        // La mesa vuelve a estar libre: se puede abrir una comanda nueva.
        await using var scope = services.CreateAsyncScope();
        var tableId = (await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, Ct)).TableId;
        var again = await Full<TPVBar.Application.Features.Orders.FloorService, long>(services, f => f.OpenTableAsync(tableId, 1, Ct));
        Assert.NotEqual(orderId, again);
    }

    [Fact]
    public async Task DosTerminalesCobrandoLaCuentaEntera_SoloUnoCobra()
    {
        await using var services = fixture.BuildServices();
        await CashHelpers.EnsureOpenAsync(services);
        var orderId = await CashHelpers.SentOrderAsync(services);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            Task.Run(() => TryAsync(() => CashHelpers.PayAsync(services, orderId, "CARD", 7.10m)), Ct)));

        Assert.Equal(1, results.Count(r => r));
        Assert.Single(await CashHelpers.PaymentsOfAsync(services, orderId));
        Assert.Equal(7.10m, (await CashHelpers.OrderAsync(services, orderId)).PaidTotal);
    }

    [Fact]
    public async Task CincoCobrosParcialesALaVez_NuncaSuperanElTotal()
    {
        await using var services = fixture.BuildServices();
        await CashHelpers.EnsureOpenAsync(services);
        var world = await SalesHelpers.CreateWorldAsync(services);
        var orderId = await Full<TPVBar.Application.Features.Orders.FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));
        await Full<TPVBar.Application.Features.Orders.OrderService, TPVBar.Application.Features.Orders.OrderView>(services, o => o.AddLineAsync(orderId, world.CanaId, 3, null, Guid.NewGuid(), Ct)); // 5,40 €
        await Full<TPVBar.Application.Features.Orders.OrderService, TPVBar.Application.Features.Orders.SendResult>(services, o => o.SendToKitchenAsync(orderId, Guid.NewGuid(), Ct));

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            Task.Run(() => TryAsync(() => CashHelpers.PayAsync(services, orderId, "CASH", 1.80m)), Ct)));

        Assert.Equal(3, results.Count(r => r)); // 3 × 1,80 € = 5,40 €; los otros dos no caben
        var payments = await CashHelpers.PaymentsOfAsync(services, orderId);
        Assert.Equal(5.40m, payments.Sum(p => p.Amount));
        var order = await CashHelpers.OrderAsync(services, orderId);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(order.GrandTotal, order.PaidTotal);
    }

    [Fact]
    public async Task MismaClaveDeIdempotenciaEnParalelo_CobraUnaSolaVez()
    {
        await using var services = fixture.BuildServices();
        await CashHelpers.EnsureOpenAsync(services);
        var orderId = await CashHelpers.SentOrderAsync(services);
        var key = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            Task.Run(() => TryAsync(() => CashHelpers.PayAsync(services, orderId, "CASH", 2m, key: key)), Ct)));

        Assert.Single(await CashHelpers.PaymentsOfAsync(services, orderId));
        Assert.Equal(2m, (await CashHelpers.OrderAsync(services, orderId)).PaidTotal);
        await CashHelpers.PayAsync(services, orderId, "CASH", 5.10m); // se salda para no dejar cobros parciales
    }

    [Fact]
    public async Task ElDineroRegistradoEsInmutable_PagosDevolucionesYMovimientos()
    {
        await using var services = fixture.BuildServices();
        await CashHelpers.EnsureOpenAsync(services);
        var orderId = await CashHelpers.SentOrderAsync(services);
        var paid = await CashHelpers.PayAsync(services, orderId, "CASH", 7.10m);
        await Full<PaymentService, long>(services, p => p.RefundAsync(paid.PaymentId, 1m, "motivo", Guid.NewGuid(), Ct));
        await Full<CashService, bool>(services, async c =>
        {
            await c.AddMovementAsync(CashMovementType.CashIn, 1m, "aporte", Ct);
            return true;
        });

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        foreach (var table in new[] { "payments", "payment_refunds", "cash_movements" })
        {
            foreach (var sql in new[] { $"UPDATE tpv.{table} SET amount = amount + 1", $"DELETE FROM tpv.{table}", $"TRUNCATE tpv.{table}" })
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, Ct));

                // TRUNCATE de una tabla referenciada por claves foráneas ya lo rechaza PostgreSQL por sí mismo
                // (0A000) antes de llegar al trigger; en el resto actúa el trigger (restrict_violation).
                var acceptable = sql.StartsWith("TRUNCATE", StringComparison.Ordinal)
                    ? new[] { PostgresErrorCodes.RestrictViolation, PostgresErrorCodes.FeatureNotSupported }
                    : [PostgresErrorCodes.RestrictViolation];
                Assert.Contains(ex.SqlState, acceptable);
            }
        }

        Assert.Equal(7.10m, (await CashHelpers.PaymentsOfAsync(services, orderId)).Single().Amount);
    }

    [Fact]
    public async Task LaBaseDeDatos_RechazaCobradoMayorQueElTotalONegativo()
    {
        await using var services = fixture.BuildServices();
        var orderId = await CashHelpers.SentOrderAsync(services);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();

        var over = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE tpv.orders SET paid_total = grand_total + 1 WHERE id = {0}", [orderId], Ct));
        var negative = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE tpv.orders SET paid_total = -1 WHERE id = {0}", [orderId], Ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, over.SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, negative.SqlState);
    }

    [Fact]
    public async Task AbrirLaCajaDesdeCincoPuestosALaVez_DejaUnaSolaSesionAbierta()
    {
        await using var services = fixture.BuildServices();
        await CashHelpers.CloseAnyOpenAsync(services);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            Task.Run(() => TryAsync(async () => await Full<CashService, long>(services, c => c.OpenAsync(50m, Ct))), Ct)));

        Assert.Equal(1, results.Count(r => r));
        await using var scope = services.CreateAsyncScope();
        var open = await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().CashSessions.AsNoTracking().CountAsync(s => s.Status == CashSessionStatus.Open, Ct);
        Assert.Equal(1, open);
    }

    [Fact]
    public async Task CierreDeCaja_CalculaElEfectivoEsperadoConPagosMixtosDevolucionesYMovimientos()
    {
        await using var services = fixture.BuildServices();
        var sessionId = await CashHelpers.FreshSessionAsync(services, 100m);
        var orderId = await CashHelpers.SentOrderAsync(services);
        var cash = await CashHelpers.PayAsync(services, orderId, "CASH", 4.00m, tendered: 20m);
        await CashHelpers.PayAsync(services, orderId, "CARD", 3.10m);
        await Full<PaymentService, long>(services, p => p.RefundAsync(cash.PaymentId, 1.00m, "Error de cobro", Guid.NewGuid(), Ct));
        await Full<CashService, bool>(services, async c =>
        {
            await c.AddMovementAsync(CashMovementType.CashOut, 20m, "Pago a proveedor", Ct);
            await c.AddMovementAsync(CashMovementType.CashIn, 5m, "Aporte de cambio", Ct);
            return true;
        });

        var open = await Full<CashService, CashSessionView?>(services, c => c.GetCurrentAsync(Ct));
        Assert.Null(open!.ExpectedCash); // a ciegas
        var closed = await Full<CashService, CloseResult>(services, c => c.CloseAsync(87.50m, "prueba", Ct));

        Assert.Equal(88.00m, closed.Report.ExpectedCash);
        Assert.Equal(-0.50m, closed.Report.Difference);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var session = await db.CashSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId, Ct);
        Assert.Equal((CashSessionStatus.Closed, 88.00m, 87.50m, -0.50m), (session.Status, session.ExpectedCash, session.CountedCash, session.Difference));
        Assert.NotNull(session.ClosedBy);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.CashClosed && e.EntityId == sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture), Ct));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.PaymentRefunded && e.EntityId == cash.PaymentId.ToString(System.Globalization.CultureInfo.InvariantCulture), Ct));

        // Cerrada: ya no admite cobros.
        var orderId2 = await CashHelpers.SentOrderAsync(services);
        await Assert.ThrowsAsync<DomainException>(() => CashHelpers.PayAsync(services, orderId2, "CASH", 1m));
    }

    [Fact]
    public async Task CerrarLaCajaMientrasSeCobra_NingunCobroQuedaFueraDelEfectivoEsperado()
    {
        await using var services = fixture.BuildServices();

        for (var round = 0; round < 4; round++)
        {
            var sessionId = await CashHelpers.FreshSessionAsync(services, 0m);
            var orderId = await CashHelpers.SentOrderAsync(services);

            var outcomes = await Task.WhenAll(
                Task.Run(() => TryAsync(() => CashHelpers.PayAsync(services, orderId, "CASH", 7.10m)), Ct),
                Task.Run(() => TryAsync(async () => await Full<CashService, CloseResult>(services, c => c.CloseAsync(0m, null, Ct))), Ct));

            // El cierre o el cobro pueden ganar la carrera, pero la contabilidad siempre debe cuadrar.
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
            var session = await db.CashSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId, Ct);
            var cashPaid = await db.Payments.AsNoTracking().Where(p => p.CashSessionId == sessionId).SumAsync(p => p.Amount, Ct);
            if (session.Status == CashSessionStatus.Closed)
            {
                Assert.Equal(cashPaid, session.ExpectedCash); // fondo 0 + cobros en efectivo
                Assert.Equal(outcomes[0], cashPaid > 0); // si el cobro triunfó, está contado en esta sesión
            }
            else
            {
                // El cierre falló (p. ej. conflicto agotado): la sesión sigue abierta y consistente.
                Assert.True(outcomes[0] || cashPaid == 0);
            }
        }
    }

    [Fact]
    public async Task NoSePuedeCerrarConUnCobroParcial_YSePuedeTrasCompletarlo()
    {
        await using var services = fixture.BuildServices();
        await CashHelpers.FreshSessionAsync(services, 0m);
        var orderId = await CashHelpers.SentOrderAsync(services);
        await CashHelpers.PayAsync(services, orderId, "CASH", 2m);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Full<CashService, CloseResult>(services, c => c.CloseAsync(2m, null, Ct)));
        Assert.Contains("cobro parcial", ex.Message, StringComparison.Ordinal);

        await CashHelpers.PayAsync(services, orderId, "CASH", 5.10m);
        var closed = await Full<CashService, CloseResult>(services, c => c.CloseAsync(7.10m, null, Ct));
        Assert.Equal(0m, closed.Report.Difference);
    }

    [Fact]
    public async Task Devolucion_NoSuperaLoCobradoYDejaElPagoOriginalIntacto()
    {
        await using var services = fixture.BuildServices();
        await CashHelpers.EnsureOpenAsync(services);
        var orderId = await CashHelpers.SentOrderAsync(services);
        var paid = await CashHelpers.PayAsync(services, orderId, "CARD", 7.10m);

        await Full<PaymentService, long>(services, p => p.RefundAsync(paid.PaymentId, 5m, "Producto en mal estado", Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<DomainException>(() => Full<PaymentService, long>(services, p => p.RefundAsync(paid.PaymentId, 2.11m, "de más", Guid.NewGuid(), Ct)));
        await Full<PaymentService, long>(services, p => p.RefundAsync(paid.PaymentId, 2.10m, "resto", Guid.NewGuid(), Ct));

        var order = await CashHelpers.OrderAsync(services, orderId);
        Assert.Equal(0m, order.PaidTotal);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(7.10m, Assert.Single(await CashHelpers.PaymentsOfAsync(services, orderId)).Amount);
    }

    [Fact]
    public async Task Devoluciones_SimultaneasDelMismoCobro_NuncaDevuelvenMasDeLoCobrado()
    {
        await using var services = fixture.BuildServices();
        await CashHelpers.EnsureOpenAsync(services);
        var orderId = await CashHelpers.SentOrderAsync(services);
        var paid = await CashHelpers.PayAsync(services, orderId, "CASH", 7.10m);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => TryAsync(async () => await Full<PaymentService, long>(services, p => p.RefundAsync(paid.PaymentId, 3m, "reclamación", Guid.NewGuid(), Ct))), Ct)));

        Assert.Equal(2, results.Count(r => r)); // 3 + 3 = 6 ≤ 7,10; un tercero (9) no cabe
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        Assert.Equal(6m, await db.PaymentRefunds.AsNoTracking().Where(r => r.PaymentId == paid.PaymentId).SumAsync(r => r.Amount, Ct));
        Assert.Equal(1.10m, (await CashHelpers.OrderAsync(services, orderId)).PaidTotal);
    }

    [Fact]
    public async Task CobroYDevolucion_QuedanAuditadosConElUsuarioYSinLaReferencia()
    {
        await using var services = fixture.BuildServices();
        await CashHelpers.EnsureOpenAsync(services);
        var orderId = await CashHelpers.SentOrderAsync(services);
        var methodId = await CashHelpers.MethodIdAsync(services, "CARD");

        await Full<PaymentService, PayResult>(services, p => p.PayAsync(new PayRequest(orderId, methodId, 7.10m, null, "REF-CONFIDENCIAL-77", Guid.NewGuid()), Ct));

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Action == AuditActions.PaymentRegistered && e.EntityId == orderId.ToString(System.Globalization.CultureInfo.InvariantCulture), Ct);
        Assert.NotNull(audit.UserId);
        Assert.DoesNotContain("REF-CONFIDENCIAL-77", audit.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"hasReference\": true", audit.DetailsJson, StringComparison.Ordinal); // jsonb normaliza con un espacio
    }

    [Fact]
    public async Task Cobros_SobrevivenAlReinicioDelServidor()
    {
        await using var services = fixture.BuildServices();
        await CashHelpers.EnsureOpenAsync(services);
        var orderId = await CashHelpers.SentOrderAsync(services);
        await CashHelpers.PayAsync(services, orderId, "CASH", 7.10m);

        await using var restarted = fixture.BuildServices();

        var checkout = await Full<PaymentService, CheckoutView>(restarted, p => p.GetCheckoutAsync(orderId, Ct));
        Assert.True(checkout.IsPaid);
        Assert.Equal(7.10m, Assert.Single(checkout.Payments).Amount);
        Assert.Equal(0m, checkout.Balance);
    }
}
