using TPVBar.Domain.Catalog;
using TPVBar.Domain.Sales;

namespace TPVBar.Application.Features.Orders;

public enum TableStatus
{
    Free = 1,
    Occupied = 2,
    PendingPayment = 3,
}

public sealed record TableCardView(
    int Id,
    string Name,
    int Capacity,
    TableStatus Status,
    long? OrderId,
    DateTimeOffset? OpenedAt,
    decimal? Total,
    int? GuestCount);

public sealed record ZoneView(int Id, string Name, IReadOnlyList<TableCardView> Tables);

public sealed record FloorView(IReadOnlyList<ZoneView> Zones);

public sealed record ProductTile(int Id, string Name, decimal Price, PreparationStation Station);

public sealed record CategoryMenu(int Id, string Name, IReadOnlyList<ProductTile> Products);

public sealed record MenuView(IReadOnlyList<CategoryMenu> Categories);

public sealed record OrderLineView(
    long Id,
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    string? Notes,
    OrderLineStatus Status,
    PreparationStation Station,
    string? VoidReason);

public sealed record OrderView(
    long Id,
    int TableId,
    string TableLabel,
    OrderStatus Status,
    DateTimeOffset OpenedAt,
    int GuestCount,
    decimal Subtotal,
    decimal TaxTotal,
    decimal GrandTotal,
    IReadOnlyList<OrderLineView> Lines,
    decimal PaidTotal = 0)
{
    public decimal Balance => GrandTotal - PaidTotal;

    public bool HasPendingLines => Lines.Any(l => l.Status == OrderLineStatus.Pending);

    public bool IsEditable => Status == OrderStatus.Open;
}

public sealed record TicketSummary(PreparationStation Station, int LineCount);

/// <param name="Tickets">Tickets de preparación creados.</param>
/// <param name="AlreadyProcessed">La clave de idempotencia ya se había procesado: no se ha hecho nada.</param>
/// <param name="UnprintedStations">Destinos sin impresora configurada: el ticket existe en pantalla de cocina pero no se imprime.</param>
public sealed record SendResult(
    IReadOnlyList<TicketSummary> Tickets,
    bool AlreadyProcessed,
    IReadOnlyList<PreparationStation>? UnprintedStations = null)
{
    public IReadOnlyList<PreparationStation> Unprinted => UnprintedStations ?? [];
}
