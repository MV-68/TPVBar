using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Sales;

/// <summary>
/// Línea de comanda. Guarda una fotografía de los datos comerciales (nombre, precio, impuesto y destino)
/// en el momento de la venta: cambiar el catálogo después no altera ventas ya hechas.
/// </summary>
public sealed class OrderLine
{
    private OrderLine()
    {
        ProductNameSnapshot = string.Empty;
    }

    public long Id { get; private set; }

    public long OrderId { get; private set; }

    public int? ProductId { get; private set; }

    public string ProductNameSnapshot { get; private set; }

    public decimal Quantity { get; private set; }

    /// <summary>Precio unitario con IVA incluido en el momento de la venta.</summary>
    public decimal UnitPrice { get; private set; }

    public decimal TaxRateSnapshot { get; private set; }

    public decimal DiscountAmount { get; private set; }

    /// <summary>Importe de la línea con IVA incluido (cantidad × precio − descuento).</summary>
    public decimal LineTotal { get; private set; }

    public string? Notes { get; private set; }

    public PreparationStation Station { get; private set; }

    public OrderLineStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public string? VoidReason { get; private set; }

    public DateTimeOffset? VoidedAt { get; private set; }

    public Guid? VoidedBy { get; private set; }

    public bool IsActive => Status != OrderLineStatus.Cancelled;

    internal static OrderLine Create(
        int productId,
        string productName,
        decimal unitPrice,
        decimal taxRatePercent,
        PreparationStation station,
        decimal quantity,
        string? notes,
        Guid createdBy,
        DateTimeOffset now)
    {
        var line = new OrderLine
        {
            ProductId = productId,
            ProductNameSnapshot = productName,
            UnitPrice = unitPrice,
            TaxRateSnapshot = taxRatePercent,
            Station = station,
            Notes = NormalizeNotes(notes),
            Status = OrderLineStatus.Pending,
            CreatedAt = now,
            CreatedBy = createdBy,
        };
        line.SetQuantity(quantity);
        return line;
    }

    internal bool CanMergeWith(int productId, decimal unitPrice, string? notes) =>
        Status == OrderLineStatus.Pending &&
        ProductId == productId &&
        UnitPrice == unitPrice &&
        string.Equals(Notes, NormalizeNotes(notes), StringComparison.Ordinal);

    internal void SetQuantity(decimal quantity)
    {
        if (Status != OrderLineStatus.Pending)
        {
            throw new DomainException("Solo se puede cambiar la cantidad de una línea que aún no se ha enviado.");
        }

        if (quantity <= 0 || quantity > Order.MaxLineQuantity || decimal.Round(quantity, 3) != quantity)
        {
            throw new DomainException($"La cantidad debe ser mayor que 0 y no superar {Order.MaxLineQuantity:0}.");
        }

        Quantity = quantity;
        LineTotal = Money.Round((quantity * UnitPrice) - DiscountAmount);
    }

    internal void MarkSent(DateTimeOffset now)
    {
        Status = OrderLineStatus.Sent;
        SentAt = now;
    }

    internal void SetPreparationStatus(OrderLineStatus status) => Status = status;

    internal void Void(string reason, Guid by, DateTimeOffset now)
    {
        if (Status == OrderLineStatus.Pending)
        {
            throw new DomainException("Una línea sin enviar se quita, no se anula.");
        }

        if (Status == OrderLineStatus.Cancelled)
        {
            throw new DomainException("La línea ya está anulada.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("Indique el motivo de la anulación.");
        }

        Status = OrderLineStatus.Cancelled;
        VoidReason = reason.Trim();
        VoidedAt = now;
        VoidedBy = by;
    }

    private static string? NormalizeNotes(string? notes)
    {
        var trimmed = notes?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > 200 ? throw new DomainException("Las observaciones no pueden superar 200 caracteres.") : trimmed;
    }
}
