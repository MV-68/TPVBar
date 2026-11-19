using TPVBar.Domain.Catalog;

namespace TPVBar.Domain.Sales;

public enum KitchenTicketStatus
{
    Pending = 1,
    Preparing = 2,
    Ready = 3,
    Delivered = 4,
    Cancelled = 5,
}

/// <summary>
/// Ticket de preparación de un envío para un destino (cocina o barra). No es un documento fiscal.
/// Guarda una fotografía de la mesa y de cada línea para no depender de datos que cambien después.
/// </summary>
public sealed class KitchenTicket
{
    private readonly List<KitchenTicketLine> _lines = [];

    private KitchenTicket()
    {
        TableLabel = string.Empty;
    }

    public long Id { get; private set; }

    public long OrderId { get; private set; }

    public PreparationStation Station { get; private set; }

    public string TableLabel { get; private set; }

    public KitchenTicketStatus Status { get; private set; }

    public DateTimeOffset SentAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public IReadOnlyCollection<KitchenTicketLine> Lines => _lines;

    /// <summary>Hay algo que preparar: el ticket no está entregado ni cancelado.</summary>
    public bool IsLive => Status is not (KitchenTicketStatus.Delivered or KitchenTicketStatus.Cancelled);

    /// <summary>Avanza el estado de preparación (solo hacia delante) y registra quién y cuándo.</summary>
    public void Advance(KitchenTicketStatus newStatus, Guid by, DateTimeOffset now)
    {
        if (!IsLive)
        {
            throw new Exceptions.DomainException("El ticket ya está cerrado.");
        }

        if (newStatus is KitchenTicketStatus.Pending or KitchenTicketStatus.Cancelled || newStatus <= Status)
        {
            throw new Exceptions.DomainException("El estado solicitado no es válido para este ticket.");
        }

        Status = newStatus;
        UpdatedAt = now;
        UpdatedBy = by;
        foreach (var line in _lines.Where(l => l.Status != KitchenTicketStatus.Cancelled))
        {
            line.SetStatus(newStatus);
        }

        if (newStatus == KitchenTicketStatus.Delivered)
        {
            CompletedAt = now;
        }
    }

    /// <summary>Marca como anulada la línea del ticket correspondiente a una línea de comanda anulada.</summary>
    public bool CancelLine(long orderLineId, Guid by, DateTimeOffset now)
    {
        var line = _lines.Find(l => l.OrderLineId == orderLineId);
        if (line is null || line.Status == KitchenTicketStatus.Cancelled)
        {
            return false;
        }

        line.SetStatus(KitchenTicketStatus.Cancelled);
        UpdatedAt = now;
        UpdatedBy = by;
        if (_lines.TrueForAll(l => l.Status == KitchenTicketStatus.Cancelled))
        {
            Status = KitchenTicketStatus.Cancelled;
            CompletedAt = now;
        }

        return true;
    }

    public static KitchenTicket Create(
        long orderId,
        PreparationStation station,
        string tableLabel,
        IEnumerable<OrderLine> lines,
        Guid createdBy,
        DateTimeOffset now)
    {
        var ticket = new KitchenTicket
        {
            OrderId = orderId,
            Station = station,
            TableLabel = tableLabel,
            Status = KitchenTicketStatus.Pending,
            SentAt = now,
            UpdatedAt = now,
            CreatedBy = createdBy,
        };

        foreach (var line in lines)
        {
            ticket._lines.Add(new KitchenTicketLine(line));
        }

        return ticket;
    }
}

public sealed class KitchenTicketLine
{
    private KitchenTicketLine()
    {
        ProductNameSnapshot = string.Empty;
    }

    internal KitchenTicketLine(OrderLine line)
    {
        OrderLineId = line.Id;
        OrderLine = line;
        Quantity = line.Quantity;
        ProductNameSnapshot = line.ProductNameSnapshot;
        Notes = line.Notes;
        Status = KitchenTicketStatus.Pending;
    }

    public long Id { get; private set; }

    public long KitchenTicketId { get; private set; }

    public long OrderLineId { get; private set; }

    public OrderLine? OrderLine { get; private set; }

    public decimal Quantity { get; private set; }

    public string ProductNameSnapshot { get; private set; }

    public string? Notes { get; private set; }

    public KitchenTicketStatus Status { get; private set; }

    internal void SetStatus(KitchenTicketStatus status) => Status = status;
}
