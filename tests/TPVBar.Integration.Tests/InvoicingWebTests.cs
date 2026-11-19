using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Invoicing;
using TPVBar.Domain.Invoicing;
using TPVBar.Domain.Security;

namespace TPVBar.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed partial class InvoicingWebTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => fixture.Web.Services;

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

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

    private static string Today => BusinessTime.Today(TimeProvider.System).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<long> IssuedInvoiceAsync()
    {
        await CashHelpers.Full<InvoiceService, bool>(Services, async s =>
        {
            await s.SaveSettingsAsync("Bar de Pruebas S.L.", "B12345674", "Calle Mayor 1, 28001 Madrid", 400m, Ct);
            return true;
        });
        await CashHelpers.EnsureOpenAsync(Services);
        var orderId = await CashHelpers.SentOrderAsync(Services);
        await CashHelpers.PayAsync(Services, orderId, "CASH", 7.10m, 10m);
        var issued = await CashHelpers.Full<InvoiceService, IssueInvoiceResult>(Services, s =>
            s.IssueAsync(new IssueInvoiceRequest(orderId, InvoiceKind.Complete, new InvoiceRecipient("=CMD|' /C calc'!A0 <script>", "A58818501", "Calle Falsa 1"), Guid.NewGuid()), Ct));
        return issued.InvoiceId;
    }

    [Fact]
    public async Task LasPaginasDeFacturacion_ExigenSesionYPermiso()
    {
        using var anonymous = NewClient();
        foreach (var url in new[] { "/facturas", "/facturas/1", "/configuracion/facturacion", "/export/facturas-libro.csv?desde=2026-01-01&hasta=2026-12-31" })
        {
            using var response = await anonymous.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        // Un camarero no ve facturas (ni puede descargar el libro); un encargado sí ve facturas pero no la configuración.
        using var waiter = await LoginAsync(DefaultRoles.Waiter);
        using (var denied = await waiter.GetAsync("/facturas", Ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Contains("denegado", denied.Headers.Location?.OriginalString ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        using var manager = await LoginAsync(DefaultRoles.Manager);
        using (var ok = await manager.GetAsync("/facturas", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Contains("no se ha probado contra la AEAT", await ok.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using var cashier = await LoginAsync(DefaultRoles.Cashier);
        using var noConfig = await cashier.GetAsync("/configuracion/facturacion", Ct);
        Assert.Equal(HttpStatusCode.Redirect, noConfig.StatusCode);
    }

    [Fact]
    public async Task LaPantallaDeFacturacion_MuestraEmisorProductorYEstadoDelEnvioDesactivado()
    {
        await IssuedInvoiceAsync();
        using var admin = await LoginAsync(DefaultRoles.Administrator);

        using var response = await admin.GetAsync("/configuracion/facturacion", Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Datos del emisor", html, StringComparison.Ordinal);
        Assert.Contains("Sistema informático (declaración responsable)", html, StringComparison.Ordinal);
        Assert.Contains("Envío a la AEAT", html, StringComparison.Ordinal);
        Assert.Contains("desactivado", html, StringComparison.Ordinal);
        Assert.Contains("Registros generados", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaFactura_MuestraQrHuellaYAvisoDeNoEnvioYCodificaLosDatosDelCliente()
    {
        var id = await IssuedInvoiceAsync();
        using var admin = await LoginAsync(DefaultRoles.Administrator);

        using var response = await admin.GetAsync($"/facturas/{id}", Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<svg", html, StringComparison.Ordinal);
        Assert.Contains("NO ha sido enviada a la AEAT", html, StringComparison.Ordinal);
        Assert.Contains("B12345674", html, StringComparison.Ordinal);
        Assert.Contains("A58818501", html, StringComparison.Ordinal);

        // El nombre del cliente (con intento de inyección) se muestra codificado, nunca como HTML ejecutable.
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportarLibroEIva_DevuelvenCsvEspanolYProtegenFormulas()
    {
        await IssuedInvoiceAsync();
        using var manager = await LoginAsync(DefaultRoles.Manager);

        using var book = await manager.GetAsync($"/export/facturas-libro.csv?desde={Today}&hasta={Today}", Ct);
        var bytes = await book.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, book.StatusCode);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("Número;Fecha;Tipo;NIF cliente;Cliente;Tipo IVA %;Base imponible;Cuota IVA;Total;Rectifica a;Estado\r\n", text, StringComparison.Ordinal);
        Assert.Contains("'=CMD", text, StringComparison.Ordinal);
        Assert.Matches(@"tpvbar-libro-facturas-\d{8}-\d{8}\.csv", book.Content.Headers.ContentDisposition?.FileNameStar ?? book.Content.Headers.ContentDisposition?.FileName ?? string.Empty);

        using var vat = await manager.GetAsync($"/export/facturas-iva.csv?desde={Today}&hasta={Today}", Ct);
        var vatText = Encoding.UTF8.GetString((await vat.Content.ReadAsByteArrayAsync(Ct))[3..]);
        Assert.StartsWith("Tipo IVA %;Base imponible;Cuota IVA;Total\r\n", vatText, StringComparison.Ordinal);
        Assert.Contains("TOTAL;", vatText, StringComparison.Ordinal);

        using var bad = await manager.GetAsync("/export/facturas-libro.csv?desde=nope&hasta=2026-01-01", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var waiter = await LoginAsync(DefaultRoles.Waiter);
        using var denied = await waiter.GetAsync($"/export/facturas-iva.csv?desde={Today}&hasta={Today}", Ct);
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
    }
}
