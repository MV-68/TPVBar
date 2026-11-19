extern alias agent;

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using agent::TPVBar.PrintAgent;
using agent::TPVBar.PrintAgent.Workers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TPVBar.Application.Features.Cash;
using TPVBar.Domain.Printing;
using TPVBar.Domain.Security;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Integration.Tests;

/// <summary>Cobro → justificante y cierre → resumen, con piezas reales hasta la impresora simulada, y permisos de pantalla.</summary>
[Collection(PostgresCollection.Name)]
public sealed partial class CashEndToEndTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => fixture.Web.Services;

    /// <summary>Los tickets parten el texto a 48 columnas: se compara sin saltos de línea.</summary>
    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    private PrintLoopWorker NewAgent()
    {
        var options = new AgentOptions { ServerUrl = "http://localhost", ApiKey = fixture.AgentKey, Name = "agente-caja" };
        return new PrintLoopWorker(new ServerClient(fixture.Web.CreateClient(), Options.Create(options)), new PrintJobProcessor(), Options.Create(options), NullLogger<PrintLoopWorker>.Instance);
    }

    private async Task RunAgentUntilAsync(Func<Task<bool>> done)
    {
        var worker = NewAgent();
        for (var i = 0; i < 30; i++)
        {
            await worker.RunOnceAsync(Ct);
            if (await done())
            {
                return;
            }
        }

        throw new InvalidOperationException("El agente no completó los trabajos esperados.");
    }

    private async Task<List<PrintJob>> JobsAsync(PrintJobType type, string referenceId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().PrintJobs.AsNoTracking()
            .Where(j => j.JobType == type && j.ReferenceId == referenceId).ToListAsync(Ct);
    }

    [Fact]
    public async Task Cobrar_ImprimeElJustificanteConDesgloseDeIvaPagosYSinPretenderSerFactura()
    {
        await CashHelpers.EnsureOpenAsync(Services);
        var orderId = await CashHelpers.SentOrderAsync(Services);
        await CashHelpers.PayAsync(Services, orderId, "CASH", 4.00m, tendered: 5m);
        await CashHelpers.PayAsync(Services, orderId, "CARD", 3.10m);
        var reference = orderId.ToString(CultureInfo.InvariantCulture);

        await RunAgentUntilAsync(async () => (await JobsAsync(PrintJobType.Receipt, reference)) is [{ Status: PrintJobStatus.Sent }]);

        var receipt = Directory.GetFiles(Path.Combine(fixture.PrintFolder, "tickets"), "*.txt")
            .Select(File.ReadAllText).Single(t => t.Contains("JUSTIFICANTE DE PAGO", StringComparison.Ordinal) && t.Contains($"nº {orderId}", StringComparison.Ordinal));
        Assert.Contains("TOTAL", receipt, StringComparison.Ordinal);
        Assert.Contains("7,10 €", receipt, StringComparison.Ordinal);
        Assert.Contains("IVA 21 %", receipt, StringComparison.Ordinal);
        Assert.Contains("IVA 10 %", receipt, StringComparison.Ordinal);
        Assert.Contains("Efectivo", receipt, StringComparison.Ordinal);
        Assert.Contains("Tarjeta", receipt, StringComparison.Ordinal);
        Assert.Contains("Entregado 5,00 €", receipt, StringComparison.Ordinal);
        Assert.Contains("No es una factura ni un documento fiscal", Flat(receipt), StringComparison.Ordinal);
        Assert.Contains("IMPRESORA SIMULADA", receipt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReimprimirElJustificante_GeneraUnaCopiaMarcadaYAuditada()
    {
        await CashHelpers.EnsureOpenAsync(Services);
        var orderId = await CashHelpers.SentOrderAsync(Services);
        await CashHelpers.PayAsync(Services, orderId, "CARD", 7.10m);

        var queued = await CashHelpers.Full<PaymentService, bool>(Services, p => p.ReprintReceiptAsync(orderId, Ct));

        Assert.True(queued);
        var jobs = await JobsAsync(PrintJobType.Receipt, orderId.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(2, jobs.Count);
        await RunAgentUntilAsync(async () => (await JobsAsync(PrintJobType.Receipt, orderId.ToString(CultureInfo.InvariantCulture))).All(j => j.Status == PrintJobStatus.Sent));
        var copies = Directory.GetFiles(Path.Combine(fixture.PrintFolder, "tickets"), "*.txt")
            .Select(File.ReadAllText).Count(t => t.Contains($"nº {orderId} · COPIA", StringComparison.Ordinal));
        Assert.Equal(1, copies);
    }

    [Fact]
    public async Task CerrarLaCaja_ImprimeElResumenInternoConLaDiferencia()
    {
        var sessionId = await CashHelpers.FreshSessionAsync(Services, 50m);
        var orderId = await CashHelpers.SentOrderAsync(Services);
        await CashHelpers.PayAsync(Services, orderId, "CASH", 7.10m);

        var closed = await CashHelpers.Full<CashService, CloseResult>(Services, c => c.CloseAsync(57.00m, "prueba", Ct));

        Assert.True(closed.ReportPrinted);
        Assert.Equal(-0.10m, closed.Report.Difference);
        var reference = sessionId.ToString(CultureInfo.InvariantCulture);
        await RunAgentUntilAsync(async () => (await JobsAsync(PrintJobType.CashClose, reference)) is [{ Status: PrintJobStatus.Sent }]);
        var summary = Directory.GetFiles(Path.Combine(fixture.PrintFolder, "tickets"), "*.txt")
            .Select(File.ReadAllText).Single(t => t.Contains("CIERRE DE CAJA", StringComparison.Ordinal) && t.Contains($"Sesión {sessionId} ", StringComparison.Ordinal));
        Assert.Contains("Efectivo esperado", summary, StringComparison.Ordinal);
        Assert.Contains("57,10 €", summary, StringComparison.Ordinal);
        Assert.Contains("DIFERENCIA", summary, StringComparison.Ordinal);
        Assert.Contains("-0,10 €", summary, StringComparison.Ordinal);
        Assert.Contains("No es un documento fiscal", Flat(summary), StringComparison.Ordinal);
    }

    // ---------- Pantallas y permisos ----------

    private HttpClient NewClient() => fixture.Web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private async Task<HttpClient> LoginAsync(string role)
    {
        var (user, password) = await fixture.CreateUserAsync(role);
        var client = NewClient();
        var token = WebUtility.HtmlDecode(TokenRegex().Match(await client.GetStringAsync("/login", Ct)).Groups[1].Value);
        using var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent([new("username", user), new("password", password), new("__RequestVerificationToken", token)]), Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private static string LocationOf(HttpResponseMessage response)
    {
        var location = response.Headers.Location ?? throw new InvalidOperationException("Sin Location.");
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    [Theory]
    [InlineData("/caja")]
    [InlineData("/cobro/1")]
    [InlineData("/configuracion/metodos-pago")]
    public async Task SinSesion_LasPantallasDeDineroRedirigenAlLogin(string path)
    {
        using var client = NewClient();

        var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", LocationOf(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caja_LaVenCajaCamareroYEncargadoPeroNoCocina()
    {
        using var cajero = await LoginAsync(DefaultRoles.Cashier);
        using var camarero = await LoginAsync(DefaultRoles.Waiter);
        using var cocina = await LoginAsync(DefaultRoles.Kitchen);

        var html = WebUtility.HtmlDecode(await cajero.GetStringAsync("/caja", Ct));
        Assert.Contains("Caja", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await camarero.GetAsync("/caja", Ct)).StatusCode);
        var denied = await cocina.GetAsync("/caja", Ct);
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.StartsWith("/acceso-denegado", LocationOf(denied), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cobro_LaPantallaLaVeQuienCobraYMuestraLaAdvertenciaDeLaTarjeta()
    {
        await CashHelpers.EnsureOpenAsync(Services);
        var orderId = await CashHelpers.SentOrderAsync(Services);
        using var camarero = await LoginAsync(DefaultRoles.Waiter);
        using var cocina = await LoginAsync(DefaultRoles.Kitchen);

        var html = WebUtility.HtmlDecode(await camarero.GetStringAsync($"/cobro/{orderId}", Ct));

        Assert.Contains("Cobro", html, StringComparison.Ordinal);
        Assert.Contains("Pendiente", html, StringComparison.Ordinal);
        Assert.Contains("7,10", html, StringComparison.Ordinal);
        Assert.Contains("Efectivo", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, (await cocina.GetAsync($"/cobro/{orderId}", Ct)).StatusCode);
        await CashHelpers.PayAsync(Services, orderId, "CARD", 7.10m);
    }

    [Fact]
    public async Task MetodosDePago_SoloLosGestionaQuienGestionaElCatalogo()
    {
        using var encargado = await LoginAsync(DefaultRoles.Manager);
        using var camarero = await LoginAsync(DefaultRoles.Waiter);

        var html = WebUtility.HtmlDecode(await encargado.GetStringAsync("/configuracion/metodos-pago", Ct));

        Assert.Contains("Efectivo", html, StringComparison.Ordinal);
        Assert.Contains("no</strong> se comunica con el datáfono", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, (await camarero.GetAsync("/configuracion/metodos-pago", Ct)).StatusCode);
    }

    [Fact]
    public async Task Navegacion_CajaApareceParaQuienCobraYNoParaCocina()
    {
        using var camarero = await LoginAsync(DefaultRoles.Waiter);
        using var cocina = await LoginAsync(DefaultRoles.Kitchen);

        Assert.Contains("href=\"caja\"", await camarero.GetStringAsync("/", Ct), StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"caja\"", await cocina.GetStringAsync("/", Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ElEncargadoYElCamarero_TienenLosPermisosDeDineroEsperados()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).ToListAsync(Ct);

        bool Has(string role, Permission permission) => roles.Single(r => r.Name == role).Permissions.Any(p => p.Permission == permission);

        Assert.True(Has("Camarero", Permission.TakePayments));
        Assert.False(Has("Camarero", Permission.OpenCloseCash)); // un camarero cobra pero no abre ni cierra la caja
        Assert.False(Has("Camarero", Permission.PerformRefunds));
        Assert.True(Has("Caja", Permission.OpenCloseCash));
        Assert.True(Has("Encargado", Permission.PerformRefunds));
    }
}
