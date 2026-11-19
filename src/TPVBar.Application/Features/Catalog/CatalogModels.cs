using TPVBar.Domain.Catalog;

namespace TPVBar.Application.Features.Catalog;

public sealed record ZoneRow(int Id, string Name, int DisplayOrder, bool IsActive, IReadOnlyList<TableRow> Tables);

public sealed record TableRow(int Id, int ZoneId, string Name, int Capacity, int DisplayOrder, bool IsActive);

public sealed record CategoryRow(int Id, string Name, int DisplayOrder, bool IsActive);

public sealed record TaxRateRow(int Id, string Name, decimal Rate, DateOnly ValidFrom, DateOnly? ValidTo, bool IsValidToday);

public sealed record ProductRow(
    int Id,
    int CategoryId,
    string CategoryName,
    int TaxRateId,
    string TaxRateName,
    string? Sku,
    string Name,
    string? Description,
    decimal SalePrice,
    PreparationStation Station,
    int DisplayOrder,
    bool IsActive,
    bool StockControlled = false,
    decimal StockQuantity = 0,
    decimal StockMinimum = 0,
    string StockUnit = "unidad");

public sealed record ProductInput(
    int CategoryId,
    int TaxRateId,
    string Name,
    decimal SalePrice,
    PreparationStation Station,
    string? Sku,
    string? Description,
    int DisplayOrder,
    bool StockControlled = false,
    decimal StockMinimum = 0,
    string? StockUnit = null);
