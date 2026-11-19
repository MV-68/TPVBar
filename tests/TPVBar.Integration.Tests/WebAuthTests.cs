using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Security;
using TPVBar.Infrastructure.Persistence;
using TPVBar.Web.Security;

namespace TPVBar.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed partial class WebAuthTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    private static string LocationOf(HttpResponseMessage response)
    {
        var location = response.Headers.Location ?? throw new InvalidOperationException("La respuesta no tiene Location.");
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    private HttpClient NewClient() => fixture.Web.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<string> GetTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path, Ct);
        var match = TokenRegex().Match(html);
        Assert.True(match.Success, $"La página {path} no contiene token antiforgery.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string user, string password, string? returnUrl = null)
    {
        var token = await GetTokenAsync(client, "/login");
        return await client.PostAsync("/auth/login", new FormUrlEncodedContent(
        [
            new("username", user),
            new("password", password),
            new("returnUrl", returnUrl ?? string.Empty),
            new("__RequestVerificationToken", token),
        ]), Ct);
    }

    [Fact]
    public async Task SinSesion_LasPaginasRedirigenAlLogin()
    {
        using var client = NewClient();

        var respuesta = await client.GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        Assert.StartsWith("/login", LocationOf(respuesta), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_MuestraFormularioEnEspanolConTokenAntiforgery()
    {
        using var client = NewClient();

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/login", Ct));

        Assert.Contains("Iniciar sesión", html, StringComparison.Ordinal);
        Assert.Contains("name=\"username\"", html, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", html, StringComparison.Ordinal);
        Assert.Matches(TokenRegex(), html);
    }

    [Fact]
    public async Task Login_SinTokenAntiforgery_SeRechaza()
    {
        using var client = NewClient();
        var (user, password) = await fixture.CreateUserAsync(DefaultRoles.Waiter);

        var respuesta = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
        [
            new("username", user),
            new("password", password),
        ]), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.DoesNotContain(respuesta.Headers, h => h.Key == "Set-Cookie" && h.Value.Any(v => v.StartsWith("tpvbar.auth", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Login_Correcto_AbreSesionConCookieSeguraYMuestraElUsuario()
    {
        using var client = NewClient();
        var (user, password) = await fixture.CreateUserAsync(DefaultRoles.Manager);

        var login = await PostLoginAsync(client, user, password);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.ToString());
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("tpvbar.auth", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase); // cookie de sesión: no persiste

        var home = await client.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var html = WebUtility.HtmlDecode(await home.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Usuario " + user, html, StringComparison.Ordinal);
        Assert.Contains("Encargado", html, StringComparison.Ordinal);
        Assert.DoesNotContain(password, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_Incorrecto_MismaRespuestaParaUsuarioInexistenteYContrasenaMala()
    {
        using var client = NewClient();
        var (user, _) = await fixture.CreateUserAsync(DefaultRoles.Waiter);

        var malaPassword = await PostLoginAsync(client, user, "contrasena-mala-123");
        var inexistente = await PostLoginAsync(client, "nadie-" + Guid.NewGuid().ToString("N")[..8], "contrasena-mala-123");

        Assert.Equal(HttpStatusCode.Redirect, malaPassword.StatusCode);
        Assert.Equal("/login?error=invalid", malaPassword.Headers.Location?.ToString());
        Assert.Equal(malaPassword.Headers.Location, inexistente.Headers.Location);
        Assert.DoesNotContain(malaPassword.Headers, h => h.Key == "Set-Cookie" && h.Value.Any(v => v.StartsWith("tpvbar.auth", StringComparison.Ordinal)));

        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/login?error=invalid", Ct));
        Assert.Contains("Usuario o contraseña incorrectos.", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_TrasVariosFallos_BloqueaYAvisa()
    {
        using var client = NewClient();
        var (user, password) = await fixture.CreateUserAsync(DefaultRoles.Waiter);

        HttpResponseMessage last = null!;
        for (var i = 0; i < LockoutPolicy.Default.MaxFailedAttempts; i++)
        {
            last = await PostLoginAsync(client, user, "mala-" + i);
        }

        Assert.Equal("/login?error=locked", last.Headers.Location?.ToString());

        var conClaveBuena = await PostLoginAsync(client, user, password);
        Assert.Equal("/login?error=locked", conClaveBuena.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("https://evil.example/robo", "/")]
    [InlineData("//evil.example/robo", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/mesas", "/mesas")]
    public async Task Login_ReturnUrl_SoloAceptaRutasLocales(string returnUrl, string esperado)
    {
        using var client = NewClient();
        var (user, password) = await fixture.CreateUserAsync(DefaultRoles.Waiter);

        var login = await PostLoginAsync(client, user, password, returnUrl);

        Assert.Equal(esperado, login.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Logout_CierraLaSesionYQuedaAuditado()
    {
        using var client = NewClient();
        var (user, password) = await fixture.CreateUserAsync(DefaultRoles.Waiter);
        await PostLoginAsync(client, user, password);

        var token = await GetTokenAsync(client, "/");
        var logout = await client.PostAsync("/auth/logout", new FormUrlEncodedContent([new("__RequestVerificationToken", token)]), Ct);

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/login", logout.Headers.Location?.ToString());

        var trasLogout = await client.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.Redirect, trasLogout.StatusCode);
        Assert.StartsWith("/login", LocationOf(trasLogout), StringComparison.Ordinal);

        await using var scope = fixture.Web.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var userId = (await db.Users.AsNoTracking().SingleAsync(u => u.NormalizedUserName == TPVBar.Domain.Entities.AppUser.NormalizeUserName(user), Ct)).Id;
        Assert.True(await db.AuditEvents.AnyAsync(e => e.UserId == userId && e.Action == AuditActions.Logout, Ct));
    }

    [Fact]
    public async Task Logout_SinSesion_NoEsAccesible()
    {
        using var client = NewClient();
        var token = await GetTokenAsync(client, "/login");

        var respuesta = await client.PostAsync("/auth/logout", new FormUrlEncodedContent([new("__RequestVerificationToken", token)]), Ct);

        Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        Assert.StartsWith("/login", LocationOf(respuesta), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorrelationId_SeDevuelveYSoloSeAceptaConFormatoSeguro()
    {
        using var client = NewClient();

        var generado = await client.GetAsync("/health", Ct);
        Assert.True(generado.Headers.TryGetValues("X-Correlation-Id", out var valoresGenerados));
        Assert.Matches("^[0-9a-f]{32}$", valoresGenerados.Single());

        using var conCabecera = new HttpRequestMessage(HttpMethod.Get, "/health");
        conCabecera.Headers.Add("X-Correlation-Id", "mi-traza-1234");
        var recibido = await client.SendAsync(conCabecera, Ct);
        Assert.Equal("mi-traza-1234", recibido.Headers.GetValues("X-Correlation-Id").Single());

        using var maliciosa = new HttpRequestMessage(HttpMethod.Get, "/health");
        maliciosa.Headers.Add("X-Correlation-Id", "x y<script>");
        var sustituido = await client.SendAsync(maliciosa, Ct);
        Assert.DoesNotContain("script", sustituido.Headers.GetValues("X-Correlation-Id").Single(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DefaultRoles.Administrator, Permission.ManageUsers, true)]
    [InlineData(DefaultRoles.Manager, Permission.ViewReports, true)]
    [InlineData(DefaultRoles.Manager, Permission.ManageUsers, false)]
    [InlineData(DefaultRoles.Waiter, Permission.ApplyDiscounts, false)]
    [InlineData(DefaultRoles.Cashier, Permission.OpenCloseCash, true)]
    public async Task Permisos_LasPoliticasSeAplicanSegunElRolDeLaSesion(string role, Permission permission, bool esperado)
    {
        var (user, password) = await fixture.CreateUserAsync(role);
        await using var scope = fixture.Web.Services.CreateAsyncScope();
        var login = await scope.ServiceProvider.GetRequiredService<TPVBar.Application.Features.Authentication.LoginService>()
            .LoginAsync(new(user, password, "test"), Ct);

        var claims = login.Permissions.Select(p => new Claim(TpvClaimTypes.Permission, p.ToString()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var resultado = await authorization.AuthorizeAsync(principal, null, Policies.For(permission));

        Assert.Equal(esperado, resultado.Succeeded);
    }

    [Fact]
    public async Task UsuariosDePrueba_SeCreanEnDesarrolloUnoPorRol()
    {
        await using var scope = fixture.Web.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();

        var nombres = await db.Users.AsNoTracking().Where(u => u.NormalizedUserName.StartsWith("DEV.")).Select(u => u.NormalizedUserName).ToListAsync(Ct);

        Assert.Equal(["DEV.ADMIN", "DEV.CAJA", "DEV.CAMARERO", "DEV.COCINA", "DEV.ENCARGADO"], nombres.Order());
    }
}
