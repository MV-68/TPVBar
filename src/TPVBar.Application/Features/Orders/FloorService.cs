using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Sales;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Orders;

/// <summary>Plano de mesas y apertura de comandas.</summary>
public sealed class FloorService(
    IAppData data,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    AuditWriter audit,
    IOperationEvents events,
    TimeProvider time)
{
    internal static readonly OrderStatus[] LiveStatuses = [OrderStatus.Open, OrderStatus.PendingPayment];

    public async Task<FloorView> GetFloorAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.TakeOrders);

        var zones = await data.ToListAsync(data.Zones.Where(z => z.IsActive).OrderBy(z => z.DisplayOrder).ThenBy(z => z.Name), cancellationToken);
        var tables = await data.ToListAsync(data.Tables.Where(t => t.IsActive).OrderBy(t => t.DisplayOrder).ThenBy(t => t.Name), cancellationToken);
        var live = await data.ToListAsync(
            data.NoTracking(data.Orders).Where(o => LiveStatuses.Contains(o.Status))
                .Select(o => new { o.Id, o.TableId, o.OpenedAt, o.GrandTotal, o.GuestCount, o.Status }),
            cancellationToken);

        var byTable = live.ToDictionary(o => o.TableId);

        var views = zones.Select(zone => new ZoneView(
            zone.Id,
            zone.Name,
            tables.Where(t => t.ZoneId == zone.Id).Select(t =>
            {
                if (!byTable.TryGetValue(t.Id, out var order))
                {
                    return new TableCardView(t.Id, t.Name, t.Capacity, TableStatus.Free, null, null, null, null);
                }

                var status = order.Status == OrderStatus.PendingPayment ? TableStatus.PendingPayment : TableStatus.Occupied;
                return new TableCardView(t.Id, t.Name, t.Capacity, status, order.Id, order.OpenedAt, order.GrandTotal, order.GuestCount);
            }).ToList())).ToList();

        return new FloorView(views);
    }

    /// <summary>
    /// Abre la comanda de la mesa o devuelve la que ya está abierta. Si dos camareros abren la misma mesa
    /// a la vez, el índice único de la base de datos deja pasar solo una y el otro recibe la misma comanda.
    /// </summary>
    public async Task<long> OpenTableAsync(int tableId, int guests, CancellationToken cancellationToken)
    {
        user.Require(Permission.TakeOrders);

        var table = await data.FirstOrDefaultAsync(data.Tables.Where(t => t.Id == tableId), cancellationToken)
            ?? throw new NotFoundException("La mesa no existe.");
        if (!table.IsActive)
        {
            throw new DomainException("La mesa está desactivada.");
        }

        var existing = await FindLiveOrderIdAsync(tableId, cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var order = Order.Open(tableId, user.UserId, Math.Max(guests, 1), time.GetUtcNow());
        data.Add(order);

        // La auditoría va en la misma transacción que la comanda (por eso referencia la mesa, no el id aún sin asignar).
        audit.Write(AuditActions.OrderOpened, "table", tableId, new { guests = order.GuestCount });

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateEntryException)
        {
            // Otra terminal abrió la mesa en el mismo instante: nos unimos a su comanda.
            return await FindLiveOrderIdAsync(tableId, cancellationToken)
                ?? throw new DomainException("No se ha podido abrir la mesa. Inténtelo de nuevo.");
        }

        events.Raise(new OperationEvent(OperationEventNames.TableStatusChanged, OrderId: order.Id, TableId: tableId));
        return order.Id;
    }

    private async Task<long?> FindLiveOrderIdAsync(int tableId, CancellationToken cancellationToken)
    {
        var ids = await data.ToListAsync(
            data.NoTracking(data.Orders).Where(o => o.TableId == tableId && LiveStatuses.Contains(o.Status)).Select(o => o.Id),
            cancellationToken);
        return ids.Count == 0 ? null : ids[0];
    }
}
