using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Catalog;

public sealed class Product
{
    public const decimal MaxPrice = 99999.99m;

    private Product()
    {
        Name = string.Empty;
    }

    public int Id { get; private set; }

    public int CategoryId { get; private set; }

    public int TaxRateId { get; private set; }

    /// <summary>Código o PLU opcional.</summary>
    public string? Sku { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    /// <summary>Precio de venta con IVA incluido.</summary>
    public decimal SalePrice { get; private set; }

    public PreparationStation Station { get; private set; }

    public int DisplayOrder { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Si el producto lleva control de existencias (por unidades, no por ingredientes ni recetas).</summary>
    public bool StockControlled { get; private set; }

    /// <summary>Existencias actuales. Solo cambian mediante movimientos de inventario.</summary>
    public decimal StockQuantity { get; private set; }

    /// <summary>Por debajo (o igual) de este nivel se avisa de stock bajo.</summary>
    public decimal StockMinimum { get; private set; }

    public string StockUnit { get; private set; } = "unidad";

    /// <summary>Token de concurrencia optimista (columna de sistema xmin de PostgreSQL).</summary>
    public uint RowVersion { get; private set; }

    public bool IsLowStock => StockControlled && StockQuantity <= StockMinimum;

    /// <summary>Configura el control de stock. La cantidad actual no se toca: solo los movimientos la cambian.</summary>
    public void ConfigureStock(bool controlled, decimal minimum, string? unit, DateTimeOffset now)
    {
        if (minimum < 0 || decimal.Round(minimum, 3) != minimum || minimum > 9_999_999m)
        {
            throw new DomainException("El stock mínimo debe ser cero o positivo, con un máximo de 3 decimales.");
        }

        var cleanUnit = string.IsNullOrWhiteSpace(unit) ? "unidad" : unit.Trim();
        if (cleanUnit.Length > 20)
        {
            throw new DomainException("La unidad de stock no puede superar 20 caracteres.");
        }

        StockControlled = controlled;
        StockMinimum = minimum;
        StockUnit = cleanUnit;
        UpdatedAt = now;
    }

    /// <summary>
    /// Aplica un movimiento a las existencias y devuelve el saldo resultante. Rechaza dejar el stock negativo
    /// salvo que esté autorizado expresamente.
    /// </summary>
    public decimal ApplyStockDelta(decimal delta, bool allowNegative, DateTimeOffset now)
    {
        if (!StockControlled)
        {
            throw new DomainException($"«{Name}» no lleva control de stock.");
        }

        if (delta == 0 || decimal.Round(delta, 3) != delta)
        {
            throw new DomainException("La cantidad debe ser distinta de cero, con un máximo de 3 decimales.");
        }

        var after = StockQuantity + delta;
        if (after < 0 && !allowNegative)
        {
            throw new DomainException($"Stock insuficiente de «{Name}»: quedan {StockQuantity:0.###} {StockUnit} y se piden {-delta:0.###}.");
        }

        StockQuantity = after;
        UpdatedAt = now;
        return after;
    }

    public static Product Create(
        int categoryId,
        int taxRateId,
        string name,
        decimal salePrice,
        PreparationStation station,
        string? sku,
        string? description,
        int displayOrder,
        DateTimeOffset now)
    {
        var product = new Product { CreatedAt = now, IsActive = true };
        product.Apply(categoryId, taxRateId, name, salePrice, station, sku, description, displayOrder, now);
        return product;
    }

    /// <summary>Modifica la ficha. Devuelve el precio anterior para que el llamador pueda auditar el cambio.</summary>
    public decimal Update(
        int categoryId,
        int taxRateId,
        string name,
        decimal salePrice,
        PreparationStation station,
        string? sku,
        string? description,
        int displayOrder,
        DateTimeOffset now)
    {
        var previous = SalePrice;
        Apply(categoryId, taxRateId, name, salePrice, station, sku, description, displayOrder, now);
        return previous;
    }

    public void SetActive(bool active, DateTimeOffset now)
    {
        IsActive = active;
        UpdatedAt = now;
    }

    private void Apply(
        int categoryId,
        int taxRateId,
        string name,
        decimal salePrice,
        PreparationStation station,
        string? sku,
        string? description,
        int displayOrder,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("El nombre del producto es obligatorio.");
        }

        if (name.Trim().Length > 120)
        {
            throw new DomainException("El nombre del producto no puede superar 120 caracteres.");
        }

        if (salePrice < 0 || salePrice > MaxPrice || Money.Round(salePrice) != salePrice)
        {
            throw new DomainException($"El precio debe estar entre 0 y {MaxPrice:0.00} con un máximo de 2 decimales.");
        }

        if (!Enum.IsDefined(station))
        {
            throw new DomainException("El destino de preparación no es válido.");
        }

        if (sku is { Length: > 40 })
        {
            throw new DomainException("El código no puede superar 40 caracteres.");
        }

        CategoryId = categoryId;
        TaxRateId = taxRateId;
        Name = name.Trim();
        SalePrice = salePrice;
        Station = station;
        Sku = string.IsNullOrWhiteSpace(sku) ? null : sku.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        DisplayOrder = displayOrder;
        UpdatedAt = now;
    }
}
