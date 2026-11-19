using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Inventory;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Inventory;

/// <summary>Almacén básico por unidades: existencias, entradas, salidas, ajustes e historial.</summary>
public sealed class StockService(
    IAppData data,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    AuditWriter audit,
    StockLedger ledger,
    TimeProvider time)
{
    public async Task<StockOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.AdjustStock);

        var categories = (await data.ToListAsync(data.NoTracking(data.Categories), cancellationToken)).ToDictionary(c => c.Id, c => c.Name);
        var products = await data.ToListAsync(data.NoTracking(data.Products).Where(p => p.StockControlled).OrderBy(p => p.Name), cancellationToken);

        var rows = products.Select(p => new StockRow(
            p.Id,
            p.Name,
            categories.GetValueOrDefault(p.CategoryId, "?"),
            p.StockQuantity,
            p.StockMinimum,
            p.StockUnit,
            p.IsActive)).ToList();
        return new StockOverview(rows, await ledger.AllowNegativeAsync(cancellationToken));
    }

    /// <summary>Productos activos con stock bajo, agotado o negativo (para el indicador de la barra superior).</summary>
    public async Task<int> GetAttentionCountAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.AdjustStock);
        var products = await data.ToListAsync(
            data.NoTracking(data.Products).Where(p => p.StockControlled && p.IsActive).Select(p => new { p.StockQuantity, p.StockMinimum }),
            cancellationToken);
        return products.Count(p => p.StockQuantity <= p.StockMinimum);
    }

    public Task RegisterEntryAsync(int productId, decimal quantity, string? notes, Guid requestId, CancellationToken cancellationToken) =>
        RegisterAsync(productId, StockMovementReason.Entry, RequirePositive(quantity), notes, requireNotes: false, requestId, cancellationToken);

    public Task RegisterExitAsync(int productId, decimal quantity, string notes, Guid requestId, CancellationToken cancellationToken) =>
        RegisterAsync(productId, StockMovementReason.Exit, -RequirePositive(quantity), notes, requireNotes: true, requestId, cancellationToken);

    /// <summary>Ajusta las existencias a un recuento físico. El ajuste se calcula con el stock actual del servidor.</summary>
    public async Task AdjustToCountAsync(int productId, decimal counted, string notes, Guid requestId, CancellationToken cancellationToken)
    {
        user.Require(Permission.AdjustStock);
        if (counted < 0 || decimal.Round(counted, 3) != counted)
        {
            throw new DomainException("El recuento debe ser cero o positivo, con un máximo de 3 decimales.");
        }

        if (await WasProcessedAsync(requestId, cancellationToken))
        {
            return;
        }

        var product = await FindControlledAsync(productId, cancellationToken);
        var delta = counted - product.StockQuantity;
        if (delta == 0)
        {
            throw new DomainException("El recuento coincide con las existencias: no hay nada que ajustar.");
        }

        await ApplyAsync(product, delta, StockMovementReason.Adjustment, notes, requireNotes: true, requestId, cancellationToken);
    }

    public async Task<IReadOnlyList<StockMovementRow>> ListMovementsAsync(int productId, int take, CancellationToken cancellationToken)
    {
        user.Require(Permission.AdjustStock);

        var movements = await data.ToListAsync(
            data.NoTracking(data.StockMovements).Where(m => m.ProductId == productId).OrderByDescending(m => m.Id).Take(Math.Clamp(take, 1, 500)),
            cancellationToken);
        var userIds = movements.Select(m => m.CreatedBy).Distinct().ToList();
        var names = (await data.ToListAsync(data.NoTracking(data.Users).Where(u => userIds.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName }), cancellationToken))
            .ToDictionary(u => u.Id, u => u.DisplayName);

        return [.. movements.Select(m => new StockMovementRow(
            m.Id,
            m.CreatedAt,
            m.QuantityDelta,
            m.QuantityAfter,
            m.Reason,
            m.ReferenceType is null ? null : $"{m.ReferenceType} {m.ReferenceId}",
            m.Notes,
            names.GetValueOrDefault(m.CreatedBy, "?")))];
    }

    /// <summary>Permitir stock negativo es una decisión del propietario: exige permiso de configuración y queda auditada.</summary>
    public async Task SetAllowNegativeAsync(bool allow, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageConfiguration);

        var now = time.GetUtcNow();
        var setting = await data.FirstOrDefaultAsync(data.AppSettings.Where(s => s.Key == AppSettingKeys.AllowNegativeStock), cancellationToken);
        var value = allow.ToString().ToLowerInvariant();
        if (setting is null)
        {
            data.Add(new AppSetting(AppSettingKeys.AllowNegativeStock, value, user.UserId, now));
        }
        else
        {
            setting.Change(value, user.UserId, now);
        }

        audit.Write(AuditActions.StockSettingChanged, "setting", AppSettingKeys.AllowNegativeStock, new { allowNegative = allow });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateEntryException ex)
        {
            throw new DomainException("El ajuste se estaba cambiando a la vez. Inténtelo de nuevo.", ex);
        }
    }

    private async Task RegisterAsync(
        int productId,
        StockMovementReason reason,
        decimal delta,
        string? notes,
        bool requireNotes,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        user.Require(Permission.AdjustStock);
        if (await WasProcessedAsync(requestId, cancellationToken))
        {
            return;
        }

        var product = await FindControlledAsync(productId, cancellationToken);
        await ApplyAsync(product, delta, reason, notes, requireNotes, requestId, cancellationToken);
    }

    private async Task ApplyAsync(
        Domain.Catalog.Product product,
        decimal delta,
        StockMovementReason reason,
        string? notes,
        bool requireNotes,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        if (requireNotes && string.IsNullOrWhiteSpace(notes))
        {
            throw new DomainException("Indique el motivo.");
        }

        var now = time.GetUtcNow();
        var before = product.StockQuantity;
        var after = product.ApplyStockDelta(delta, await ledger.AllowNegativeAsync(cancellationToken), now);
        data.Add(StockMovement.Create(product.Id, delta, after, reason, null, null, notes, user.UserId, now));
        data.Add(IdempotencyRecord.Create(requestId, $"stock.{reason}", now));

        audit.Write(AuditActions.StockMovementRegistered, "product", product.Id, new { reason = reason.ToString(), delta, after, notes = notes?.Trim() });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateEntryException ex)
        {
            throw new DomainException("La operación ya se estaba procesando. Compruebe el stock.", ex);
        }

        ledger.RaiseIfLow(product, before, after);
    }

    private async Task<Domain.Catalog.Product> FindControlledAsync(int productId, CancellationToken cancellationToken)
    {
        var product = await data.FirstOrDefaultAsync(data.Products.Where(p => p.Id == productId), cancellationToken)
            ?? throw new NotFoundException("El producto no existe.");
        if (!product.StockControlled)
        {
            throw new DomainException($"«{product.Name}» no lleva control de stock. Actívelo en la ficha del producto.");
        }

        return product;
    }

    private async Task<bool> WasProcessedAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var key = requestId.ToString("N");
        return await data.AnyAsync(data.IdempotencyRecords.Where(r => r.Key == key), cancellationToken);
    }

    private static decimal RequirePositive(decimal quantity) =>
        quantity > 0 && decimal.Round(quantity, 3) == quantity
            ? quantity
            : throw new DomainException("La cantidad debe ser mayor que cero, con un máximo de 3 decimales.");
}
