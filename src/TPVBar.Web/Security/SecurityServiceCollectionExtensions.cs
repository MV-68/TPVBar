using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using TPVBar.Domain.Security;

namespace TPVBar.Web.Security;

public static class SecurityServiceCollectionExtensions
{
    public const string LoginRateLimitPolicy = "login";

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AbsoluteTimeout = TimeSpan.FromHours(12);

    public static IServiceCollection AddTpvBarSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        // Las claves que firman la cookie de sesión y los tokens antifalsificación deben sobrevivir a un reinicio
        // del servicio: sin ellas todas las sesiones se pierden. Con una cuenta de servicio sin perfil de usuario,
        // ASP.NET Core las guardaría solo en memoria; por eso en producción se fija una carpeta (DataProtection:KeysDirectory)
        // y, en Windows, se cifran con DPAPI de la máquina.
        var keysDirectory = configuration["DataProtection:KeysDirectory"];
        if (!string.IsNullOrWhiteSpace(keysDirectory))
        {
            var dataProtection = services.AddDataProtection()
                .SetApplicationName("TPVBar")
                .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
            if (OperatingSystem.IsWindows())
            {
                dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
            }
        }

        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "tpvbar.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                // Con Security:RequireHttps=true (valor de producción) la cookie SOLO viaja por HTTPS: sin certificado
                // no se puede iniciar sesión. En desarrollo se admite HTTP (ver docs/operations/produccion.md).
                options.Cookie.SecurePolicy = configuration.GetValue("Security:RequireHttps", false)
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/acceso-denegado";
                options.ExpireTimeSpan = IdleTimeout;
                options.SlidingExpiration = true;
                options.Events.OnValidatePrincipal = async context =>
                {
                    // Caducidad absoluta: aunque se use sin pausas, la sesión termina a las 12 h.
                    var issued = context.Principal?.FindFirst(TpvClaimTypes.IssuedAtUnix)?.Value;
                    if (!long.TryParse(issued, NumberStyles.None, CultureInfo.InvariantCulture, out var issuedUnix) ||
                        DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(issuedUnix) > AbsoluteTimeout)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync();
                    }
                };
            });

        services.AddCascadingAuthenticationState();

        services.AddAuthorizationBuilder()
            // Todo requiere sesión salvo lo marcado explícitamente con [AllowAnonymous].
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicies()
            .AddPolicy(Policies.CashAccess, policy => policy.RequireAssertion(context =>
                context.User.HasClaim(TpvClaimTypes.Permission, nameof(Permission.TakePayments)) ||
                context.User.HasClaim(TpvClaimTypes.Permission, nameof(Permission.OpenCloseCash))));

        var permitPerMinute = configuration.GetValue("Security:LoginRateLimitPerMinute", 10);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                // El formulario de login es una navegación normal: se vuelve a la pantalla con un aviso.
                context.HttpContext.Response.Redirect("/login?error=rate");
                return ValueTask.CompletedTask;
            };
            options.AddPolicy(LoginRateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });

        return services;
    }

    private static AuthorizationBuilder AddPolicies(this AuthorizationBuilder builder)
    {
        foreach (var permission in Enum.GetValues<Permission>())
        {
            var name = permission.ToString();
            builder.AddPolicy(Policies.For(permission), policy =>
                policy.RequireAuthenticatedUser().RequireClaim(TpvClaimTypes.Permission, name));
        }

        return builder;
    }
}
