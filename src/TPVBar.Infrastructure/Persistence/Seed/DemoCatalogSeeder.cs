using Microsoft.EntityFrameworkCore;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Floor;

namespace TPVBar.Infrastructure.Persistence.Seed;

/// <summary>
/// Datos de demostración FICTICIOS para desarrollo: zonas, mesas, categorías, tipos impositivos y productos.
/// Los tipos impositivos son de ejemplo y NO constituyen una configuración fiscal validada.
/// Solo se cargan si la base de datos aún no tiene zonas, y nunca en producción.
/// </summary>
internal static class DemoCatalogSeeder
{
    public static async Task SeedAsync(TpvBarDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await db.Zones.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = time.GetUtcNow();
        var since = new DateOnly(2020, 1, 1);

        var reduced = TaxRate.Create("Reducido (demo)", 10m, since);
        var general = TaxRate.Create("General (demo)", 21m, since);
        var superReduced = TaxRate.Create("Superreducido (demo)", 4m, since);
        db.TaxRates.AddRange(reduced, general, superReduced);

        var barra = Zone.Create("Barra", 1, now);
        var salon = Zone.Create("Salón", 2, now);
        var terraza = Zone.Create("Terraza", 3, now);
        db.Zones.AddRange(barra, salon, terraza);
        await db.SaveChangesAsync(cancellationToken);

        AddTables(db, barra.Id, "Barra", 4, 1);
        AddTables(db, salon.Id, "Mesa", 6, 4);
        AddTables(db, terraza.Id, "Terraza", 4, 4);

        var cafes = Category.Create("Cafés", 1);
        var bebidas = Category.Create("Bebidas", 2);
        var cervezas = Category.Create("Cervezas", 3);
        var tapas = Category.Create("Tapas", 4);
        var raciones = Category.Create("Raciones", 5);
        var postres = Category.Create("Postres", 6);
        db.Categories.AddRange(cafes, bebidas, cervezas, tapas, raciones, postres);
        await db.SaveChangesAsync(cancellationToken);

        var order = 0;
        void Add(Category category, TaxRate tax, string name, decimal price, PreparationStation station) =>
            db.Products.Add(Product.Create(category.Id, tax.Id, name, price, station, null, null, ++order, now));

        Add(cafes, reduced, "Café solo", 1.30m, PreparationStation.Bar);
        Add(cafes, reduced, "Café con leche", 1.60m, PreparationStation.Bar);
        Add(cafes, reduced, "Cortado", 1.40m, PreparationStation.Bar);
        Add(cafes, reduced, "Capuchino", 2.00m, PreparationStation.Bar);

        Add(bebidas, reduced, "Agua 0,5 L", 1.50m, PreparationStation.Bar);
        Add(bebidas, reduced, "Refresco", 2.20m, PreparationStation.Bar);
        Add(bebidas, reduced, "Zumo de naranja", 2.80m, PreparationStation.Bar);

        Add(cervezas, general, "Caña", 1.80m, PreparationStation.Bar);
        Add(cervezas, general, "Doble", 3.00m, PreparationStation.Bar);
        Add(cervezas, general, "Cerveza sin alcohol", 2.30m, PreparationStation.Bar);

        Add(tapas, reduced, "Tortilla de patatas", 3.50m, PreparationStation.Kitchen);
        Add(tapas, reduced, "Croquetas (4)", 3.20m, PreparationStation.Kitchen);
        Add(tapas, reduced, "Ensaladilla", 3.00m, PreparationStation.Kitchen);

        Add(raciones, reduced, "Calamares", 11.50m, PreparationStation.Kitchen);
        Add(raciones, reduced, "Patatas bravas", 6.50m, PreparationStation.Kitchen);
        Add(raciones, reduced, "Jamón ibérico", 16.00m, PreparationStation.Kitchen);

        Add(postres, reduced, "Tarta de queso", 4.50m, PreparationStation.Kitchen);
        Add(postres, reduced, "Flan", 3.20m, PreparationStation.Kitchen);

        _ = superReduced;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void AddTables(TpvBarDbContext db, int zoneId, string prefix, int count, int capacity)
    {
        for (var i = 1; i <= count; i++)
        {
            db.Tables.Add(DiningTable.Create(zoneId, $"{prefix} {i}", capacity, i));
        }
    }
}
