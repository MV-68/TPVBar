using Microsoft.EntityFrameworkCore;
using TPVBar.Domain.Inventory;

namespace TPVBar.Infrastructure.Persistence.Seed;

/// <summary>
/// Existencias de DEMOSTRACIÓN para desarrollo (productos ficticios). Solo si ningún producto lleva aún control
/// de stock. Cada existencia inicial se registra como movimiento «Entrada» para que el saldo siempre cuadre con
/// el historial. El usuario del movimiento es el identificador vacío (sistema).
/// </summary>
internal static class DemoStockSeeder
{
    public static async Task SeedAsync(TpvBarDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await db.Products.AnyAsync(p => p.StockControlled, cancellationToken))
        {
            return;
        }

        var now = time.GetUtcNow();
        (string Name, decimal Quantity, decimal Minimum)[] demo =
        [
            ("Agua 0,5 L", 24m, 6m),
            ("Refresco", 24m, 6m),
            ("Cerveza sin alcohol", 12m, 4m),
            ("Tarta de queso", 6m, 2m),
        ];

        var names = demo.Select(d => d.Name).ToList();
        var products = await db.Products.Where(p => names.Contains(p.Name)).ToListAsync(cancellationToken);
        foreach (var product in products)
        {
            var (_, quantity, minimum) = demo.Single(d => d.Name == product.Name);
            product.ConfigureStock(true, minimum, "unidad", now);
            var after = product.ApplyStockDelta(quantity, allowNegative: false, now);
            db.StockMovements.Add(StockMovement.Create(product.Id, quantity, after, StockMovementReason.Entry, null, null, "Existencias iniciales de demostración", Guid.Empty, now));
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
