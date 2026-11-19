using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Inventory;

public enum StockMovementReason
{
    /// <summary>Salida por venta: se descuenta al enviar la línea a cocina o barra.</summary>
    Sale = 1,

    /// <summary>Reposición al anular una línea ya enviada (movimiento inverso de la venta).</summary>
    VoidReturn = 2,

    /// <summary>Entrada manual (compra, reposición).</summary>
    Entry = 3,

    /// <summary>Salida manual (merma, consumo propio, rotura).</summary>
    Exit = 4,

    /// <summary>Ajuste a un recuento físico.</summary>
    Adjustment = 5,
}

/// <summary>
/// Movimiento de inventario. Inmutable (un trigger lo impide en la base de datos) y con un saldo resultante
/// guardado: el stock actual de un producto siempre es la suma de sus movimientos. Los movimientos ligados a una
/// línea de comanda son únicos por motivo y línea: un reintento no puede descontar dos veces.
/// </summary>
public sealed class StockMovement
{
    private StockMovement()
    {
    }

    public long Id { get; private set; }

    public int ProductId { get; private set; }

    /// <summary>Positivo = entra stock; negativo = sale. Nunca cero.</summary>
    public decimal QuantityDelta { get; private set; }

    /// <summary>Existencias del producto justo después de este movimiento.</summary>
    public decimal QuantityAfter { get; private set; }

    public StockMovementReason Reason { get; private set; }

    public string? ReferenceType { get; private set; }

    public string? ReferenceId { get; private set; }

    public string? Notes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public static StockMovement Create(
        int productId,
        decimal quantityDelta,
        decimal quantityAfter,
        StockMovementReason reason,
        string? referenceType,
        string? referenceId,
        string? notes,
        Guid createdBy,
        DateTimeOffset now)
    {
        if (quantityDelta == 0)
        {
            throw new DomainException("El movimiento de stock no puede ser de cantidad cero.");
        }

        if (!Enum.IsDefined(reason))
        {
            throw new DomainException("El motivo del movimiento no es válido.");
        }

        var trimmed = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (trimmed is { Length: > 500 })
        {
            throw new DomainException("Las notas no pueden superar 500 caracteres.");
        }

        return new StockMovement
        {
            ProductId = productId,
            QuantityDelta = quantityDelta,
            QuantityAfter = quantityAfter,
            Reason = reason,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            Notes = trimmed,
            CreatedAt = now,
            CreatedBy = createdBy,
        };
    }
}
