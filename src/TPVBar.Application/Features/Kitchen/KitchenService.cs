using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Sales;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Kitchen;

public sealed record KitchenLineView(long Id, decimal Quantity, string Name, string? Notes, bool IsCancelled);

public sealed record KitchenTicketView(
    long Id,
    long OrderId,
    PreparationStation Station,
    string TableLabel,
    KitchenTicketStatus Status,
    DateTimeOffset SentAt,
    IReadOnlyList<KitchenLineView> Lines);

/// <summary>Pantalla de cocina (KDS): tickets vivos y avance de su estado de preparación.</summary>
public sealed class KitchenService(
    IAppData data,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    AuditWriter audit,
    IOperationEvents events,
    TimeProvider time)
{
    public async Task<IReadOnlyList<KitchenTicketView>> GetBoardAsync(PreparationStation? station, CancellationToken cancellationToken)
    {
        user.Require(Permission.UseKitchen);

        var tickets = await data.ToListAsync(
            data.NoTracking(data.KitchenTickets)
                .Where(t => t.Status != KitchenTicketStatus.Delivered && t.Status != KitchenTicketStatus.Cancelled)
                .Where(t => station == null || t.Station == station)
                .OrderBy(t => t.SentAt),
            cancellationToken);

        return tickets.Select(t => new KitchenTicketView(
            t.Id,
            t.OrderId,
            t.Station,
            t.TableLabel,
            t.Status,
            t.SentAt,
            t.Lines.OrderBy(l => l.Id).Select(l => new KitchenLineView(l.Id, l.Quantity, l.ProductNameSnapshot, l.Notes, l.Status == KitchenTicketStatus.Cancelled)).ToList())).ToList();
    }

    /// <summary>
    /// Avanza un ticket y refleja el estado en las líneas de la comanda. Repetir el mismo cambio (doble
    /// pulsación, dos pantallas de cocina) no hace nada.
    /// </summary>
    public async Task AdvanceAsync(long ticketId, KitchenTicketStatus newStatus, CancellationToken cancellationToken)
    {
        user.Require(Permission.UseKitchen);

        var ticket = await data.FirstOrDefaultAsync(data.KitchenTickets.Where(t => t.Id == ticketId), cancellationToken)
            ?? throw new NotFoundException("El ticket no existe.");

        if (ticket.Status == newStatus)
        {
            return;
        }

        var order = await data.FirstOrDefaultAsync(data.Orders.Where(o => o.Id == ticket.OrderId), cancellationToken)
            ?? throw new NotFoundException("La comanda del ticket no existe.");

        var now = time.GetUtcNow();
        var previous = ticket.Status;
        ticket.Advance(newStatus, user.UserId, now);

        var lineStatus = newStatus switch
        {
            KitchenTicketStatus.Preparing => OrderLineStatus.Preparing,
            KitchenTicketStatus.Ready => OrderLineStatus.Ready,
            KitchenTicketStatus.Delivered => OrderLineStatus.Delivered,
            _ => throw new DomainException("El estado solicitado no es válido."),
        };
        order.SetLinesPreparationStatus(
            [.. ticket.Lines.Where(l => l.Status != KitchenTicketStatus.Cancelled).Select(l => l.OrderLineId)],
            lineStatus,
            now);

        audit.Write(AuditActions.KitchenTicketAdvanced, "kitchen_ticket", ticket.Id, new { from = previous.ToString(), to = newStatus.ToString(), orderId = order.Id });
        await unitOfWork.SaveChangesAsync(cancellationToken);

        events.Raise(new OperationEvent(OperationEventNames.KitchenTicketStatusChanged, OrderId: order.Id, TicketId: ticket.Id, Station: ticket.Station));
        events.Raise(new OperationEvent(OperationEventNames.OrderUpdated, OrderId: order.Id, TableId: order.TableId));
    }
}
