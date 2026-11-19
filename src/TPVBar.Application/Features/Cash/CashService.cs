using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Printing;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Cash;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Sales;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Cash;

/// <summary>Apertura, movimientos y cierre de caja. El efectivo esperado lo calcula SIEMPRE el servidor.</summary>
public sealed class CashService(
    IAppData data,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    AuditWriter audit,
    PrintEnqueuer printing,
    IOperationEvents events,
    TimeProvider time)
{
    public async Task<CashStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        RequireCashAccess();
        var session = await FindOpenAsync(tracked: false, cancellationToken);
        return new CashStatus(session is not null, session?.Id, session?.OpenedAt);
    }

    /// <summary>La sesión abierta con sus totales, o <see langword="null"/> si la caja está cerrada.</summary>
    public async Task<CashSessionView?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        RequireCashAccess();
        var session = await FindOpenAsync(tracked: false, cancellationToken);
        return session is null ? null : await BuildViewAsync(session, cancellationToken);
    }

    public async Task<CashSessionView?> GetSessionAsync(long sessionId, CancellationToken cancellationToken)
    {
        user.Require(Permission.OpenCloseCash);
        var session = await data.FirstOrDefaultAsync(data.NoTracking(data.CashSessions).Where(s => s.Id == sessionId), cancellationToken);
        return session is null ? null : await BuildViewAsync(session, cancellationToken);
    }

    public async Task<IReadOnlyList<PaymentMethodView>> ListMethodsAsync(CancellationToken cancellationToken)
    {
        RequireCashAccess();
        var methods = await data.ToListAsync(data.NoTracking(data.PaymentMethods).Where(m => m.IsActive).OrderBy(m => m.DisplayOrder).ThenBy(m => m.Name), cancellationToken);
        return [.. methods.Select(ToView)];
    }

    public async Task<long> OpenAsync(decimal openingAmount, CancellationToken cancellationToken)
    {
        user.Require(Permission.OpenCloseCash);

        if (await FindOpenAsync(tracked: false, cancellationToken) is not null)
        {
            throw new DomainException("Ya hay una caja abierta.");
        }

        var session = CashSession.Open(user.UserId, openingAmount, time.GetUtcNow());
        data.Add(session);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateEntryException ex)
        {
            // Dos puestos abriendo a la vez: el índice único deja pasar solo una.
            throw new DomainException("Ya hay una caja abierta.", ex);
        }

        audit.Write(AuditActions.CashOpened, "cash_session", session.Id, new { openingAmount });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        events.Raise(new OperationEvent(OperationEventNames.CashSessionChanged));
        return session.Id;
    }

    public async Task AddMovementAsync(CashMovementType type, decimal amount, string concept, CancellationToken cancellationToken)
    {
        user.Require(Permission.OpenCloseCash);
        var session = await RequireOpenAsync(cancellationToken);
        var now = time.GetUtcNow();

        session.RegisterActivity(now);
        data.Add(CashMovement.Create(session.Id, type, amount, concept, user.UserId, now));

        audit.Write(AuditActions.CashMovementRegistered, "cash_session", session.Id, new { type = type.ToString(), amount, concept = concept.Trim() });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        events.Raise(new OperationEvent(OperationEventNames.CashSessionChanged));
    }

    /// <summary>
    /// Cierra la caja con el efectivo contado. Es a ciegas: el esperado se calcula aquí y solo se revela en el
    /// informe de cierre. No se puede cerrar con comandas a medio cobrar (hay dinero registrado sin saldar).
    /// </summary>
    public async Task<CloseResult> CloseAsync(decimal countedCash, string? notes, CancellationToken cancellationToken)
    {
        user.Require(Permission.OpenCloseCash);
        var session = await RequireOpenAsync(cancellationToken);
        var now = time.GetUtcNow();

        var partial = await data.ToListAsync(
            data.NoTracking(data.Orders).Where(o => o.Status == OrderStatus.PendingPayment && o.PaidTotal > 0).Select(o => o.Id),
            cancellationToken);
        if (partial.Count > 0)
        {
            throw new DomainException(
                $"No se puede cerrar la caja: hay {partial.Count} comanda(s) con un cobro parcial sin completar (nº {string.Join(", ", partial.Take(5))}). Termine de cobrarlas o devuelva lo cobrado.");
        }

        var ledger = await LoadLedgerAsync(session.Id, cancellationToken);
        var expected = ledger.ExpectedCash(session.OpeningAmount);

        session.RegisterActivity(now); // serializa con cobros simultáneos (ver CashSession.RegisterActivity)
        session.Close(user.UserId, countedCash, expected, notes, now);

        audit.Write(AuditActions.CashClosed, "cash_session", session.Id, new { expected, counted = countedCash, difference = session.Difference });
        var view = await BuildViewAsync(session, ledger, cancellationToken);
        var queued = await printing.EnqueueCashCloseAsync(view, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        events.Raise(new OperationEvent(OperationEventNames.CashSessionChanged));
        return new CloseResult(view, queued);
    }

    /// <summary>Cobros de la sesión abierta, con lo ya devuelto de cada uno (para ofrecer devoluciones).</summary>
    public async Task<IReadOnlyList<PaymentRow>> ListSessionPaymentsAsync(CancellationToken cancellationToken)
    {
        RequireCashAccess();
        var session = await FindOpenAsync(tracked: false, cancellationToken);
        if (session is null)
        {
            return [];
        }

        var ledger = await LoadLedgerAsync(session.Id, cancellationToken);
        var orderIds = ledger.Payments.Select(p => p.OrderId).Distinct().ToList();
        var orders = await data.ToListAsync(data.NoTracking(data.Orders).Where(o => orderIds.Contains(o.Id)).Select(o => new { o.Id, o.TableId }), cancellationToken);
        var tables = (await data.ToListAsync(data.NoTracking(data.Tables), cancellationToken)).ToDictionary(t => t.Id, t => t.Name);

        return [.. ledger.Payments.OrderByDescending(p => p.Id).Select(p => new PaymentRow(
            p.Id,
            p.OrderId,
            tables.GetValueOrDefault(orders.FirstOrDefault(o => o.Id == p.OrderId)?.TableId ?? 0, "?"),
            ledger.Methods.TryGetValue(p.PaymentMethodId, out var m) ? m.Name : "?",
            p.Amount,
            ledger.Refunds.Where(r => r.PaymentId == p.Id).Sum(r => r.Amount),
            p.PaidAt,
            p.ExternalReference))];
    }

    // ----- Auxiliares -----

    private void RequireCashAccess()
    {
        if (!user.Has(Permission.TakePayments) && !user.Has(Permission.OpenCloseCash))
        {
            throw new ForbiddenException();
        }
    }

    internal async Task<CashSession?> FindOpenAsync(bool tracked, CancellationToken cancellationToken)
    {
        var query = tracked ? data.CashSessions : data.NoTracking(data.CashSessions);
        return await data.FirstOrDefaultAsync(query.Where(s => s.Status == CashSessionStatus.Open), cancellationToken);
    }

    private async Task<CashSession> RequireOpenAsync(CancellationToken cancellationToken) =>
        await FindOpenAsync(tracked: true, cancellationToken) ?? throw new DomainException("No hay una caja abierta.");

    private async Task<CashSessionView> BuildViewAsync(CashSession session, CancellationToken cancellationToken) =>
        await BuildViewAsync(session, await LoadLedgerAsync(session.Id, cancellationToken), cancellationToken);

    private async Task<CashSessionView> BuildViewAsync(CashSession session, CashLedger ledger, CancellationToken cancellationToken)
    {
        var userIds = new[] { session.OpenedBy, session.ClosedBy ?? Guid.Empty }.Concat(ledger.Movements.Select(m => m.CreatedBy)).Distinct().ToList();
        var names = (await data.ToListAsync(data.NoTracking(data.Users).Where(u => userIds.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName }), cancellationToken))
            .ToDictionary(u => u.Id, u => u.DisplayName);
        string Name(Guid id) => names.GetValueOrDefault(id, "?");

        var methodIds = ledger.Payments.Select(p => p.PaymentMethodId).Concat(ledger.Refunds.Select(r => r.PaymentMethodId)).Distinct();
        var totals = methodIds
            .Select(id => ledger.Methods.TryGetValue(id, out var m) ? m : null)
            .Where(m => m is not null)
            .OrderBy(m => m!.DisplayOrder)
            .Select(m => new MethodTotal(
                m!.Id,
                m.Name,
                m.AffectsCashDrawer,
                ledger.Payments.Where(p => p.PaymentMethodId == m.Id).Sum(p => p.Amount),
                ledger.Refunds.Where(r => r.PaymentMethodId == m.Id).Sum(r => r.Amount)))
            .ToList();

        return new CashSessionView(
            session.Id,
            session.Status,
            session.OpenedAt,
            Name(session.OpenedBy),
            session.OpeningAmount,
            ledger.Payments.Count,
            ledger.Payments.Select(p => p.OrderId).Distinct().Count(),
            totals,
            ledger.Movements.Where(m => m.Type == CashMovementType.CashIn).Sum(m => m.Amount),
            ledger.Movements.Where(m => m.Type == CashMovementType.CashOut).Sum(m => m.Amount),
            [.. ledger.Movements.OrderBy(m => m.Id).Select(m => new MovementView(m.Id, m.Type, m.Amount, m.Concept, m.CreatedAt, Name(m.CreatedBy)))],
            session.ClosedAt,
            session.ClosedBy is { } closer ? Name(closer) : null,
            session.Status == CashSessionStatus.Closed ? session.ExpectedCash : null,
            session.CountedCash,
            session.Difference,
            session.Notes);
    }

    internal async Task<CashLedger> LoadLedgerAsync(long sessionId, CancellationToken cancellationToken)
    {
        var payments = await data.ToListAsync(data.NoTracking(data.Payments).Where(p => p.CashSessionId == sessionId), cancellationToken);
        var refunds = await data.ToListAsync(data.NoTracking(data.PaymentRefunds).Where(r => r.CashSessionId == sessionId), cancellationToken);
        var movements = await data.ToListAsync(data.NoTracking(data.CashMovements).Where(m => m.CashSessionId == sessionId), cancellationToken);
        var methods = (await data.ToListAsync(data.NoTracking(data.PaymentMethods), cancellationToken)).ToDictionary(m => m.Id);
        return new CashLedger(payments, refunds, movements, methods);
    }

    internal static PaymentMethodView ToView(PaymentMethod m) =>
        new(m.Id, m.Code, m.Name, m.RequiresReference, m.AffectsCashDrawer, m.IsActive, m.DisplayOrder);
}

