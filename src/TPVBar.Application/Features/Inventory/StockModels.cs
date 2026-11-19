using TPVBar.Domain.Inventory;

namespace TPVBar.Application.Features.Inventory;

public enum StockStatus
{
    Ok = 1,
    Low = 2,
    OutOfStock = 3,
    Negative = 4,
}

public sealed record StockRow(
    int ProductId,
    string Name,
    string CategoryName,
    decimal Quantity,
    decimal Minimum,
    string Unit,
    bool ProductActive)
{
    public StockStatus Status => Quantity < 0 ? StockStatus.Negative : Quantity == 0 ? StockStatus.OutOfStock : Quantity <= Minimum ? StockStatus.Low : StockStatus.Ok;

    public bool NeedsAttention => Status != StockStatus.Ok;
}

public sealed record StockOverview(IReadOnlyList<StockRow> Rows, bool AllowNegative)
{
    public int AttentionCount => Rows.Count(r => r.ProductActive && r.NeedsAttention);
}

public sealed record StockMovementRow(
    long Id,
    DateTimeOffset At,
    decimal Delta,
    decimal After,
    StockMovementReason Reason,
    string? Reference,
    string? Notes,
    string By);
