using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Printing;
using TPVBar.Domain.Printing;
using TPVBar.Domain.Security;
using TPVBar.PrintContracts;
using TPVBar.Web.Realtime;
using DeliveryLevel = TPVBar.PrintContracts.DeliveryLevel;

namespace TPVBar.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed partial class AgentApiAndRealtimeTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    private HttpClient ApiClient(string? key)
    {
        var client = fixture.Web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add(AgentApiRoutes.KeyHeader, key);
        }

        return client;
    }

    // ---------- API del agente ----------

    [Theory]
    [InlineData(null)]
    [InlineData("clave-incorrecta")]
    [InlineData("")]
    public async Task Api_SinClaveOConClaveIncorrecta_Es401(string? key)
    {
        using var client = ApiClient(key);

        var printers = await client.GetAsync(AgentApiRoutes.Printers, Ct);
        var claim = await client.PostAsJsonAsync(AgentApiRoutes.Claim, new ClaimRequest("intruso", null, 5), PrintJson.Options, Ct);
        var result = await client.PostAsJsonAsync(AgentApiRoutes.ResultFor(1), new ResultRequest("intruso", true, DeliveryLevel.Simulated, null), PrintJson.Options, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, printers.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, claim.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task Api_SinClaveConfiguradaEnElServidor_QuedaDesactivada503()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:TpvBar", fixture.ConnectionString);
            builder.UseSetting("PrintAgent:ApiKey", string.Empty);
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentApiRoutes.KeyHeader, "cualquiera");

        var response = await client.GetAsync(AgentApiRoutes.Printers, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); // no existe clave por defecto
    }

    [Fact]
    public async Task Api_ConClave_ListaImpresorasSinSecretosYAceptaElContrato()
    {
        using var client = ApiClient(fixture.AgentKey);

        var response = await client.GetAsync(AgentApiRoutes.Printers, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var printers = PrintJson.Deserialize<PrintersResponse>(body)!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(printers.Printers, p => p.Name == "Cocina (simulada)" && p.Connection == "Simulated" && p.PaperWidthMm == 80);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Api_ReclamarYNotificarResultado_FuncionaYSeRepiteSinEfecto()
    {
        var services = fixture.Web.Services;
        var printer = await SalesHelpers.RunAsync<PrintAdminService, int>(services, SalesHelpers.Full,
            a => a.CreatePrinterAsync(new PrinterInput("Api " + Guid.NewGuid().ToString("N")[..8], PrinterType.Kitchen, PrinterConnection.Simulated, Path.GetTempPath(), 80), Ct));
        var jobId = await SalesHelpers.RunAsync<PrintAdminService, long>(services, SalesHelpers.Full, a => a.TestPrintAsync(printer, Ct));
        using var client = ApiClient(fixture.AgentKey);

        ClaimedJob? claimed = null;
        for (var i = 0; i < 10 && claimed is null; i++)
        {
            var response = await client.PostAsJsonAsync(AgentApiRoutes.Claim, new ClaimRequest("agente-api", "1.0", 20), PrintJson.Options, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            claimed = PrintJson.Deserialize<ClaimResponse>(await response.Content.ReadAsStringAsync(Ct))!.Jobs.FirstOrDefault(j => j.Id == jobId);
        }

        Assert.NotNull(claimed);
        Assert.Equal(PrintDocumentKind.Test, claimed.Document.Kind);
        Assert.Equal(printer, claimed.PrinterId);

        var wrongAgent = await client.PostAsJsonAsync(AgentApiRoutes.ResultFor(jobId), new ResultRequest("otro", true, DeliveryLevel.SentToDevice, null), PrintJson.Options, Ct);
        Assert.Equal(HttpStatusCode.Conflict, wrongAgent.StatusCode);

        var ok = await client.PostAsJsonAsync(AgentApiRoutes.ResultFor(jobId), new ResultRequest("agente-api", true, DeliveryLevel.SentToDevice, null), PrintJson.Options, Ct);
        var again = await client.PostAsJsonAsync(AgentApiRoutes.ResultFor(jobId), new ResultRequest("agente-api", true, DeliveryLevel.SentToDevice, null), PrintJson.Options, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("Recorded", await ok.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Contains("AlreadyRecorded", await again.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var unknown = await client.PostAsJsonAsync(AgentApiRoutes.ResultFor(long.MaxValue), new ResultRequest("agente-api", true, DeliveryLevel.SentToDevice, null), PrintJson.Options, Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Api_NoRequiereCookieNiAntiforgery_PeroLasPaginasSiLaRequieren()
    {
        using var client = ApiClient(fixture.AgentKey);

        var api = await client.GetAsync(AgentApiRoutes.Printers, Ct);
        var page = await client.GetAsync("/mesas", Ct);

        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode); // la clave del agente no abre las pantallas
    }

    // ---------- Pantallas ----------

    private async Task<string> LoginCookieAsync(string role)
    {
        var (user, password) = await fixture.CreateUserAsync(role);
        using var client = ApiClient(null);

        using var loginPage = await client.GetAsync("/login", Ct);
        var antiforgeryCookie = loginPage.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).Single(c => c.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal));
        var token = WebUtility.HtmlDecode(TokenRegex().Match(await loginPage.Content.ReadAsStringAsync(Ct)).Groups[1].Value);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = new FormUrlEncodedContent([new("username", user), new("password", password), new("__RequestVerificationToken", token)]),
        };
        request.Headers.Add("Cookie", antiforgeryCookie);
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).Single(c => c.StartsWith("tpvbar.auth", StringComparison.Ordinal));
    }

    private async Task<string> GetPageAsync(string role, string path)
    {
        using var client = ApiClient(null);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", await LoginCookieAsync(role));
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<HttpStatusCode> StatusOfAsync(string role, string path)
    {
        using var client = ApiClient(null);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", await LoginCookieAsync(role));
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task Cocina_LaPantallaLaVenCocinaYEncargadoPeroNoElCamarero()
    {
        var html = await GetPageAsync(DefaultRoles.Kitchen, "/cocina");
        Assert.Contains("Pendiente", html, StringComparison.Ordinal);
        Assert.Contains("Preparando", html, StringComparison.Ordinal);
        Assert.Contains("Preparado", html, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, await StatusOfAsync(DefaultRoles.Manager, "/cocina"));
        Assert.Equal(HttpStatusCode.Redirect, await StatusOfAsync(DefaultRoles.Waiter, "/cocina"));
    }

    [Fact]
    public async Task Impresoras_LaPantallaIndicaQueLasSimuladasNoSonReales()
    {
        var html = await GetPageAsync(DefaultRoles.Manager, "/configuracion/impresoras");

        Assert.Contains("Impresoras", html, StringComparison.Ordinal);
        Assert.Contains("SIMULADA", html, StringComparison.Ordinal);
        Assert.Contains("no es un dispositivo real", html, StringComparison.Ordinal);
        Assert.Contains("Agente de impresión", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, await StatusOfAsync(DefaultRoles.Waiter, "/configuracion/impresoras"));
        Assert.Equal(HttpStatusCode.Redirect, await StatusOfAsync(DefaultRoles.Kitchen, "/configuracion/impresoras"));
    }

    [Fact]
    public async Task Navegacion_CocinaSoloVeCocinaYElEncargadoVeImpresoras()
    {
        var cocina = await GetPageAsync(DefaultRoles.Kitchen, "/");
        var encargado = await GetPageAsync(DefaultRoles.Manager, "/");

        Assert.Contains("href=\"cocina\"", cocina, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"mesas\"", cocina, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"configuracion/impresoras\"", cocina, StringComparison.Ordinal);
        Assert.Contains("href=\"configuracion/impresoras\"", encargado, StringComparison.Ordinal);
    }

    // ---------- SignalR ----------

    private async Task<HubConnection> ConnectAsync(string? role)
    {
        var cookie = role is null ? null : await LoginCookieAsync(role);
        var server = fixture.Web.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, OperationsHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (cookie is not null)
                {
                    options.Headers.Add("Cookie", cookie);
                }
            })
            .Build();
        await connection.StartAsync(Ct);
        return connection;
    }

    private static Task<JsonElement?> Listen(HubConnection connection, string eventName)
    {
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(eventName, payload => tcs.TrySetResult(payload));
        return tcs.Task;
    }

    private static async Task<bool> ReceivedAsync(Task<JsonElement?> pending, TimeSpan wait) =>
        await Task.WhenAny(pending, Task.Delay(wait)) == pending;

    [Fact]
    public async Task Hub_SinSesion_NoSePuedeConectar() =>
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => ConnectAsync(null));

    [Fact]
    public async Task Hub_CadaConexionRecibeSoloLosAvisosDeSusGrupos()
    {
        await using var kitchen = await ConnectAsync(DefaultRoles.Kitchen);
        await using var waiter = await ConnectAsync(DefaultRoles.Waiter);
        await using var manager = await ConnectAsync(DefaultRoles.Manager);
        var publisher = fixture.Web.Services.GetRequiredService<OperationEventPublisher>();

        var kitchenGets = Listen(kitchen, OperationEventNames.KitchenTicketCreated);
        var waiterGets = Listen(waiter, OperationEventNames.KitchenTicketCreated);
        await publisher.PublishAsync([new OperationEvent(OperationEventNames.KitchenTicketCreated, OrderId: 7, TicketId: 9, Station: TPVBar.Domain.Catalog.PreparationStation.Bar)]);

        Assert.True(await ReceivedAsync(kitchenGets, TimeSpan.FromSeconds(5)));
        Assert.True(await ReceivedAsync(waiterGets, TimeSpan.FromSeconds(5)));

        // Los avisos de impresión van a quien gestiona impresión (y a sala), no a la cocina.
        var printKitchen = Listen(kitchen, OperationEventNames.PrinterStatusChanged);
        var printManager = Listen(manager, OperationEventNames.PrinterStatusChanged);
        await publisher.PublishAsync([new OperationEvent(OperationEventNames.PrinterStatusChanged)]);

        Assert.True(await ReceivedAsync(printManager, TimeSpan.FromSeconds(5)));
        Assert.False(await ReceivedAsync(printKitchen, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Hub_LosAvisosSoloLlevanIdentificadoresNuncaDatos()
    {
        await using var kitchen = await ConnectAsync(DefaultRoles.Kitchen);
        var received = Listen(kitchen, OperationEventNames.KitchenTicketStatusChanged);

        await fixture.Web.Services.GetRequiredService<OperationEventPublisher>()
            .PublishAsync([new OperationEvent(OperationEventNames.KitchenTicketStatusChanged, OrderId: 1, TicketId: 2)]);

        Assert.True(await ReceivedAsync(received, TimeSpan.FromSeconds(5)));
        var keys = (await received)!.Value.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Subset(new HashSet<string> { "name", "orderId", "tableId", "ticketId", "station", "productId" }, keys);
    }

    [Fact]
    public async Task Publicador_UnAvisoRepetidoEnUnaOperacionSeEnviaUnaSolaVez()
    {
        await using var kitchen = await ConnectAsync(DefaultRoles.Kitchen);
        var count = 0;
        kitchen.On<JsonElement>(OperationEventNames.OrderUpdated, _ => Interlocked.Increment(ref count));
        var same = new OperationEvent(OperationEventNames.OrderUpdated, OrderId: 5);

        await fixture.Web.Services.GetRequiredService<OperationEventPublisher>().PublishAsync([same, same, same]);
        await Task.Delay(500, Ct);

        Assert.Equal(1, count);
    }
}
