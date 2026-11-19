using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Inventory;
using TPVBar.Domain.Sales;

namespace TPVBar.Application.Features.Inventory;

/// <summary>
/// Reglas del inventario en un solo lugar. Política única y coherente: el stock se descuenta al ENVIAR la línea
/// a cocina o barra (es cuando el producto sale) y se repone si esa línea se anula. Cada movimiento ligado a una
/// línea es único por motivo y línea en la base de datos, así que un reintento no puede descontar dos veces.
/// </summary>
public sealed class StockLedger(IAppData data, ICurrentUser user, IOperationEvents events, TimeProvider time)
{
    public const string OrderLineReference = "order_line";

    public async Task<bool> AllowNegativeAsync(CancellationToken cancellationToken)
    {
        var setting = await data.FirstOrDefaultAsync(data.NoTracking(data.AppSettings).Where(s => s.Key == AppSettingKeys.AllowNegativeStock), cancellationToken);
        return setting is not null && bool.TryParse(setting.Value, out var allowed) && allowed;
    }

    /// <summary>Descuenta las líneas recién enviadas de los productos con control de stock.</summary>
    public async Task DeductForSendAsync(IReadOnlyList<OrderLine> sentLines, CancellationToken cancellationToken)
    {
        var productIds = sentLines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).Distinct().ToList();
        if (productIds.Count == 0)
        {
            return;
        }

        var products = (await data.ToListAsync(data.Products.Where(p => productIds.Contains(p.Id) && p.StockControlled), cancellationToken))
            .ToDictionary(p => p.Id);
        if (products.Count == 0)
        {
            return;
        }

        var allowNegative = await AllowNegativeAsync(cancellationToken);
        var now = time.GetUtcNow();
        foreach (var line in sentLines.Where(l => l.ProductId is not null && products.ContainsKey(l.ProductId.Value)))
        {
            var product = products[line.ProductId!.Value];
            var before = product.StockQuantity;
            var after = product.ApplyStockDelta(-line.Quantity, allowNegative, now);
            data.Add(StockMovement.Create(product.Id, -line.Quantity, after, StockMovementReason.Sale, OrderLineReference, Id(line), null, user.UserId, now));
            RaiseIfLow(product, before, after);
        }
    }

    /// <summary>
    /// Repone el stock de una línea anulada, solo si esa línea llegó a descontarse (existe su movimiento de venta).
    /// Así activar el control de stock después de vender no infla las existencias al anular.
    /// </summary>
    public async Task ReturnForVoidAsync(OrderLine line, CancellationToken cancellationToken)
    {
        if (line.ProductId is not { } productId)
        {
            return;
        }

        var reference = Id(line);
        var deducted = await data.AnyAsync(
            data.NoTracking(data.StockMovements).Where(m => m.Reason == StockMovementReason.Sale && m.ReferenceType == OrderLineReference && m.ReferenceId == reference),
            cancellationToken);
        if (!deducted)
        {
            return;
        }

        var product = await data.FirstOrDefaultAsync(data.Products.Where(p => p.Id == productId), cancellationToken);
        if (product is null || !product.StockControlled)
        {
            return;
        }

        var now = time.GetUtcNow();
        var after = product.ApplyStockDelta(line.Quantity, allowNegative: true, now);
        data.Add(StockMovement.Create(product.Id, line.Quantity, after, StockMovementReason.VoidReturn, OrderLineReference, reference, "Anulación de una línea enviada", user.UserId, now));
        events.Raise(new OperationEvent(OperationEventNames.StockChanged, ProductId: product.Id));
    }

    /// <summary>Avisa una sola vez al cruzar el mínimo (no en cada venta posterior).</summary>
    internal void RaiseIfLow(Product product, decimal before, decimal after)
    {
        events.Raise(new OperationEvent(OperationEventNames.StockChanged, ProductId: product.Id));
        if (after <= product.StockMinimum && before > product.StockMinimum)
        {
            events.Raise(new OperationEvent(OperationEventNames.StockAlertRaised, ProductId: product.Id));
        }
    }

    private static string Id(OrderLine line) => line.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
