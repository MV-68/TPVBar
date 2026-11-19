using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Inventory;
using TPVBar.Application.Features.Printing;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Sales;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Orders;

/// <summary>
/// Casos de uso de la comanda. Cada llamada es una operación atómica: cambios, auditoría y marca de
/// idempotencia se confirman en una sola transacción. Un conflicto de concurrencia (dos terminales
/// editando la misma comanda) lo reintenta el host con una operación nueva.
/// </summary>
public sealed class OrderService(
    IAppData data,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    AuditWriter audit,
    PrintEnqueuer printing,
    StockLedger stock,
    IOperationEvents events,
    TimeProvider time)
{
    public async Task<OrderView> GetAsync(long orderId, CancellationToken cancellationToken)
    {
        user.Require(Permission.TakeOrders);
        var order = await data.FirstOrDefaultAsync(data.NoTracking(data.Orders).Where(o => o.Id == orderId), cancellationToken)
            ?? throw new NotFoundException("La comanda no existe.");
        return await ToViewAsync(order, cancellationToken);
    }

    /// <param name="requestId">Identificador de esta pulsación: repetirlo (doble clic, reintento) no añade otra vez.</param>
    public async Task<OrderView> AddLineAsync(
        long orderId,
        int productId,
        decimal quantity,
        string? notes,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        user.Require(Permission.TakeOrders);

        if (await WasProcessedAsync(requestId, cancellationToken))
        {
            return await GetAsync(orderId, cancellationToken);
        }

        var order = await LoadOrderAsync(orderId, cancellationToken);

        // Precio, impuesto y destino se leen SIEMPRE de la base de datos, nunca del navegador.
        var product = await data.FirstOrDefaultAsync(data.Products.Where(p => p.Id == productId), cancellationToken);
        if (product is null || !product.IsActive)
        {
            throw new DomainException("El producto no está disponible.");
        }

        var category = await data.FirstOrDefaultAsync(data.Categories.Where(c => c.Id == product.CategoryId), cancellationToken);
        if (category is not { IsActive: true })
        {
            throw new DomainException("La categoría del producto no está disponible.");
        }

        var taxRate = await data.FirstOrDefaultAsync(data.TaxRates.Where(t => t.Id == product.TaxRateId), cancellationToken);
        if (taxRate is null || !taxRate.IsValidOn(BusinessTime.Today(time)))
        {
            throw new DomainException("El producto no tiene un tipo impositivo vigente. Avise al encargado.");
        }

        var now = time.GetUtcNow();
        order.AddLine(product, taxRate.Rate, quantity, notes, user.UserId, now);

        audit.Write(AuditActions.OrderLineAdded, "order", order.Id, new { productId, product.Name, quantity });
        await CompleteAsync(requestId, "order.add_line", cancellationToken);

        events.Raise(new OperationEvent(OperationEventNames.OrderLineAdded, OrderId: order.Id, TableId: order.TableId));
        events.Raise(new OperationEvent(OperationEventNames.TableStatusChanged, TableId: order.TableId));
        return await ToViewAsync(order, cancellationToken);
    }

    public async Task<OrderView> ChangeQuantityAsync(long orderId, long lineId, decimal quantity, CancellationToken cancellationToken)
    {
        user.Require(Permission.TakeOrders);
        var order = await LoadOrderAsync(orderId, cancellationToken);

        order.ChangeQuantity(lineId, quantity, time.GetUtcNow());

        audit.Write(AuditActions.OrderLineQuantityChanged, "order", order.Id, new { lineId, quantity });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        RaiseOrderUpdated(order);
        return await ToViewAsync(order, cancellationToken);
    }

    public async Task<OrderView> RemoveLineAsync(long orderId, long lineId, CancellationToken cancellationToken)
    {
        user.Require(Permission.TakeOrders);
        var order = await LoadOrderAsync(orderId, cancellationToken);

        var line = order.RemoveLine(lineId, time.GetUtcNow());

        audit.Write(AuditActions.OrderLineRemoved, "order", order.Id, new { lineId, line.ProductNameSnapshot, line.Quantity });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        RaiseOrderUpdated(order);
        return await ToViewAsync(order, cancellationToken);
    }

    /// <summary>Anula una línea ya enviada. Exige permiso, motivo y deja auditoría.</summary>
    public async Task<OrderView> VoidLineAsync(long orderId, long lineId, string reason, CancellationToken cancellationToken)
    {
        user.Require(Permission.VoidLines);
        var order = await LoadOrderAsync(orderId, cancellationToken);

        var now = time.GetUtcNow();
        var line = order.VoidLine(lineId, reason, user.UserId, now);

        // Anular una línea enviada repone el stock que se descontó al enviarla.
        await stock.ReturnForVoidAsync(line, cancellationToken);

        // La anulación llega también a cocina: el ticket muestra la línea como ANULADA y se imprime un aviso.
        var tickets = await data.ToListAsync(data.KitchenTickets.Where(t => t.OrderId == orderId), cancellationToken);
        var affected = tickets.Where(t => t.CancelLine(lineId, user.UserId, now)).ToList();
        if (affected.Count > 0)
        {
            await printing.EnqueueKitchenVoidAsync(await TableLabelAsync(order.TableId, cancellationToken), line.Station, line, order.Id, cancellationToken);
        }

        audit.Write(AuditActions.OrderLineVoided, "order", order.Id, new { lineId, line.ProductNameSnapshot, line.Quantity, line.LineTotal, reason = reason.Trim() });
        await unitOfWork.SaveChangesAsync(cancellationToken);

        foreach (var ticket in affected)
        {
            events.Raise(new OperationEvent(OperationEventNames.KitchenTicketStatusChanged, OrderId: order.Id, TicketId: ticket.Id, Station: ticket.Station));
        }

        RaiseOrderUpdated(order);
        return await ToViewAsync(order, cancellationToken);
    }

    /// <summary>
    /// Envía las líneas pendientes: crea un ticket de preparación por destino (cocina/barra) y las marca
    /// como enviadas, todo en una transacción. La visualización en pantalla de cocina y la impresión
    /// llegan en la Fase 3.
    /// </summary>
    public async Task<SendResult> SendToKitchenAsync(long orderId, Guid requestId, CancellationToken cancellationToken)
    {
        user.Require(Permission.TakeOrders);

        if (await WasProcessedAsync(requestId, cancellationToken))
        {
            return new SendResult([], AlreadyProcessed: true);
        }

        var order = await LoadOrderAsync(orderId, cancellationToken);
        var label = await TableLabelAsync(order.TableId, cancellationToken);
        var now = time.GetUtcNow();

        var sent = order.MarkPendingLinesSent(now);

        // Política de stock: se descuenta al enviar. Si no alcanza, falla todo el envío y no se guarda nada.
        await stock.DeductForSendAsync(sent, cancellationToken);

        var summaries = new List<TicketSummary>();
        var tickets = new List<KitchenTicket>();
        var unprinted = new List<PreparationStation>();
        foreach (var group in sent.GroupBy(l => l.Station).OrderBy(g => g.Key))
        {
            var ticket = KitchenTicket.Create(order.Id, group.Key, label, group, user.UserId, now);
            data.Add(ticket);
            tickets.Add(ticket);
            summaries.Add(new TicketSummary(group.Key, group.Count()));

            // El trabajo de impresión se guarda en la MISMA transacción que el envío.
            if (!await printing.EnqueueKitchenTicketAsync(label, group.Key, group, requestId, order.Id, cancellationToken))
            {
                unprinted.Add(group.Key);
            }
        }

        audit.Write(AuditActions.OrderSentToKitchen, "order", order.Id, new { lines = sent.Count, tickets = summaries.Count, unprinted });
        await CompleteAsync(requestId, "order.send", cancellationToken);

        // Los avisos se emiten con los identificadores ya asignados y solo se publican si todo se confirmó.
        events.Raise(new OperationEvent(OperationEventNames.OrderSentToKitchen, OrderId: order.Id, TableId: order.TableId));
        foreach (var ticket in tickets)
        {
            events.Raise(new OperationEvent(OperationEventNames.KitchenTicketCreated, OrderId: order.Id, TicketId: ticket.Id, Station: ticket.Station));
        }

        RaiseOrderUpdated(order);
        return new SendResult(summaries, AlreadyProcessed: false, unprinted);
    }

    public async Task<OrderView> RequestBillAsync(long orderId, CancellationToken cancellationToken)
    {
        user.Require(Permission.TakeOrders);
        var order = await LoadOrderAsync(orderId, cancellationToken);

        order.RequestBill(time.GetUtcNow());

        // Pre-cuenta para el cliente (no fiscal) en la impresora de tickets, si existe.
        await printing.EnqueuePreBillAsync(await TableLabelAsync(order.TableId, cancellationToken), order, cancellationToken);

        audit.Write(AuditActions.OrderBillRequested, "order", order.Id, new { order.GrandTotal });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        RaiseOrderUpdated(order);
        return await ToViewAsync(order, cancellationToken);
    }

    public async Task<OrderView> ReopenAsync(long orderId, CancellationToken cancellationToken)
    {
        user.Require(Permission.ReopenTables);
        var order = await LoadOrderAsync(orderId, cancellationToken);

        order.Reopen(time.GetUtcNow());

        audit.Write(AuditActions.OrderReopened, "order", order.Id);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        RaiseOrderUpdated(order);
        return await ToViewAsync(order, cancellationToken);
    }

    public async Task CancelAsync(long orderId, CancellationToken cancellationToken)
    {
        user.Require(Permission.TakeOrders);
        var order = await LoadOrderAsync(orderId, cancellationToken);

        order.Cancel(time.GetUtcNow());

        audit.Write(AuditActions.OrderCancelled, "order", order.Id);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        RaiseOrderUpdated(order);
        events.Raise(new OperationEvent(OperationEventNames.TableStatusChanged, TableId: order.TableId));
    }

    private void RaiseOrderUpdated(Order order) =>
        events.Raise(new OperationEvent(OperationEventNames.OrderUpdated, OrderId: order.Id, TableId: order.TableId));

    public async Task<OrderView> SetGuestsAsync(long orderId, int guests, CancellationToken cancellationToken)
    {
        user.Require(Permission.TakeOrders);
        var order = await LoadOrderAsync(orderId, cancellationToken);

        order.SetGuestCount(guests, time.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ToViewAsync(order, cancellationToken);
    }

    private async Task<Order> LoadOrderAsync(long orderId, CancellationToken cancellationToken) =>
        await data.FirstOrDefaultAsync(data.Orders.Where(o => o.Id == orderId), cancellationToken)
            ?? throw new NotFoundException("La comanda no existe.");

    private async Task<bool> WasProcessedAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var key = requestId.ToString("N");
        return await data.AnyAsync(data.IdempotencyRecords.Where(r => r.Key == key), cancellationToken);
    }

    /// <summary>Guarda los cambios junto con la marca de idempotencia. Una repetición simultánea falla por clave duplicada.</summary>
    private async Task CompleteAsync(Guid requestId, string operation, CancellationToken cancellationToken)
    {
        data.Add(IdempotencyRecord.Create(requestId, operation, time.GetUtcNow()));
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateEntryException ex)
        {
            throw new DomainException("La operación ya se estaba procesando. Compruebe la comanda.", ex);
        }
    }

    private async Task<string> TableLabelAsync(int tableId, CancellationToken cancellationToken)
    {
        var table = await data.FirstOrDefaultAsync(data.NoTracking(data.Tables).Where(t => t.Id == tableId), cancellationToken);
        if (table is null)
        {
            return string.Empty;
        }

        var zone = await data.FirstOrDefaultAsync(data.NoTracking(data.Zones).Where(z => z.Id == table.ZoneId), cancellationToken);
        return zone is null ? table.Name : $"{zone.Name} · {table.Name}";
    }

    private async Task<OrderView> ToViewAsync(Order order, CancellationToken cancellationToken)
    {
        var label = await TableLabelAsync(order.TableId, cancellationToken);
        var lines = order.Lines
            .OrderBy(l => l.Id)
            .Select(l => new OrderLineView(l.Id, l.ProductNameSnapshot, l.Quantity, l.UnitPrice, l.LineTotal, l.Notes, l.Status, l.Station, l.VoidReason))
            .ToList();

        return new OrderView(order.Id, order.TableId, label, order.Status, order.OpenedAt, order.GuestCount, order.Subtotal, order.TaxTotal, order.GrandTotal, lines, order.PaidTotal);
    }
}
