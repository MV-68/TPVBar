using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPVBar.Application.Features.Cash;
using TPVBar.Application.Features.Orders;
using TPVBar.Domain.Cash;
using TPVBar.Domain.Sales;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Integration.Tests;

/// <summary>
/// La caja es única en todo el sistema y la base de datos de pruebas se comparte: estas utilidades dejan la caja
/// en un estado conocido (cerrando antes cualquier sesión abierta y saldando cobros parciales de otras pruebas).
/// </summary>
internal static class CashHelpers
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Task<T> Full<TService, T>(IServiceProvider sp, Func<TService, Task<T>> op)
        where TService : notnull => SalesHelpers.RunAsync(sp, SalesHelpers.Full, op);

    public static async Task<int> MethodIdAsync(IServiceProvider sp, string code)
    {
        await using var scope = sp.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().PaymentMethods.AsNoTracking()
            .Where(m => m.Code == code).Select(m => m.Id).SingleAsync(Ct);
    }

    /// <summary>Cierra la sesión abierta (si la hay), terminando antes de cobrar las comandas a medias.</summary>
    public static async Task CloseAnyOpenAsync(IServiceProvider sp)
    {
        var current = await Full<CashService, CashSessionView?>(sp, c => c.GetCurrentAsync(Ct));
        if (current is null)
        {
            return;
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var partial = await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().Orders.AsNoTracking()
                .Where(o => o.Status == OrderStatus.PendingPayment && o.PaidTotal > 0)
                .Select(o => new { o.Id, o.GrandTotal, o.PaidTotal }).ToListAsync(Ct);
            var cash = await MethodIdAsync(sp, "CASH");
            foreach (var order in partial)
            {
                await Full<PaymentService, PayResult>(sp, p => p.PayAsync(new PayRequest(order.Id, cash, order.GrandTotal - order.PaidTotal, null, null, Guid.NewGuid()), Ct));
            }
        }

        await Full<CashService, CloseResult>(sp, c => c.CloseAsync(0m, "cierre de limpieza de pruebas", Ct));
    }

    /// <summary>Deja una sesión nueva recién abierta con el fondo indicado.</summary>
    public static async Task<long> FreshSessionAsync(IServiceProvider sp, decimal opening)
    {
        await CloseAnyOpenAsync(sp);
        return await Full<CashService, long>(sp, c => c.OpenAsync(opening, Ct));
    }

    /// <summary>Reutiliza la sesión abierta o abre una con fondo cero.</summary>
    public static async Task EnsureOpenAsync(IServiceProvider sp)
    {
        var status = await Full<CashService, CashStatus>(sp, c => c.GetStatusAsync(Ct));
        if (!status.IsOpen)
        {
            await Full<CashService, long>(sp, c => c.OpenAsync(0m, Ct));
        }
    }

    /// <summary>Comanda de 7,10 € (2 cañas + 1 tortilla) enviada y lista para cobrar.</summary>
    public static async Task<long> SentOrderAsync(IServiceProvider sp)
    {
        var world = await SalesHelpers.CreateWorldAsync(sp);
        var orderId = await Full<FloorService, long>(sp, f => f.OpenTableAsync(world.TableId, 2, Ct));
        await Full<OrderService, OrderView>(sp, o => o.AddLineAsync(orderId, world.CanaId, 2, null, Guid.NewGuid(), Ct));
        await Full<OrderService, OrderView>(sp, o => o.AddLineAsync(orderId, world.TortillaId, 1, null, Guid.NewGuid(), Ct));
        await Full<OrderService, SendResult>(sp, o => o.SendToKitchenAsync(orderId, Guid.NewGuid(), Ct));
        return orderId;
    }

    public static async Task<PayResult> PayAsync(IServiceProvider sp, long orderId, string method, decimal amount, decimal? tendered = null, Guid? key = null)
    {
        var methodId = await MethodIdAsync(sp, method);
        return await Full<PaymentService, PayResult>(sp, p => p.PayAsync(new PayRequest(orderId, methodId, amount, tendered, null, key ?? Guid.NewGuid()), Ct));
    }

    public static async Task<List<Payment>> PaymentsOfAsync(IServiceProvider sp, long orderId)
    {
        await using var scope = sp.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().Payments.AsNoTracking().Where(p => p.OrderId == orderId).ToListAsync(Ct);
    }

    public static async Task<Order> OrderAsync(IServiceProvider sp, long orderId)
    {
        await using var scope = sp.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, Ct);
    }
}
