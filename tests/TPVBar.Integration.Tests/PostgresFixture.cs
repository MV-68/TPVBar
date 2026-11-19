using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TPVBar.Application;
using TPVBar.Application.Features.Authentication;
using TPVBar.Infrastructure;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Integration.Tests;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>
/// Base de datos PostgreSQL REAL de pruebas (no SQLite ni en memoria). Se recrea desde las migraciones
/// al empezar la ejecución. La cadena de conexión viene de la variable TPVBAR_TEST_CONNECTION o de los
/// User Secrets (ConnectionStrings:TpvBarTest, creada por scripts/dev-db.ps1). Es una base de datos
/// de desarrollo local: se BORRA y se vuelve a crear en cada ejecución.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _web;

    public string ConnectionString { get; private set; } = string.Empty;

    public string WebUserPassword { get; } = "Pw-" + Guid.NewGuid().ToString("N");

    /// <summary>Clave de la API del agente de impresión usada por el servidor de pruebas.</summary>
    public string AgentKey { get; } = "k-" + Guid.NewGuid().ToString("N");

    /// <summary>Carpeta donde las impresoras simuladas de demostración escriben sus tickets.</summary>
    public string PrintFolder { get; } = Path.Combine(Path.GetTempPath(), "tpvbar-tests-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Archivo de estado de copias que lee la pantalla de diagnóstico (no existe hasta que una prueba lo crea).</summary>
    public string BackupStatusFile { get; } = Path.Combine(Path.GetTempPath(), "tpvbar-backup-" + Guid.NewGuid().ToString("N")[..8] + ".json");

    public WebApplicationFactory<Program> Web =>
        _web ?? throw new InvalidOperationException("El fixture no está inicializado.");

    public async ValueTask InitializeAsync()
    {
        ConnectionString = ResolveConnectionString();

        await using (var services = BuildServices())
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
            await db.Database.EnsureDeletedAsync();
            await DatabaseInitializer.MigrateAsync(services);
            await DatabaseInitializer.SeedReferenceDataAsync(services);
        }

        _web = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:TpvBar", ConnectionString);
            builder.UseSetting("Seed:DevUserPassword", WebUserPassword);
            builder.UseSetting("Security:LoginRateLimitPerMinute", "100000");
            builder.UseSetting("PrintAgent:ApiKey", AgentKey);
            builder.UseSetting("Seed:SimulatedPrintFolder", PrintFolder);
            builder.UseSetting("Backup:StatusFile", BackupStatusFile);
        });

        // El host se crea de forma diferida: se fuerza ahora para que el seed de demostración (catálogo,
        // impresoras, usuarios) se ejecute antes que cualquier prueba cree sus propios datos.
        _ = _web.Services;
    }

    public async ValueTask DisposeAsync()
    {
        if (_web is not null)
        {
            await _web.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
        File.Delete(BackupStatusFile);

        try
        {
            if (Directory.Exists(PrintFolder))
            {
                Directory.Delete(PrintFolder, recursive: true);
            }
        }
        catch (IOException)
        {
            // Carpeta temporal: si algo la mantiene abierta, el sistema la limpiará.
        }
    }

    /// <summary>Contenedor de servicios independiente de la web, para probar Application + Infrastructure directamente.</summary>
    public ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TpvBar"] = ConnectionString })
            .Build();

        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddTpvBarApplication()
            .AddTpvBarInfrastructure()
            .BuildServiceProvider();
    }

    /// <summary>Crea un usuario nuevo con nombre único (para que las pruebas no se estorben).</summary>
    public async Task<(string UserName, string Password)> CreateUserAsync(string role, IServiceProvider? services = null)
    {
        var userName = "t" + Guid.NewGuid().ToString("N")[..12];
        var password = "Pw-" + Guid.NewGuid().ToString("N");

        var provider = services ?? Web.Services;
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<UserAdministrationService>()
            .CreateUserAsync(userName, "Usuario " + userName, password, [role], null, CancellationToken.None);

        return (userName, password);
    }

    private static string ResolveConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<PostgresFixture>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connection = configuration["TPVBAR_TEST_CONNECTION"] ?? configuration["ConnectionStrings:TpvBarTest"];
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException(
                "Faltan las pruebas de integración contra PostgreSQL real: defina TPVBAR_TEST_CONNECTION o ejecute scripts/dev-db.ps1 " +
                "(crea la base 'tpvbar_test' y el secreto ConnectionStrings:TpvBarTest). No se omiten en silencio.");
        }

        return connection;
    }
}
