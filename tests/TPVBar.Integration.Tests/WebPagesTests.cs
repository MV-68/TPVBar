using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using TPVBar.Application.Features.Orders;
using TPVBar.Domain.Security;

namespace TPVBar.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed partial class WebPagesTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    private HttpClient NewClient() => fixture.Web.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private async Task<HttpClient> LoginAsync(string role)
    {
        var (user, password) = await fixture.CreateUserAsync(role);
        var client = NewClient();
        var page = await client.GetStringAsync("/login", Ct);
        var token = WebUtility.HtmlDecode(TokenRegex().Match(page).Groups[1].Value);
        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
        [
            new("username", user),
            new("password", password),
            new("__RequestVerificationToken", token),
        ]), Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private static string LocationOf(HttpResponseMessage response)
    {
        var location = response.Headers.Location ?? throw new InvalidOperationException("Sin Location.");
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    [Theory]
    [InlineData("/mesas")]
    [InlineData("/productos")]
    [InlineData("/configuracion/zonas-mesas")]
    [InlineData("/configuracion/tipos-impositivos")]
    [InlineData("/comanda/1")]
    public async Task SinSesion_TodasLasPantallasRedirigenAlLogin(string path)
    {
        using var client = NewClient();

        var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", LocationOf(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Camarero_VeLasMesasDeDemostracion()
    {
        using var client = await LoginAsync(DefaultRoles.Waiter);

        var response = await client.GetAsync("/mesas", Ct);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Mesas", html, StringComparison.Ordinal);
        Assert.Contains("Salón", html, StringComparison.Ordinal);
        Assert.Contains("Mesa 1", html, StringComparison.Ordinal);
        Assert.Contains("Libre", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Camarero_NoPuedeEntrarEnProductosNiConfiguracion()
    {
        using var client = await LoginAsync(DefaultRoles.Waiter);

        foreach (var path in new[] { "/productos", "/configuracion/zonas-mesas", "/configuracion/tipos-impositivos" })
        {
            var response = await client.GetAsync(path, Ct);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/acceso-denegado", LocationOf(response), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Cocina_NoPuedeTomarComandas()
    {
        using var client = await LoginAsync(DefaultRoles.Kitchen);

        var response = await client.GetAsync("/mesas", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/acceso-denegado", LocationOf(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encargado_VeElCatalogoConPreciosEnEuros()
    {
        using var client = await LoginAsync(DefaultRoles.Manager);

        var response = await client.GetAsync("/productos", Ct);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Café solo", html, StringComparison.Ordinal);
        Assert.Matches(@"1,30\s€", html);
    }

    [Fact]
    public async Task Encargado_VeLaPantallaDeComandaConSuMesa()
    {
        var world = await SalesHelpers.CreateWorldAsync(fixture.Web.Services);
        var orderId = await SalesHelpers.RunAsync<FloorService, long>(
            fixture.Web.Services, SalesHelpers.Waiter, f => f.OpenTableAsync(world.TableId, 2, Ct));
        using var client = await LoginAsync(DefaultRoles.Manager);

        var response = await client.GetAsync($"/comanda/{orderId}", Ct);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Mesa A", html, StringComparison.Ordinal);
        Assert.Contains("Enviar a cocina", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comandero_TomaComandasPeroNoCobra()
    {
        var world = await SalesHelpers.CreateWorldAsync(fixture.Web.Services);
        var orderId = await SalesHelpers.RunAsync<FloorService, long>(
            fixture.Web.Services, SalesHelpers.Waiter, f => f.OpenTableAsync(world.TableId, 2, Ct));
        using var client = await LoginAsync(DefaultRoles.OrderTaker);

        var comanda = await client.GetAsync($"/comanda/{orderId}", Ct);
        var html = WebUtility.HtmlDecode(await comanda.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, comanda.StatusCode);
        Assert.Contains("Enviar a cocina", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/cobro/", html, StringComparison.Ordinal);

        var menu = await client.GetStringAsync("/", Ct);
        Assert.Contains("href=\"mesas\"", menu, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"caja\"", menu, StringComparison.Ordinal);

        foreach (var path in new[] { $"/cobro/{orderId}", "/caja", "/productos" })
        {
            var response = await client.GetAsync(path, Ct);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/acceso-denegado", LocationOf(response), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Navegacion_SoloMuestraLoQueElRolPuedeUsar()
    {
        using var camarero = await LoginAsync(DefaultRoles.Waiter);
        using var encargado = await LoginAsync(DefaultRoles.Manager);

        var htmlCamarero = await camarero.GetStringAsync("/", Ct);
        var htmlEncargado = await encargado.GetStringAsync("/", Ct);

        Assert.Contains("href=\"mesas\"", htmlCamarero, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"productos\"", htmlCamarero, StringComparison.Ordinal);
        Assert.Contains("href=\"productos\"", htmlEncargado, StringComparison.Ordinal);
        Assert.Contains("href=\"configuracion/zonas-mesas\"", htmlEncargado, StringComparison.Ordinal);
    }
}
