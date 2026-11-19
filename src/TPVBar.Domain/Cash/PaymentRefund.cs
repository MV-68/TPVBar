using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Cash;

/// <summary>
/// Devolución (operación compensatoria) sobre un pago. Inmutable. Se devuelve por el mismo método del cobro
/// y se imputa a la sesión de caja abierta en el momento de devolver.
/// </summary>
public sealed class PaymentRefund
{
    private PaymentRefund()
    {
        Reason = string.Empty;
    }

    public long Id { get; private set; }

    public long PaymentId { get; private set; }

    public long OrderId { get; private set; }

    public long CashSessionId { get; private set; }

    public int PaymentMethodId { get; private set; }

    public decimal Amount { get; private set; }

    public string Reason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public static PaymentRefund Create(Payment payment, long cashSessionId, decimal amount, string reason, Guid createdBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);

        if (amount <= 0 || Money.Round(amount) != amount)
        {
            throw new DomainException("El importe a devolver debe ser mayor que cero, con un máximo de 2 decimales.");
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 200)
        {
            throw new DomainException("Indique el motivo de la devolución (máximo 200 caracteres).");
        }

        return new PaymentRefund
        {
            PaymentId = payment.Id,
            OrderId = payment.OrderId,
            CashSessionId = cashSessionId,
            PaymentMethodId = payment.PaymentMethodId,
            Amount = amount,
            Reason = reason.Trim(),
            CreatedAt = now,
            CreatedBy = createdBy,
        };
    }
}
