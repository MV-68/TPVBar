using Microsoft.Extensions.DependencyInjection;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Catalog;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Security;

namespace TPVBar.Integration.Tests;

internal sealed record World(int ZoneId, int TableId, int Table2Id, int CategoryId, int CanaId, int TortillaId);

internal static class SalesHelpers
{
    public static readonly Permission[] Waiter = [Permission.TakeOrders];
    /// <summary>Todos los permisos de operación (comandas, cocina, impresión, catálogo): un administrador de pruebas.</summary>
    public static readonly Permission[] Full =
    [
        Permission.TakeOrders, Permission.VoidLines, Permission.ReopenTables, Permission.ManageCatalog,
        Permission.ModifyPrices, Permission.UseKitchen, Permission.ManagePrinting,
        Permission.TakePayments, Permission.OpenCloseCash, Permission.PerformRefunds,
        Permission.AdjustStock, Permission.ViewReports, Permission.ManageConfiguration,
        Permission.IssueInvoices, Permission.RectifyInvoices,
    ];

    public static readonly Permission[] Kitchen = [Permission.UseKitchen];

    public static readonly Permission[] Manager = [Permission.TakeOrders, Permission.VoidLines, Permission.ReopenTables, Permission.ManageCatalog, Permission.ModifyPrices];

    /// <summary>
    /// Ejecuta un caso de uso en un ámbito nuevo con la identidad dada, igual que el host web.
    /// Reintenta los conflictos de concurrencia con una operación nueva (como UseCaseRunner).
    /// </summary>
    public static async Task<T> RunAsync<TService, T>(
        IServiceProvider services,
        Permission[] permissions,
        Func<TService, Task<T>> operation,
        int maxAttempts = 12)
        where TService : notnull
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CurrentUserAccessor>().Set(Guid.NewGuid(), permissions, "test");
            try
            {
                return await operation(scope.ServiceProvider.GetRequiredService<TService>());
            }
            catch (ConcurrencyConflictException) when (attempt < maxAttempts)
            {
                await Task.Delay(10 * attempt);
            }
        }
    }

    /// <summary>Un bar de prueba con nombres únicos: no interfiere con las demás pruebas.</summary>
    public static async Task<World> CreateWorldAsync(IServiceProvider services)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var ct = CancellationToken.None;

        return await RunAsync<CatalogService, World>(services, Manager, async catalog =>
        {
            var zone = await catalog.CreateZoneAsync("Zona " + id, 1, ct);
            var table1 = await catalog.CreateTableAsync(zone, "Mesa A", 4, 1, ct);
            var table2 = await catalog.CreateTableAsync(zone, "Mesa B", 4, 2, ct);
            var category = await catalog.CreateCategoryAsync("Cat " + id, 1, ct);
            var general = await catalog.CreateTaxRateAsync("General " + id, 21m, new DateOnly(2020, 1, 1), null, ct);
            var reduced = await catalog.CreateTaxRateAsync("Reducido " + id, 10m, new DateOnly(2020, 1, 1), null, ct);
            var cana = await catalog.CreateProductAsync(new ProductInput(category, general, "Caña " + id, 1.80m, PreparationStation.Bar, null, null, 1), ct);
            var tortilla = await catalog.CreateProductAsync(new ProductInput(category, reduced, "Tortilla " + id, 3.50m, PreparationStation.Kitchen, null, null, 2), ct);
            return new World(zone, table1, table2, category, cana, tortilla);
        });
    }
}
