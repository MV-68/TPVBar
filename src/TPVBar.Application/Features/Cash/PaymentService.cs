using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Printing;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Cash;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Sales;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Cash;

/// <summary>
/// Cobro de comandas y devoluciones. Cada operación es atómica: pago, saldo de la comanda, actividad de caja,
/// justificante en cola, auditoría y clave de idempotencia se confirman en una sola transacción.
/// </summary>
public sealed class PaymentService(
    IAppData data,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    AuditWriter audit,
    PrintEnqueuer printing,
    IOperationEvents events,
    TimeProvider time)
{
    public async Task<CheckoutView> GetCheckoutAsync(long orderId, CancellationToken cancellationToken)
    {
        user.Require(Permission.TakePayments);

        var order = await data.FirstOrDefaultAsync(data.NoTracking(data.Orders).Where(o => o.Id == orderId), cancellationToken)
            ?? throw new NotFoundException("La comanda no existe.");
        var methods = (await data.ToListAsync(data.NoTracking(data.PaymentMethods), cancellationToken)).ToDictionary(m => m.Id);
        var payments = await data.ToListAsync(data.NoTracking(data.Payments).Where(p => p.OrderId == orderId).OrderBy(p => p.Id), cancellationToken);
        var refunds = await data.ToListAsync(data.NoTracking(data.PaymentRefunds).Where(r => r.OrderId == orderId), cancellationToken);
        var open = await data.AnyAsync(data.CashSessions.Where(s => s.Status == CashSessionStatus.Open), cancellationToken);

        return new CheckoutView(
            order.Id,
            await TableLabelAsync(order.TableId, cancellationToken),
            order.Status,
            order.Subtotal,
            order.TaxTotal,
            order.GrandTotal,
            order.PaidTotal,
            order.Balance,
            [.. order.Lines.Where(l => l.IsActive).OrderBy(l => l.Id).Select(l => new CheckoutLine(l.ProductNameSnapshot, l.Quantity, l.LineTotal, l.Status))],
            [.. payments.Select(p => new CheckoutPayment(
                p.Id,
                methods.TryGetValue(p.PaymentMethodId, out var m) ? m.Name : "?",
                p.Amount,
                p.TenderedAmount,
                p.ChangeGiven,
                p.ExternalReference,
                p.PaidAt,
                refunds.Where(r => r.PaymentId == p.Id).Sum(r => r.Amount)))],
            [.. methods.Values.Where(m => m.IsActive).OrderBy(m => m.DisplayOrder).ThenBy(m => m.Name).Select(CashService.ToView)],
            open,
            order.Lines.Any(l => l.Status == OrderLineStatus.Pending));
    }

    /// <param name="request">Lleva una clave de idempotencia: repetirla (doble clic, reintento) no cobra dos veces.</param>
    public async Task<PayResult> PayAsync(PayRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        user.Require(Permission.TakePayments);

        if (await WasProcessedAsync(request.RequestId, cancellationToken))
        {
            var order0 = await data.FirstOrDefaultAsync(data.NoTracking(data.Orders).Where(o => o.Id == request.OrderId), cancellationToken)
                ?? throw new NotFoundException("La comanda no existe.");
            return new PayResult(0, 0, order0.Balance, order0.Status == OrderStatus.Paid, AlreadyProcessed: true, ReceiptQueued: false);
        }

        var now = time.GetUtcNow();
        var session = await data.FirstOrDefaultAsync(data.CashSessions.Where(s => s.Status == CashSessionStatus.Open), cancellationToken)
            ?? throw new DomainException("No hay una caja abierta. Un encargado debe abrirla antes de cobrar.");
        var order = await data.FirstOrDefaultAsync(data.Orders.Where(o => o.Id == request.OrderId), cancellationToken)
            ?? throw new NotFoundException("La comanda no existe.");
        var method = await data.FirstOrDefaultAsync(data.PaymentMethods.Where(m => m.Id == request.MethodId), cancellationToken)
            ?? throw new DomainException("El método de pago no existe.");

        // El importe se valida en el dominio: positivo, con 2 decimales y sin superar el saldo pendiente.
        var payment = Payment.Create(order.Id, session.Id, method, request.Amount, request.Tendered, request.Reference, user.UserId, now);
        order.RegisterPayment(request.Amount, now);
        session.RegisterActivity(now);
        data.Add(payment);

        var receiptQueued = false;
        if (order.Status == OrderStatus.Paid)
        {
            var previous = await data.ToListAsync(data.NoTracking(data.Payments).Where(p => p.OrderId == order.Id).OrderBy(p => p.Id), cancellationToken);
            var methods = (await data.ToListAsync(data.NoTracking(data.PaymentMethods), cancellationToken)).ToDictionary(m => m.Id);
            var lines = previous.Append(payment)
                .Select(p => new ReceiptPayment(methods.TryGetValue(p.PaymentMethodId, out var m) ? m.Name : "?", p.Amount, p.TenderedAmount, p.ChangeGiven))
                .ToList();
            receiptQueued = await printing.EnqueueReceiptAsync(await TableLabelAsync(order.TableId, cancellationToken), order, lines, isReprint: false, cancellationToken);
        }

        audit.Write(
            AuditActions.PaymentRegistered,
            "order",
            order.Id,
            new { method = method.Code, request.Amount, balance = order.Balance, paid = order.Status == OrderStatus.Paid, hasReference = payment.ExternalReference is not null, session = session.Id });
        await CompleteAsync(request.RequestId, "payment.pay", cancellationToken);

        events.Raise(new OperationEvent(OperationEventNames.PaymentRegistered, OrderId: order.Id, TableId: order.TableId));
        events.Raise(new OperationEvent(OperationEventNames.OrderUpdated, OrderId: order.Id, TableId: order.TableId));
        events.Raise(new OperationEvent(OperationEventNames.CashSessionChanged));
        if (order.Status == OrderStatus.Paid)
        {
            events.Raise(new OperationEvent(OperationEventNames.TableStatusChanged, OrderId: order.Id, TableId: order.TableId));
        }

        return new PayResult(payment.Id, payment.ChangeGiven, order.Balance, order.Status == OrderStatus.Paid, AlreadyProcessed: false, receiptQueued);
    }

    /// <summary>Devolución (operación compensatoria). El pago original no se modifica nunca.</summary>
    public async Task<long> RefundAsync(long paymentId, decimal amount, string reason, Guid requestId, CancellationToken cancellationToken)
    {
        user.Require(Permission.PerformRefunds);

        if (await WasProcessedAsync(requestId, cancellationToken))
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var session = await data.FirstOrDefaultAsync(data.CashSessions.Where(s => s.Status == CashSessionStatus.Open), cancellationToken)
            ?? throw new DomainException("No hay una caja abierta: la devolución se imputa a la caja actual.");
        var payment = await data.FirstOrDefaultAsync(data.NoTracking(data.Payments).Where(p => p.Id == paymentId), cancellationToken)
            ?? throw new NotFoundException("El cobro no existe.");

        var refunded = (await data.ToListAsync(data.NoTracking(data.PaymentRefunds).Where(r => r.PaymentId == paymentId), cancellationToken)).Sum(r => r.Amount);
        if (amount > payment.Amount - refunded)
        {
            throw new DomainException($"Solo se pueden devolver {payment.Amount - refunded:0.00} € de este cobro (ya devuelto: {refunded:0.00} €).");
        }

        var order = await data.FirstOrDefaultAsync(data.Orders.Where(o => o.Id == payment.OrderId), cancellationToken)
            ?? throw new NotFoundException("La comanda del cobro no existe.");

        var refund = PaymentRefund.Create(payment, session.Id, amount, reason, user.UserId, now);
        order.RegisterRefund(amount, now);
        session.RegisterActivity(now);
        data.Add(refund);

        audit.Write(AuditActions.PaymentRefunded, "payment", paymentId, new { amount, reason = reason.Trim(), orderId = order.Id, session = session.Id });
        await CompleteAsync(requestId, "payment.refund", cancellationToken);

        events.Raise(new OperationEvent(OperationEventNames.PaymentRegistered, OrderId: order.Id, TableId: order.TableId));
        events.Raise(new OperationEvent(OperationEventNames.OrderUpdated, OrderId: order.Id, TableId: order.TableId));
        events.Raise(new OperationEvent(OperationEventNames.CashSessionChanged));
        return refund.Id;
    }

    /// <summary>Reimprime el justificante de una comanda cobrada (copia nueva, auditada).</summary>
    public async Task<bool> ReprintReceiptAsync(long orderId, CancellationToken cancellationToken)
    {
        user.Require(Permission.TakePayments);

        var order = await data.FirstOrDefaultAsync(data.NoTracking(data.Orders).Where(o => o.Id == orderId), cancellationToken)
            ?? throw new NotFoundException("La comanda no existe.");
        if (order.Status != OrderStatus.Paid)
        {
            throw new DomainException("Solo se puede reimprimir el justificante de una comanda cobrada.");
        }

        var methods = (await data.ToListAsync(data.NoTracking(data.PaymentMethods), cancellationToken)).ToDictionary(m => m.Id);
        var payments = await data.ToListAsync(data.NoTracking(data.Payments).Where(p => p.OrderId == orderId).OrderBy(p => p.Id), cancellationToken);
        var lines = payments.Select(p => new ReceiptPayment(methods.TryGetValue(p.PaymentMethodId, out var m) ? m.Name : "?", p.Amount, p.TenderedAmount, p.ChangeGiven)).ToList();

        var queued = await printing.EnqueueReceiptAsync(await TableLabelAsync(order.TableId, cancellationToken), order, lines, isReprint: true, cancellationToken);
        audit.Write(AuditActions.ReceiptReprinted, "order", orderId, new { queued });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return queued;
    }

    private async Task<bool> WasProcessedAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var key = requestId.ToString("N");
        return await data.AnyAsync(data.IdempotencyRecords.Where(r => r.Key == key), cancellationToken);
    }

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
}
