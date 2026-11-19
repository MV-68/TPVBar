using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Floor;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Catalog;

/// <summary>
/// Administración del catálogo y de la sala: zonas, mesas, categorías, tipos impositivos y productos.
/// Exige <see cref="Permission.ManageCatalog"/>; cambiar un precio exige además <see cref="Permission.ModifyPrices"/>.
/// </summary>
public sealed class CatalogService(
    IAppData data,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    AuditWriter audit,
    TimeProvider time)
{
    // ----- Lecturas -----

    public async Task<IReadOnlyList<ZoneRow>> ListZonesAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var zones = await data.ToListAsync(data.NoTracking(data.Zones).OrderBy(z => z.DisplayOrder).ThenBy(z => z.Name), cancellationToken);
        var tables = await data.ToListAsync(data.NoTracking(data.Tables).OrderBy(t => t.DisplayOrder).ThenBy(t => t.Name), cancellationToken);

        return zones.Select(z => new ZoneRow(
            z.Id,
            z.Name,
            z.DisplayOrder,
            z.IsActive,
            tables.Where(t => t.ZoneId == z.Id).Select(t => new TableRow(t.Id, t.ZoneId, t.Name, t.Capacity, t.DisplayOrder, t.IsActive)).ToList())).ToList();
    }

    public async Task<IReadOnlyList<CategoryRow>> ListCategoriesAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var categories = await data.ToListAsync(data.NoTracking(data.Categories).OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name), cancellationToken);
        return categories.Select(c => new CategoryRow(c.Id, c.Name, c.DisplayOrder, c.IsActive)).ToList();
    }

    public async Task<IReadOnlyList<TaxRateRow>> ListTaxRatesAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var today = BusinessTime.Today(time);
        var rates = await data.ToListAsync(data.NoTracking(data.TaxRates).OrderBy(t => t.Rate).ThenBy(t => t.ValidFrom), cancellationToken);
        return rates.Select(t => new TaxRateRow(t.Id, t.Name, t.Rate, t.ValidFrom, t.ValidTo, t.IsValidOn(today))).ToList();
    }

    public async Task<IReadOnlyList<ProductRow>> ListProductsAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var products = await data.ToListAsync(data.NoTracking(data.Products).OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name), cancellationToken);
        var categories = (await data.ToListAsync(data.NoTracking(data.Categories), cancellationToken)).ToDictionary(c => c.Id, c => c.Name);
        var rates = (await data.ToListAsync(data.NoTracking(data.TaxRates), cancellationToken)).ToDictionary(t => t.Id, t => t.Name);

        return products.Select(p => new ProductRow(
            p.Id,
            p.CategoryId,
            categories.GetValueOrDefault(p.CategoryId, "?"),
            p.TaxRateId,
            rates.GetValueOrDefault(p.TaxRateId, "?"),
            p.Sku,
            p.Name,
            p.Description,
            p.SalePrice,
            p.Station,
            p.DisplayOrder,
            p.IsActive,
            p.StockControlled,
            p.StockQuantity,
            p.StockMinimum,
            p.StockUnit)).ToList();
    }

    // ----- Zonas y mesas -----

    public async Task<int> CreateZoneAsync(string name, int displayOrder, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var zone = Zone.Create(name, displayOrder, time.GetUtcNow());
        data.Add(zone);
        await SaveAsync("Ya existe una zona con ese nombre.", cancellationToken);
        Log(AuditActions.CatalogCreated, "zone", zone.Id, new { zone.Name });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return zone.Id;
    }

    public async Task UpdateZoneAsync(int id, string name, int displayOrder, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var zone = await FindAsync(data.Zones.Where(z => z.Id == id), "La zona no existe.", cancellationToken);
        zone.Update(name, displayOrder);
        Log(AuditActions.CatalogUpdated, "zone", id, new { zone.Name, displayOrder });
        await SaveAsync("Ya existe una zona con ese nombre.", cancellationToken);
    }

    public async Task SetZoneActiveAsync(int id, bool active, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var zone = await FindAsync(data.Zones.Where(z => z.Id == id), "La zona no existe.", cancellationToken);

        if (!active)
        {
            var tableIds = await data.ToListAsync(data.NoTracking(data.Tables).Where(t => t.ZoneId == id).Select(t => t.Id), cancellationToken);
            if (await HasLiveOrderAsync(tableIds, cancellationToken))
            {
                throw new DomainException("No se puede desactivar la zona: tiene mesas con comandas abiertas.");
            }
        }

        zone.SetActive(active);
        Log(AuditActions.CatalogUpdated, "zone", id, new { active });
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CreateTableAsync(int zoneId, string name, int capacity, int displayOrder, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        await FindAsync(data.Zones.Where(z => z.Id == zoneId), "La zona no existe.", cancellationToken);

        var table = DiningTable.Create(zoneId, name, capacity, displayOrder);
        data.Add(table);
        await SaveAsync("Ya existe una mesa con ese nombre en la zona.", cancellationToken);
        Log(AuditActions.CatalogCreated, "table", table.Id, new { table.Name, zoneId });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return table.Id;
    }

    public async Task UpdateTableAsync(int id, int zoneId, string name, int capacity, int displayOrder, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var table = await FindAsync(data.Tables.Where(t => t.Id == id), "La mesa no existe.", cancellationToken);
        await FindAsync(data.Zones.Where(z => z.Id == zoneId), "La zona no existe.", cancellationToken);

        table.Update(zoneId, name, capacity, displayOrder);
        Log(AuditActions.CatalogUpdated, "table", id, new { table.Name, zoneId, capacity });
        await SaveAsync("Ya existe una mesa con ese nombre en la zona.", cancellationToken);
    }

    public async Task SetTableActiveAsync(int id, bool active, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var table = await FindAsync(data.Tables.Where(t => t.Id == id), "La mesa no existe.", cancellationToken);

        if (!active && await HasLiveOrderAsync([id], cancellationToken))
        {
            throw new DomainException("No se puede desactivar la mesa: tiene una comanda abierta.");
        }

        table.SetActive(active);
        Log(AuditActions.CatalogUpdated, "table", id, new { active });
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    // ----- Categorías -----

    public async Task<int> CreateCategoryAsync(string name, int displayOrder, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var category = Category.Create(name, displayOrder);
        data.Add(category);
        await SaveAsync("Ya existe una categoría con ese nombre.", cancellationToken);
        Log(AuditActions.CatalogCreated, "category", category.Id, new { category.Name });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return category.Id;
    }

    public async Task UpdateCategoryAsync(int id, string name, int displayOrder, bool active, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var category = await FindAsync(data.Categories.Where(c => c.Id == id), "La categoría no existe.", cancellationToken);
        category.Update(name, displayOrder);
        category.SetActive(active);
        Log(AuditActions.CatalogUpdated, "category", id, new { category.Name, active });
        await SaveAsync("Ya existe una categoría con ese nombre.", cancellationToken);
    }

    // ----- Tipos impositivos -----

    public async Task<int> CreateTaxRateAsync(string name, decimal ratePercent, DateOnly validFrom, DateOnly? validTo, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var rate = TaxRate.Create(name, ratePercent, validFrom, validTo);
        data.Add(rate);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        Log(AuditActions.CatalogCreated, "tax_rate", rate.Id, new { rate.Name, ratePercent, validFrom, validTo });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return rate.Id;
    }

    public async Task EndTaxRateAsync(int id, DateOnly lastDay, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var rate = await FindAsync(data.TaxRates.Where(t => t.Id == id), "El tipo impositivo no existe.", cancellationToken);
        rate.EndValidity(lastDay);
        Log(AuditActions.CatalogUpdated, "tax_rate", id, new { lastDay });
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    // ----- Productos -----

    public async Task<int> CreateProductAsync(ProductInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        user.Require(Permission.ManageCatalog);
        await EnsureReferencesAsync(input, cancellationToken);

        var product = Product.Create(input.CategoryId, input.TaxRateId, input.Name, input.SalePrice, input.Station, input.Sku, input.Description, input.DisplayOrder, time.GetUtcNow());
        product.ConfigureStock(input.StockControlled, input.StockMinimum, input.StockUnit, time.GetUtcNow());
        data.Add(product);
        await SaveAsync("Ya existe un producto con ese código.", cancellationToken);
        Log(AuditActions.CatalogCreated, "product", product.Id, new { product.Name, input.SalePrice });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return product.Id;
    }

    public async Task UpdateProductAsync(int id, ProductInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        user.Require(Permission.ManageCatalog);
        var product = await FindAsync(data.Products.Where(p => p.Id == id), "El producto no existe.", cancellationToken);
        await EnsureReferencesAsync(input, cancellationToken);

        if (product.SalePrice != input.SalePrice)
        {
            user.Require(Permission.ModifyPrices);
        }

        var previous = product.Update(input.CategoryId, input.TaxRateId, input.Name, input.SalePrice, input.Station, input.Sku, input.Description, input.DisplayOrder, time.GetUtcNow());
        product.ConfigureStock(input.StockControlled, input.StockMinimum, input.StockUnit, time.GetUtcNow());

        Log(AuditActions.CatalogUpdated, "product", id, new { product.Name });
        if (previous != input.SalePrice)
        {
            // Las ventas ya hechas no cambian: cada línea guarda el precio que tenía al venderse.
            Log(AuditActions.PriceChanged, "product", id, new { from = previous, to = input.SalePrice });
        }

        await SaveAsync("Ya existe un producto con ese código.", cancellationToken);
    }

    public async Task SetProductActiveAsync(int id, bool active, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var product = await FindAsync(data.Products.Where(p => p.Id == id), "El producto no existe.", cancellationToken);
        product.SetActive(active, time.GetUtcNow());
        Log(AuditActions.CatalogUpdated, "product", id, new { active });
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    // ----- Auxiliares -----

    private async Task EnsureReferencesAsync(ProductInput input, CancellationToken cancellationToken)
    {
        if (!await data.AnyAsync(data.Categories.Where(c => c.Id == input.CategoryId), cancellationToken))
        {
            throw new DomainException("La categoría no existe.");
        }

        if (!await data.AnyAsync(data.TaxRates.Where(t => t.Id == input.TaxRateId), cancellationToken))
        {
            throw new DomainException("El tipo impositivo no existe.");
        }
    }

    private async Task<bool> HasLiveOrderAsync(List<int> tableIds, CancellationToken cancellationToken) =>
        tableIds.Count > 0 &&
        await data.AnyAsync(
            data.NoTracking(data.Orders).Where(o => tableIds.Contains(o.TableId) && FloorStatuses.Live.Contains(o.Status)),
            cancellationToken);

    private async Task<T> FindAsync<T>(IQueryable<T> query, string notFoundMessage, CancellationToken cancellationToken) =>
        await data.FirstOrDefaultAsync(query, cancellationToken) ?? throw new NotFoundException(notFoundMessage);

    private void Log(string action, string entityType, object id, object details) => audit.Write(action, entityType, id, details);

    private async Task SaveAsync(string duplicateMessage, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateEntryException ex)
        {
            throw new DomainException(duplicateMessage, ex);
        }
    }
}

internal static class FloorStatuses
{
    public static readonly Domain.Sales.OrderStatus[] Live = [Domain.Sales.OrderStatus.Open, Domain.Sales.OrderStatus.PendingPayment];
}
