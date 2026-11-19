using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TPVBar.Application.Features.Authentication;
using TPVBar.Web.Security;

namespace TPVBar.Web.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth");

        // Los formularios llevan token antiforgery (validado automáticamente por [FromForm] + UseAntiforgery).
        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(SecurityServiceCollectionExtensions.LoginRateLimitPolicy);

        group.MapPost("/logout", LogoutAsync);

        return app;
    }

    private static async Task<IResult> LoginAsync(
        [FromForm] string? username,
        [FromForm] string? password,
        [FromForm] string? returnUrl,
        HttpContext http,
        LoginService login,
        CancellationToken cancellationToken)
    {
        var result = await login.LoginAsync(
            new LoginRequest(username ?? string.Empty, password ?? string.Empty, Workstation(http)),
            cancellationToken);

        if (!result.Succeeded)
        {
            return Results.LocalRedirect(result.Failure == LoginFailure.LockedOut ? "/login?error=locked" : "/login?error=invalid");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.UserId!.Value.ToString()),
            new(ClaimTypes.Name, result.UserName!),
            new(TpvClaimTypes.DisplayName, result.DisplayName!),
            new(TpvClaimTypes.IssuedAtUnix, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        };
        claims.AddRange(result.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(result.Permissions.Select(p => new Claim(TpvClaimTypes.Permission, p.ToString())));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = false });

        return Results.LocalRedirect(SafeReturnUrl(returnUrl));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext http,
        LoginService login,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            await login.LogoutAsync(userId, Workstation(http), cancellationToken);
        }

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.LocalRedirect("/login");
    }

    private static string? Workstation(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    /// <summary>Solo se admiten rutas locales: evita redirecciones abiertas hacia sitios externos.</summary>
    internal static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) &&
        returnUrl[0] == '/' &&
        (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
            ? returnUrl
            : "/";
}