/// <summary>Contabilidad de una sesión de caja. Reglas del efectivo esperado en un solo lugar.</summary>
internal sealed class CashLedger(
    IReadOnlyList<Payment> payments,
    IReadOnlyList<PaymentRefund> refunds,
    IReadOnlyList<CashMovement> movements,
    IReadOnlyDictionary<int, PaymentMethod> methods)
{
    public IReadOnlyList<Payment> Payments { get; } = payments;

    public IReadOnlyList<PaymentRefund> Refunds { get; } = refunds;

    public IReadOnlyList<CashMovement> Movements { get; } = movements;

    public IReadOnlyDictionary<int, PaymentMethod> Methods { get; } = methods;

    /// <summary>Fondo inicial + cobros en efectivo − devoluciones en efectivo + entradas − salidas.</summary>
    public decimal ExpectedCash(decimal openingAmount)
    {
        bool IsCash(int methodId) => Methods.TryGetValue(methodId, out var m) && m.AffectsCashDrawer;

        return openingAmount
               + Payments.Where(p => IsCash(p.PaymentMethodId)).Sum(p => p.Amount)
               - Refunds.Where(r => IsCash(r.PaymentMethodId)).Sum(r => r.Amount)
               + Movements.Where(m => m.Type == CashMovementType.CashIn).Sum(m => m.Amount)
               - Movements.Where(m => m.Type == CashMovementType.CashOut).Sum(m => m.Amount);
    }
}
