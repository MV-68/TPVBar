using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TPVBar.Application.Abstractions;
using TPVBar.Application.Features.Authentication;
using TPVBar.Infrastructure.Persistence.Seed;

namespace TPVBar.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    /// <summary>Aplica las migraciones pendientes (acción explícita: --migrate o desarrollo).</summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();

        // Con una cadena de conexión de migraciones (rol propietario) las migraciones se aplican con ese rol
        // y la aplicación puede funcionar con un rol de solo datos (ver scripts\db-roles.sql).
        var migrationsConnection = scope.ServiceProvider.GetRequiredService<IConfiguration>()
            .GetConnectionString(MigrationsConnectionStringName);
        if (string.IsNullOrWhiteSpace(migrationsConnection))
        {
            var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
            return;
        }

        var options = new DbContextOptionsBuilder<TpvBarDbContext>()
            .UseNpgsql(migrationsConnection, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", TpvBarDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var owner = new TpvBarDbContext(options);
        await owner.Database.MigrateAsync(cancellationToken);
    }

    public const string MigrationsConnectionStringName = "TpvBarMigrations";

    /// <summary>Datos de referencia (roles). Seguro en cualquier entorno y repetible.</summary>
    public static async Task SeedReferenceDataAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        await ReferenceDataSeeder.SeedAsync(db, cancellationToken);
    }

    /// <summary>Usuarios de prueba. El llamador debe invocarlo solo en desarrollo.</summary>
    public static async Task SeedDevelopmentDataAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        await DemoCatalogSeeder.SeedAsync(
            provider.GetRequiredService<TpvBarDbContext>(),
            provider.GetRequiredService<TimeProvider>(),
            cancellationToken);
        await DemoStockSeeder.SeedAsync(
            provider.GetRequiredService<TpvBarDbContext>(),
            provider.GetRequiredService<TimeProvider>(),
            cancellationToken);
        await DemoPrintersSeeder.SeedAsync(
            provider.GetRequiredService<TpvBarDbContext>(),
            provider.GetRequiredService<IConfiguration>()["Seed:SimulatedPrintFolder"],
            cancellationToken);
        await DevelopmentDataSeeder.SeedAsync(
            provider.GetRequiredService<UserAdministrationService>(),
            provider.GetRequiredService<IUserStore>(),
            provider.GetRequiredService<IConfiguration>()["Seed:DevUserPassword"],
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DevelopmentDataSeeder)),
            cancellationToken);
    }
}
