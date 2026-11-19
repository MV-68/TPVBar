using Microsoft.EntityFrameworkCore;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Printing;

namespace TPVBar.Infrastructure.Persistence.Seed;

/// <summary>
/// Impresoras de DEMOSTRACIÓN para desarrollo. Son SIMULADAS: escriben archivos de texto en una carpeta y
/// no son dispositivos reales (la pantalla de impresoras lo indica). Barra y cocina comparten la impresora
/// de cocina por defecto (pregunta pendiente 7 del documento maestro). Solo se cargan si no hay impresoras.
/// </summary>
internal static class DemoPrintersSeeder
{
    public static async Task SeedAsync(TpvBarDbContext db, string? simulatedFolder, CancellationToken cancellationToken)
    {
        if (await db.Printers.AnyAsync(cancellationToken))
        {
            return;
        }

        var folder = string.IsNullOrWhiteSpace(simulatedFolder)
            ? Path.Combine(Path.GetTempPath(), "tpvbar-print")
            : simulatedFolder;

        var receipt = Printer.Create("Tickets (simulada)", PrinterType.Receipt, PrinterConnection.Simulated, Path.Combine(folder, "tickets"), 80);
        var kitchen = Printer.Create("Cocina (simulada)", PrinterType.Kitchen, PrinterConnection.Simulated, Path.Combine(folder, "cocina"), 80);
        db.Printers.AddRange(receipt, kitchen);
        await db.SaveChangesAsync(cancellationToken);

        db.PrinterRoutes.AddRange(
            new PrinterRoute(PreparationStation.Kitchen, kitchen.Id),
            new PrinterRoute(PreparationStation.Bar, kitchen.Id));
        await db.SaveChangesAsync(cancellationToken);
    }
}
