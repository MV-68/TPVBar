using TPVBar.Domain.Cash;
using TPVBar.Domain.Sales;

namespace TPVBar.Application.Features.Cash;

public sealed record PaymentMethodView(int Id, string Code, string Name, bool RequiresReference, bool IsCash, bool IsActive, int DisplayOrder);

public sealed record MethodTotal(int MethodId, string Name, bool IsCash, decimal Payments, decimal Refunds)
{
    public decimal Net => Payments - Refunds;
}

public sealed record MovementView(long Id, CashMovementType Type, decimal Amount, string Concept, DateTimeOffset CreatedAt, string By);

/// <summary>
/// Estado de una sesión de caja. Mientras está abierta NO incluye el efectivo esperado: el recuento es a ciegas
/// para no sesgarlo. Al cerrar se calcula y se muestran esperado, contado y diferencia.
/// </summary>
public sealed record CashSessionView(
    long Id,
    CashSessionStatus Status,
    DateTimeOffset OpenedAt,
    string OpenedBy,
    decimal OpeningAmount,
    int PaymentsCount,
    int OrdersCount,
    IReadOnlyList<MethodTotal> Methods,
    decimal CashIn,
    decimal CashOut,
    IReadOnlyList<MovementView> Movements,
    DateTimeOffset? ClosedAt,
    string? ClosedBy,
    decimal? ExpectedCash,
    decimal? CountedCash,
    decimal? Difference,
    string? Notes)
{
    public decimal TotalPayments => Methods.Sum(m => m.Payments);

    public decimal TotalRefunds => Methods.Sum(m => m.Refunds);

    public decimal NetTotal => TotalPayments - TotalRefunds;
}

public sealed record CashStatus(bool IsOpen, long? SessionId, DateTimeOffset? OpenedAt);

public sealed record CloseResult(CashSessionView Report, bool ReportPrinted);

public sealed record PaymentRow(
    long Id,
    long OrderId,
    string TableLabel,
    string Method,
    decimal Amount,
    decimal Refunded,
    DateTimeOffset PaidAt,
    string? Reference)
{
    public decimal Refundable => Amount - Refunded;
}

public sealed record CheckoutPayment(
    long Id,
    string Method,
    decimal Amount,
    decimal? Tendered,
    decimal Change,
    string? Reference,
    DateTimeOffset PaidAt,
    decimal Refunded);

public sealed record CheckoutLine(string Name, decimal Quantity, decimal LineTotal, OrderLineStatus Status);

public sealed record CheckoutView(
    long OrderId,
    string TableLabel,
    OrderStatus Status,
    decimal Subtotal,
    decimal TaxTotal,
    decimal GrandTotal,
    decimal PaidTotal,
    decimal Balance,
    IReadOnlyList<CheckoutLine> Lines,
    IReadOnlyList<CheckoutPayment> Payments,
    IReadOnlyList<PaymentMethodView> Methods,
    bool CashOpen,
    bool HasPendingLines)
{
    public bool IsPaid => Status == OrderStatus.Paid;

    public bool CanPay => !IsPaid && CashOpen && !HasPendingLines && Balance > 0 && Status is OrderStatus.Open or OrderStatus.PendingPayment;
}

/// <param name="ReceiptQueued">Se encoló el justificante (solo si la comanda quedó cobrada y hay impresora de tickets).</param>
public sealed record PayResult(long PaymentId, decimal Change, decimal Balance, bool OrderPaid, bool AlreadyProcessed, bool ReceiptQueued);

public sealed record PayRequest(long OrderId, int MethodId, decimal Amount, decimal? Tendered, string? Reference, Guid RequestId);

/// <summary>Un pago tal como aparece en el justificante.</summary>
public sealed record ReceiptPayment(string Method, decimal Amount, decimal? Tendered, decimal Change);
