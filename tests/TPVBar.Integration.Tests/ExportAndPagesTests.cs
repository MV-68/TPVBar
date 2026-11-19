using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Catalog;
using TPVBar.Application.Features.Inventory;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Security;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed partial class ExportAndPagesTests(PostgresFixture fixture)
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

    private static string LocationOf(HttpResponseMessage response)
    {
        var location = response.Headers.Location ?? throw new InvalidOperationException("Sin Location.");
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    private static string Today => BusinessTime.Today(TimeProvider.System).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string ExportUrl(string report, string? from = null, string? to = null, string? group = null) =>
        $"/export/{report}.csv?desde={from ?? Today}&hasta={to ?? Today}" + (group is null ? string.Empty : $"&agrupar={group}");

    // ---------- Exportación CSV ----------

    [Fact]
    public async Task Exportar_DevuelveUnCsvEspanolConBomYNombreDeArchivo()
    {
        using var client = await LoginAsync(DefaultRoles.Manager);

        using var response = await client.GetAsync(ExportUrl("ventas-por-periodo"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Matches(@"tpvbar-ventas-por-periodo-\d{8}-\d{8}\.csv", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName ?? string.Empty);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("Periodo;Tickets;Ventas (IVA incl.);Ticket medio\r\n", text, StringComparison.Ordinal);
        Assert.Contains("TOTAL;", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exportar_IncluyeLasVentasRealesConComaDecimalYProtegeFormulas()
    {
        await CashHelpers.EnsureOpenAsync(Services);
        var world = await SalesHelpers.CreateWorldAsync(Services);
        await Full<CatalogService, bool>(Services, async c =>
        {
            var p = (await c.ListProductsAsync(Ct)).Single(x => x.Id == world.TortillaId);
            await c.UpdateProductAsync(world.TortillaId, new ProductInput(p.CategoryId, p.TaxRateId, "=1+1 formula", p.SalePrice, p.Station, null, null, 1), Ct);
            return true;
        });
        var orderId = await Full<TPVBar.Application.Features.Orders.FloorService, long>(Services, f => f.OpenTableAsync(world.TableId, 2, Ct));
        await Full<TPVBar.Application.Features.Orders.OrderService, TPVBar.Application.Features.Orders.OrderView>(Services, o => o.AddLineAsync(orderId, world.TortillaId, 2, null, Guid.NewGuid(), Ct));
        await Full<TPVBar.Application.Features.Orders.OrderService, TPVBar.Application.Features.Orders.SendResult>(Services, o => o.SendToKitchenAsync(orderId, Guid.NewGuid(), Ct));
        await CashHelpers.PayAsync(Services, orderId, "CASH", 7.00m);
        using var client = await LoginAsync(DefaultRoles.Manager);

        var products = await client.GetStringAsync(ExportUrl("ventas-por-producto"), Ct);
        var methods = await client.GetStringAsync(ExportUrl("cobros-por-metodo"), Ct);

        Assert.Contains("'=1+1 formula;", products, StringComparison.Ordinal); // la fórmula no se ejecuta en Excel
        Assert.DoesNotContain("\r\n=1+1", products, StringComparison.Ordinal);
        Assert.Matches(@"'=1\+1 formula;[^;]+;2;7,00", products.TrimStart('﻿'));
        Assert.Contains("Efectivo;", methods, StringComparison.Ordinal);
        Assert.Matches(@"Método de pago;Cobros;Devoluciones;Neto", methods);
    }

    [Fact]
    public async Task Exportar_AgrupandoPorSemanaOMes_CambiaLaSerie()
    {
        using var client = await LoginAsync(DefaultRoles.Manager);
        var from = BusinessTime.Today(TimeProvider.System).AddDays(-40).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var day = await client.GetStringAsync(ExportUrl("ventas-por-periodo", from, group: "dia"), Ct);
        var week = await client.GetStringAsync(ExportUrl("ventas-por-periodo", from, group: "semana"), Ct);
        var month = await client.GetStringAsync(ExportUrl("ventas-por-periodo", from, group: "mes"), Ct);

        Assert.True(day.Split("\r\n").Length > week.Split("\r\n").Length);
        Assert.True(week.Split("\r\n").Length > month.Split("\r\n").Length);
        Assert.Contains("Semana del", week, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exportar_QuedaAuditadoConElUsuario()
    {
        using var client = await LoginAsync(DefaultRoles.Manager);

        using var response = await client.GetAsync(ExportUrl("cobros-por-metodo"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var audit = await db.AuditEvents.AsNoTracking().Where(e => e.Action == AuditActions.ReportExported && e.EntityId == "cobros-por-metodo").OrderByDescending(e => e.Id).FirstAsync(Ct);
        Assert.NotNull(audit.UserId);
    }

    [Theory]
    [InlineData("/export/ventas-por-periodo.csv")]
    [InlineData("/export/desconocido.csv?desde=2026-01-01&hasta=2026-01-02")]
    [InlineData("/export/ventas-por-periodo.csv?desde=ayer&hasta=hoy")]
    [InlineData("/export/ventas-por-periodo.csv?desde=2026-02-01&hasta=2026-01-01")]
    [InlineData("/export/ventas-por-periodo.csv?desde=2020-01-01&hasta=2026-01-01")]
    public async Task Exportar_ParametrosInvalidos_Es400SinRevelarDetallesInternos(string url)
    {
        using var client = await LoginAsync(DefaultRoles.Manager);

        using var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exportar_SinSesionRedirigeAlLoginYSinPermisoSeDeniega()
    {
        using var anonymous = NewClient();
        using var waiter = await LoginAsync(DefaultRoles.Waiter);
        using var kitchen = await LoginAsync(DefaultRoles.Kitchen);
        using var cashier = await LoginAsync(DefaultRoles.Cashier);

        var login = await anonymous.GetAsync(ExportUrl("ventas-por-periodo"), Ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.StartsWith("/login", LocationOf(login), StringComparison.Ordinal);

        foreach (var client in new[] { waiter, kitchen, cashier })
        {
            var denied = await client.GetAsync(ExportUrl("ventas-por-periodo"), Ct);
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.StartsWith("/acceso-denegado", LocationOf(denied), StringComparison.Ordinal);
        }
    }

    // ---------- Pantallas ----------

    [Fact]
    public async Task Estadisticas_LaVeElEncargadoConLasDefinicionesYElBotonDeExportar()
    {
        using var client = await LoginAsync(DefaultRoles.Manager);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/estadisticas", Ct));

        Assert.Contains("Estadísticas", html, StringComparison.Ordinal);
        Assert.Contains("Ticket medio", html, StringComparison.Ordinal);
        Assert.Contains("Neto tras devoluciones", html, StringComparison.Ordinal);
        Assert.Contains("Cobros por método de pago", html, StringComparison.Ordinal);
        Assert.Contains("Diferencias de caja", html, StringComparison.Ordinal);
        Assert.Contains("/export/ventas-por-periodo.csv?desde=", html, StringComparison.Ordinal);
        Assert.Contains("XLSX no está disponible", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Almacen_LaVeElEncargadoYMuestraLaPoliticaDeStock()
    {
        using var client = await LoginAsync(DefaultRoles.Manager);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/almacen", Ct));

        Assert.Contains("Almacén", html, StringComparison.Ordinal);
        Assert.Contains("descuenta al enviar", html, StringComparison.Ordinal);
        Assert.Contains("Agua 0,5 L", html, StringComparison.Ordinal); // existencias de demostración
    }

    [Fact]
    public async Task AlmacenYEstadisticas_NoLasVenCamareroNiCocinaNiCaja()
    {
        foreach (var role in new[] { DefaultRoles.Waiter, DefaultRoles.Kitchen, DefaultRoles.Cashier })
        {
            using var client = await LoginAsync(role);
            foreach (var path in new[] { "/almacen", "/estadisticas" })
            {
                var response = await client.GetAsync(path, Ct);
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                Assert.StartsWith("/acceso-denegado", LocationOf(response), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task SinSesion_AlmacenYEstadisticasRedirigenAlLogin()
    {
        using var client = NewClient();

        foreach (var path in new[] { "/almacen", "/estadisticas" })
        {
            var response = await client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/login", LocationOf(response), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Navegacion_AlmacenYEstadisticasSoloParaQuienTienePermiso()
    {
        using var manager = await LoginAsync(DefaultRoles.Manager);
        using var waiter = await LoginAsync(DefaultRoles.Waiter);

        var managerHtml = await manager.GetStringAsync("/", Ct);
        var waiterHtml = await waiter.GetStringAsync("/", Ct);

        Assert.Contains("href=\"almacen\"", managerHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"estadisticas\"", managerHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"almacen\"", waiterHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"estadisticas\"", waiterHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndicadorDeStockBajo_AparecePAraElEncargadoPeroNoParaElCamarero()
    {
        // Un producto controlado por debajo de su mínimo activa el aviso en la barra superior del encargado.
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        await Full<CatalogService, bool>(Services, async c =>
        {
            var p = (await c.ListProductsAsync(Ct)).Single(x => x.Id == world.CanaId);
            await c.UpdateProductAsync(world.CanaId, new ProductInput(p.CategoryId, p.TaxRateId, p.Name, p.SalePrice, p.Station, null, null, 1, true, 5m, "unidad"), Ct);
            return true;
        });
        await Full<StockService, bool>(Services, async s =>
        {
            await s.RegisterEntryAsync(world.CanaId, 1m, "poco stock", Guid.NewGuid(), Ct);
            return true;
        });
        using var manager = await LoginAsync(DefaultRoles.Manager);
        using var waiter = await LoginAsync(DefaultRoles.Waiter);

        var managerHtml = WebUtility.HtmlDecode(await manager.GetStringAsync("/", Ct));
        var waiterHtml = WebUtility.HtmlDecode(await waiter.GetStringAsync("/", Ct));

        Assert.Matches(@"Stock bajo: \d+", managerHtml);
        Assert.DoesNotContain("Stock bajo:", waiterHtml, StringComparison.Ordinal);
    }

    private static Task<T> Full<TService, T>(IServiceProvider sp, Func<TService, Task<T>> op)
        where TService : notnull => CashHelpers.Full(sp, op);
}
