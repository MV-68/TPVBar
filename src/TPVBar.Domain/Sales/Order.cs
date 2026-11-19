using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Sales;

/// <summary>
/// Comanda (cuenta) de una mesa. Los totales se calculan SIEMPRE aquí, en el servidor, a partir de las
/// líneas activas; nunca se aceptan importes del navegador.
/// </summary>
public sealed class Order
{
    public const decimal MaxLineQuantity = 999m;

    private readonly List<OrderLine> _lines = [];

    private Order()
    {
    }

    public long Id { get; private set; }

    public int TableId { get; private set; }

    public Guid OpenedBy { get; private set; }

    public OrderStatus Status { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int GuestCount { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>Base imponible (suma de bases de las líneas activas).</summary>
    public decimal Subtotal { get; private set; }

    public decimal DiscountTotal { get; private set; }

    public decimal TaxTotal { get; private set; }

    /// <summary>Total con IVA incluido. Siempre igual a Subtotal + TaxTotal.</summary>
    public decimal GrandTotal { get; private set; }

    /// <summary>Cobrado hasta ahora, neto de devoluciones. Se actualiza en la misma transacción que cada pago.</summary>
    public decimal PaidTotal { get; private set; }

    /// <summary>Saldo pendiente de cobro.</summary>
    public decimal Balance => GrandTotal - PaidTotal;

    /// <summary>Token de concurrencia optimista (columna de sistema xmin de PostgreSQL).</summary>
    public uint RowVersion { get; private set; }

    public IReadOnlyCollection<OrderLine> Lines => _lines;

    public static Order Open(int tableId, Guid openedBy, int guestCount, DateTimeOffset now)
    {
        var order = new Order
        {
            TableId = tableId,
            OpenedBy = openedBy,
            Status = OrderStatus.Open,
            OpenedAt = now,
            UpdatedAt = now,
        };
        order.SetGuestCount(guestCount, now);
        return order;
    }

    public void SetGuestCount(int guestCount, DateTimeOffset now)
    {
        EnsureEditable();
        if (guestCount is < 1 or > 99)
        {
            throw new DomainException("El número de comensales debe estar entre 1 y 99.");
        }

        GuestCount = guestCount;
        UpdatedAt = now;
    }

    public OrderLine AddLine(
        Product product,
        decimal taxRatePercent,
        decimal quantity,
        string? notes,
        Guid userId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(product);
        EnsureEditable();

        var existing = _lines.Find(l => l.CanMergeWith(product.Id, product.SalePrice, notes));
        if (existing is not null)
        {
            existing.SetQuantity(existing.Quantity + quantity);
            Touch(now);
            return existing;
        }

        var line = OrderLine.Create(product.Id, product.Name, product.SalePrice, taxRatePercent, product.Station, quantity, notes, userId, now);
        _lines.Add(line);
        Touch(now);
        return line;
    }

    public void ChangeQuantity(long lineId, decimal quantity, DateTimeOffset now)
    {
        EnsureEditable();
        FindLine(lineId).SetQuantity(quantity);
        Touch(now);
    }

    /// <summary>Quita una línea que aún no se ha enviado (no hay nada confirmado que conservar).</summary>
    public OrderLine RemoveLine(long lineId, DateTimeOffset now)
    {
        EnsureEditable();
        var line = FindLine(lineId);
        if (line.Status != OrderLineStatus.Pending)
        {
            throw new DomainException("Una línea ya enviada no se puede quitar: hay que anularla indicando el motivo.");
        }

        _lines.Remove(line);
        Touch(now);
        return line;
    }

    /// <summary>Anula una línea ya enviada. El permiso se comprueba en la capa de aplicación.</summary>
    public OrderLine VoidLine(long lineId, string reason, Guid by, DateTimeOffset now)
    {
        EnsureEditable();
        var line = FindLine(lineId);
        line.Void(reason, by, now);
        Touch(now);
        return line;
    }

    /// <summary>Marca como enviadas todas las líneas pendientes y devuelve las afectadas.</summary>
    public IReadOnlyList<OrderLine> MarkPendingLinesSent(DateTimeOffset now)
    {
        EnsureEditable();
        var pending = _lines.Where(l => l.Status == OrderLineStatus.Pending).ToList();
        if (pending.Count == 0)
        {
            throw new DomainException("No hay líneas pendientes de enviar.");
        }

        foreach (var line in pending)
        {
            line.MarkSent(now);
        }

        Touch(now);
        return pending;
    }

    /// <summary>
    /// Refleja en la comanda el avance de preparación de cocina. Las líneas anuladas no se tocan. También
    /// "toca" la comanda para que un avance de cocina y una anulación simultánea no se pisen.
    /// </summary>
    public void SetLinesPreparationStatus(IReadOnlyCollection<long> lineIds, OrderLineStatus status, DateTimeOffset now)
    {
        if (status is OrderLineStatus.Pending or OrderLineStatus.Cancelled)
        {
            throw new DomainException("El estado de preparación no es válido.");
        }

        foreach (var line in _lines.Where(l => lineIds.Contains(l.Id) && l.Status != OrderLineStatus.Cancelled && l.Status != OrderLineStatus.Pending))
        {
            line.SetPreparationStatus(status);
        }

        Touch(now);
    }

    public void RequestBill(DateTimeOffset now)
    {
        if (Status != OrderStatus.Open)
        {
            throw new DomainException("Solo se puede pedir la cuenta de una comanda abierta.");
        }

        if (_lines.Any(l => l.Status == OrderLineStatus.Pending))
        {
            throw new DomainException("Envíe o quite las líneas pendientes antes de pedir la cuenta.");
        }

        if (!_lines.Exists(l => l.IsActive))
        {
            throw new DomainException("La comanda no tiene líneas que cobrar.");
        }

        Status = OrderStatus.PendingPayment;
        Touch(now);
    }

    /// <summary>
    /// Registra un cobro (total o parcial). La primera entrada de dinero bloquea la edición de la comanda;
    /// al llegar el saldo a cero la comanda queda cobrada y la mesa libre. No admite cobrar más que el saldo.
    /// </summary>
    public void RegisterPayment(decimal amount, DateTimeOffset now)
    {
        if (Status is not (OrderStatus.Open or OrderStatus.PendingPayment))
        {
            throw new DomainException(Status == OrderStatus.Paid ? "La comanda ya está cobrada." : "La comanda está cancelada.");
        }

        if (_lines.Exists(l => l.Status == OrderLineStatus.Pending))
        {
            throw new DomainException("Envíe o quite las líneas pendientes antes de cobrar.");
        }

        if (GrandTotal <= 0 || !_lines.Exists(l => l.IsActive))
        {
            throw new DomainException("La comanda no tiene importe que cobrar.");
        }

        if (amount <= 0 || Common.Money.Round(amount) != amount)
        {
            throw new DomainException("El importe del cobro debe ser mayor que cero, con un máximo de 2 decimales.");
        }

        if (amount > Balance)
        {
            throw new DomainException($"El importe supera el saldo pendiente ({Balance:0.00} €).");
        }

        PaidTotal += amount;
        if (Balance == 0)
        {
            Status = OrderStatus.Paid;
            ClosedAt = now;
        }
        else
        {
            Status = OrderStatus.PendingPayment;
        }

        Touch(now);
    }

    /// <summary>Refleja una devolución: baja lo cobrado. La comanda cobrada sigue cerrada (es una corrección posterior).</summary>
    public void RegisterRefund(decimal amount, DateTimeOffset now)
    {
        if (Status is not (OrderStatus.Paid or OrderStatus.PendingPayment))
        {
            throw new DomainException("Solo se puede devolver dinero de una comanda con cobros.");
        }

        if (amount <= 0 || amount > PaidTotal)
        {
            throw new DomainException("El importe a devolver supera lo cobrado de la comanda.");
        }

        PaidTotal -= amount;
        Touch(now);
    }

    public void Reopen(DateTimeOffset now)
    {
        if (Status != OrderStatus.PendingPayment)
        {
            throw new DomainException("Solo se puede reabrir una comanda pendiente de cobro.");
        }

        if (PaidTotal > 0)
        {
            throw new DomainException("La comanda tiene cobros registrados: devuélvalos antes de reabrirla.");
        }

        Status = OrderStatus.Open;
        Touch(now);
    }

    /// <summary>Cancela una comanda que no tiene nada servido ni cobrado y libera la mesa.</summary>
    public void Cancel(DateTimeOffset now)
    {
        EnsureEditable();
        if (_lines.Exists(l => l.IsActive && l.Status != OrderLineStatus.Pending))
        {
            throw new DomainException("No se puede cancelar una comanda con productos ya enviados: anule antes cada línea.");
        }

        _lines.RemoveAll(l => l.Status == OrderLineStatus.Pending);
        Status = OrderStatus.Cancelled;
        ClosedAt = now;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Recalculate();
    }

    /// <summary>Recalcula los totales a partir de las líneas activas.</summary>
    private void Recalculate()
    {
        decimal net = 0;
        decimal tax = 0;
        decimal gross = 0;
        decimal discounts = 0;

        foreach (var line in _lines.Where(l => l.IsActive))
        {
            var (lineNet, lineTax) = Money.SplitGross(line.LineTotal, line.TaxRateSnapshot);
            net += lineNet;
            tax += lineTax;
            gross += line.LineTotal;
            discounts += line.DiscountAmount;
        }

        Subtotal = net;
        TaxTotal = tax;
        GrandTotal = gross;
        DiscountTotal = discounts;
    }

    private void EnsureEditable()
    {
        if (Status != OrderStatus.Open)
        {
            throw new DomainException(Status switch
            {
                OrderStatus.PendingPayment => "La comanda está pendiente de cobro: reábrala para modificarla.",
                OrderStatus.Paid => "La comanda ya está cobrada.",
                _ => "La comanda está cancelada.",
            });
        }
    }

    private OrderLine FindLine(long lineId) =>
        _lines.Find(l => l.Id == lineId) ?? throw new NotFoundException("La línea no existe en esta comanda.");
}
