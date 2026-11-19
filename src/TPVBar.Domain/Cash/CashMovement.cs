using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Cash;

public enum CashMovementType
{
    /// <summary>Entrada manual de efectivo en el cajón (p. ej. cambio aportado).</summary>
    CashIn = 1,

    /// <summary>Salida manual de efectivo (p. ej. pago a un proveedor o retirada de excedente).</summary>
    CashOut = 2,
}

/// <summary>Movimiento manual de caja. Inmutable: lo protege un trigger en la base de datos.</summary>
public sealed class CashMovement
{
    private CashMovement()
    {
        Concept = string.Empty;
    }

    public long Id { get; private set; }

    public long CashSessionId { get; private set; }

    public CashMovementType Type { get; private set; }

    public decimal Amount { get; private set; }

    public string Concept { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public static CashMovement Create(long cashSessionId, CashMovementType type, decimal amount, string concept, Guid createdBy, DateTimeOffset now)
    {
        if (!Enum.IsDefined(type))
        {
            throw new DomainException("El tipo de movimiento no es válido.");
        }

        CashSession.ValidateAmount(amount, allowZero: false, "El importe del movimiento");
        if (string.IsNullOrWhiteSpace(concept) || concept.Trim().Length > 200)
        {
            throw new DomainException("El concepto es obligatorio (máximo 200 caracteres).");
        }

        return new CashMovement { CashSessionId = cashSessionId, Type = type, Amount = amount, Concept = concept.Trim(), CreatedAt = now, CreatedBy = createdBy };
    }
}
