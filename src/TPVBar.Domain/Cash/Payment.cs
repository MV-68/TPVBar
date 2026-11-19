using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Cash;

/// <summary>
/// Cobro confirmado de una comanda. Es INMUTABLE (un trigger lo impide en la base de datos): las correcciones
/// se registran como <see cref="PaymentRefund"/>, nunca modificando ni borrando el pago.
/// </summary>
public sealed class Payment
{
    private Payment()
    {
    }

    public long Id { get; private set; }

    public long OrderId { get; private set; }

    public long CashSessionId { get; private set; }

    public int PaymentMethodId { get; private set; }

    /// <summary>Importe aplicado a la cuenta.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Efectivo entregado por el cliente (solo métodos de cajón). El cambio devuelto es <see cref="ChangeGiven"/>.</summary>
    public decimal? TenderedAmount { get; private set; }

    public decimal ChangeGiven { get; private set; }

    public string? ExternalReference { get; private set; }

    public DateTimeOffset PaidAt { get; private set; }

    public Guid ReceivedBy { get; private set; }

    public static Payment Create(
        long orderId,
        long cashSessionId,
        PaymentMethod method,
        decimal amount,
        decimal? tenderedAmount,
        string? externalReference,
        Guid receivedBy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (!method.IsActive)
        {
            throw new DomainException($"El método de pago «{method.Name}» está desactivado.");
        }

        if (amount <= 0 || Money.Round(amount) != amount)
        {
            throw new DomainException("El importe del cobro debe ser mayor que cero, con un máximo de 2 decimales.");
        }

        var reference = string.IsNullOrWhiteSpace(externalReference) ? null : externalReference.Trim();
        if (reference is { Length: > 120 })
        {
            throw new DomainException("La referencia no puede superar 120 caracteres.");
        }

        if (method.RequiresReference && reference is null)
        {
            throw new DomainException($"El método «{method.Name}» exige indicar una referencia.");
        }

        decimal? tendered = null;
        var change = 0m;
        if (method.AffectsCashDrawer)
        {
            tendered = tenderedAmount ?? amount;
            if (Money.Round(tendered.Value) != tendered.Value || tendered < amount)
            {
                throw new DomainException("El efectivo entregado no puede ser menor que el importe a cobrar.");
            }

            change = tendered.Value - amount;
        }
        else if (tenderedAmount is not null && tenderedAmount != amount)
        {
            throw new DomainException("Solo se admite «entregado» y cambio en métodos de efectivo.");
        }

        return new Payment
        {
            OrderId = orderId,
            CashSessionId = cashSessionId,
            PaymentMethodId = method.Id,
            Amount = amount,
            TenderedAmount = tendered,
            ChangeGiven = change,
            ExternalReference = reference,
            PaidAt = now,
            ReceivedBy = receivedBy,
        };
    }
}
