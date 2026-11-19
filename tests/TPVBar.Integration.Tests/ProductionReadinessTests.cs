using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TPVBar.Domain.Security;
using TPVBar.Infrastructure;
using TPVBar.Infrastructure.Persistence;
using TPVBar.Web.Diagnostics;
using TPVBar.Web.Realtime;

namespace TPVBar.Integration.Tests;

/// <summary>Preparación de producción (Fase 6): salud, cabeceras, diagnóstico, estado de copias, cookie y migraciones.</summary>
[Collection(PostgresCollection.Name)]
public sealed partial class ProductionReadinessTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    private static HttpClient NewClient(WebApplicationFactory<Program> web) =>
        web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private async Task<HttpResponseMessage> LoginResponseAsync(WebApplicationFactory<Program> web, HttpClient client, string role)
    {
        var (user, password) = await fixture.CreateUserAsync(role);
        var token = WebUtility.HtmlDecode(TokenRegex().Match(await client.GetStringAsync("/login", Ct)).Groups[1].Value);
        _ = web;
        return await client.PostAsync("/auth/login", new FormUrlEncodedContent([new("username", user), new("password", password), new("__RequestVerificationToken", token)]), Ct);
    }

    private async Task<HttpClient> LoginAsync(string role)
    {
        var client = NewClient(fixture.Web);
        using var response = await LoginResponseAsync(fixture.Web, client, role);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private static DiagnosticsService NewDiagnostics(AsyncServiceScope scope, string? statusFile)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(statusFile is null ? [] : new Dictionary<string, string?> { ["Backup:StatusFile"] = statusFile })
            .Build();
        return new DiagnosticsService(
            scope.ServiceProvider.GetRequiredService<TpvBarDbContext>(),
            new ConnectionTracker(),
            configuration,
            scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>(),
            TimeProvider.System);
    }

    private static string PathOf(HttpResponseMessage response)
    {
        var location = response.Headers.Location ?? throw new InvalidOperationException("Sin Location.");
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    private static string WriteStatus(bool success, DateTimeOffset finishedAt, bool secondary, string message = "ok")
    {
        var path = Path.Combine(Path.GetTempPath(), "tpvbar-status-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            success,
            finishedAt,
            fileName = "tpvbar_prueba.dump",
            sizeBytes = 2048,
            verified = success,
            copiedToSecondaryLocation = secondary,
            message,
        }));
        return path;
    }

    // ---------- Salud ----------

    [Fact]
    public async Task Health_EsAnonimoYNoRevelaDetalles()
    {
        using var client = NewClient(fixture.Web);

        using var live = await client.GetAsync("/health", Ct);
        using var ready = await client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var body = await ready.Content.ReadAsStringAsync(Ct);
        Assert.Equal("Healthy", body);
        Assert.DoesNotContain("Host=", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Cabeceras de seguridad ----------

    [Fact]
    public async Task LasRespuestas_LlevanCabecerasDeSeguridad()
    {
        using var client = NewClient(fixture.Web);

        using var response = await client.GetAsync("/login", Ct);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
    }

    // ---------- Cookie ----------

    [Fact]
    public async Task ConRequireHttps_LaCookieDeSesionEsSecure()
    {
        await using var secureWeb = fixture.Web.WithWebHostBuilder(b => b.UseSetting("Security:RequireHttps", "true"));
        using var client = NewClient(secureWeb);

        using var response = await LoginResponseAsync(secureWeb, client, DefaultRoles.Manager);

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("tpvbar.auth=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SinRequireHttps_ElDesarrolloPorHttpSigueFuncionando()
    {
        using var client = NewClient(fixture.Web);

        using var response = await LoginResponseAsync(fixture.Web, client, DefaultRoles.Manager);

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("tpvbar.auth=", StringComparison.Ordinal));
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Pantalla de diagnóstico ----------

    [Fact]
    public async Task Diagnostico_ExigeAdministracion()
    {
        using var manager = await LoginAsync(DefaultRoles.Manager);
        using var waiter = await LoginAsync(DefaultRoles.Waiter);
        using var anonymous = NewClient(fixture.Web);

        using var asManager = await manager.GetAsync("/configuracion/diagnostico", Ct);
        using var asWaiter = await waiter.GetAsync("/configuracion/diagnostico", Ct);
        using var asAnonymous = await anonymous.GetAsync("/configuracion/diagnostico", Ct);

        Assert.Equal(HttpStatusCode.Redirect, asManager.StatusCode);
        Assert.StartsWith("/acceso-denegado", PathOf(asManager), StringComparison.Ordinal);
        Assert.StartsWith("/acceso-denegado", PathOf(asWaiter), StringComparison.Ordinal);
        Assert.StartsWith("/login", PathOf(asAnonymous), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostico_ElAdministradorVeEstadosSinSecretos()
    {
        using var admin = await LoginAsync(DefaultRoles.Administrator);

        using var response = await admin.GetAsync("/configuracion/diagnostico", Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("PostgreSQL", html, StringComparison.Ordinal);
        Assert.Contains("Migraciones pendientes", html, StringComparison.Ordinal);
        Assert.Contains("Agente de impresión", html, StringComparison.Ordinal);
        Assert.Contains("Copia de seguridad", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.AgentKey, html, StringComparison.Ordinal);
        Assert.DoesNotContain("tpvbar_app", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostico_LeeLaBaseDeDatosReal()
    {
        await using var scope = fixture.Web.Services.CreateAsyncScope();

        var snapshot = await NewDiagnostics(scope, null).GetAsync(Ct);

        Assert.True(snapshot.Database.Reachable);
        Assert.Equal(0, snapshot.Database.PendingMigrations);
        Assert.NotNull(snapshot.Database.LastMigration);
        Assert.NotNull(snapshot.Database.ServerVersion);
        Assert.NotNull(snapshot.Print);
        Assert.True(snapshot.Database.LatencyMs >= 0);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.AppVersion));
    }

    [Fact]
    public async Task Diagnostico_SinConfigurarCopias_Avisa()
    {
        await using var scope = fixture.Web.Services.CreateAsyncScope();

        var snapshot = await NewDiagnostics(scope, null).GetAsync(Ct);

        Assert.Equal(BackupStatusKind.NotConfigured, snapshot.Backup.Kind);
        Assert.Contains(snapshot.Warnings, w => w.Contains("Backup:StatusFile", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diagnostico_CopiaNuncaEjecutada_Avisa()
    {
        await using var scope = fixture.Web.Services.CreateAsyncScope();

        var snapshot = await NewDiagnostics(scope, Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N") + ".json")).GetAsync(Ct);

        Assert.Equal(BackupStatusKind.NeverRun, snapshot.Backup.Kind);
        Assert.Contains(snapshot.Warnings, w => w.Contains("ninguna copia", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diagnostico_CopiaCorrectaYDuplicada_NoAvisa()
    {
        var file = WriteStatus(success: true, DateTimeOffset.UtcNow.AddHours(-2), secondary: true);
        await using var scope = fixture.Web.Services.CreateAsyncScope();

        var snapshot = await NewDiagnostics(scope, file).GetAsync(Ct);

        Assert.Equal(BackupStatusKind.Available, snapshot.Backup.Kind);
        Assert.False(snapshot.BackupIsStale);
        Assert.DoesNotContain(snapshot.Warnings, w => w.Contains("copia", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Diagnostico_CopiaSinUbicacionSecundaria_Avisa()
    {
        var file = WriteStatus(success: true, DateTimeOffset.UtcNow.AddHours(-1), secondary: false);
        await using var scope = fixture.Web.Services.CreateAsyncScope();

        var snapshot = await NewDiagnostics(scope, file).GetAsync(Ct);

        Assert.Contains(snapshot.Warnings, w => w.Contains("otra ubicación", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diagnostico_CopiaObsoleta_Avisa()
    {
        var file = WriteStatus(success: true, DateTimeOffset.UtcNow.AddHours(-31), secondary: true);
        await using var scope = fixture.Web.Services.CreateAsyncScope();

        var snapshot = await NewDiagnostics(scope, file).GetAsync(Ct);

        Assert.True(snapshot.BackupIsStale);
        Assert.Contains(snapshot.Warnings, w => w.Contains("30 horas", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diagnostico_CopiaFallida_Avisa()
    {
        var file = WriteStatus(success: false, DateTimeOffset.UtcNow.AddMinutes(-5), secondary: false, message: "pg_dump falló");
        await using var scope = fixture.Web.Services.CreateAsyncScope();

        var snapshot = await NewDiagnostics(scope, file).GetAsync(Ct);

        Assert.Contains(snapshot.Warnings, w => w.Contains("falló", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diagnostico_EstadoDeCopiaIlegible_NoRompeLaPantalla()
    {
        var file = Path.Combine(Path.GetTempPath(), "tpvbar-roto-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(file, "{ esto no es json");
        await using var scope = fixture.Web.Services.CreateAsyncScope();

        var snapshot = await NewDiagnostics(scope, file).GetAsync(Ct);

        Assert.Equal(BackupStatusKind.Unreadable, snapshot.Backup.Kind);
        Assert.Contains(snapshot.Warnings, w => w.Contains("No se puede leer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diagnostico_ElTextoDelEstadoNoSeInterpretaComoHtml()
    {
        var file = WriteStatus(success: false, DateTimeOffset.UtcNow, secondary: false, message: "<script>alert(1)</script>");
        await using var scope = fixture.Web.Services.CreateAsyncScope();
        var snapshot = await NewDiagnostics(scope, file).GetAsync(Ct);

        // El mensaje llega tal cual como DATO; la página lo muestra con la codificación de Razor.
        Assert.Equal("<script>alert(1)</script>", snapshot.Backup.Status!.Message);
        using var admin = await LoginAsync(DefaultRoles.Administrator);
        File.Copy(file, fixture.BackupStatusFile, overwrite: true);
        try
        {
            var html = await admin.GetStringAsync("/configuracion/diagnostico", Ct);
            Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(fixture.BackupStatusFile);
        }
    }

    // ---------- Contadores ----------

    [Fact]
    public void ConnectionTracker_NuncaBajaDeCero()
    {
        var tracker = new ConnectionTracker();

        tracker.Closed();
        tracker.CircuitClosed();
        tracker.Opened();
        tracker.Opened();
        tracker.CircuitOpened();
        tracker.Closed();

        Assert.Equal(1, tracker.Count);
        Assert.Equal(1, tracker.Circuits);
        tracker.Closed();
        tracker.Closed();
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public async Task ElManejadorDeCircuitos_CuentaPantallasAbiertasYCerradas()
    {
        var tracker = new ConnectionTracker();
        var handler = new CircuitCountHandler(tracker);

        await handler.OnCircuitOpenedAsync(null!, Ct);
        await handler.OnCircuitOpenedAsync(null!, Ct);
        await handler.OnCircuitClosedAsync(null!, Ct);

        Assert.Equal(1, tracker.Circuits);
        Assert.Equal(0, tracker.Count);
    }
    // ---------- Migraciones con rol propietario ----------

    [Fact]
    public async Task Migrar_UsaLaCadenaDeMigracionesSiExiste()
    {
        var invalid = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TpvBar"] = fixture.ConnectionString,
                ["ConnectionStrings:TpvBarMigrations"] = "Host=localhost;Port=1;Database=no_existe;Username=x;Password=y;Timeout=2;Command Timeout=2",
            })
            .Build();
        await using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(invalid)
            .AddLogging()
            .AddTpvBarInfrastructure()
            .BuildServiceProvider();

        // Si se usara la cadena de la aplicación (válida), no fallaría: falla porque se usa la de migraciones.
        await Assert.ThrowsAnyAsync<Exception>(() => DatabaseInitializer.MigrateAsync(services, Ct));
    }

    [Fact]
    public async Task Migrar_ConLaCadenaDeMigracionesCorrecta_FuncionaYEsIdempotente()
    {
        var valid = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TpvBar"] = "Host=localhost;Port=1;Database=no_existe;Username=x;Password=y;Timeout=2",
                ["ConnectionStrings:TpvBarMigrations"] = fixture.ConnectionString,
            })
            .Build();
        await using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(valid)
            .AddLogging()
            .AddTpvBarInfrastructure()
            .BuildServiceProvider();

        await DatabaseInitializer.MigrateAsync(services, Ct);

        await using var scope = fixture.Web.Services.CreateAsyncScope();
        var snapshot = await NewDiagnostics(scope, null).GetAsync(Ct);
        Assert.Equal(0, snapshot.Database.PendingMigrations);
    }
}

