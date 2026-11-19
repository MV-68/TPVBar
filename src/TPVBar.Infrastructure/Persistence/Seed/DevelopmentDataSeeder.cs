using Microsoft.Extensions.Logging;
using TPVBar.Application.Features.Authentication;
using TPVBar.Domain.Security;

namespace TPVBar.Infrastructure.Persistence.Seed;

/// <summary>
/// Usuarios de prueba, uno por rol. SOLO para desarrollo: la contraseña viene de configuración
/// (User Secrets, clave <c>Seed:DevUserPassword</c>) y no existe ninguna contraseña por defecto.
/// </summary>
internal static partial class DevelopmentDataSeeder
{
    private static readonly (string UserName, string DisplayName, string Role)[] TestUsers =
    [
        ("dev.admin", "Admin (prueba)", DefaultRoles.Administrator),
        ("dev.encargado", "Encargado (prueba)", DefaultRoles.Manager),
        ("dev.camarero", "Camarero (prueba)", DefaultRoles.Waiter),
        ("dev.cocina", "Cocina (prueba)", DefaultRoles.Kitchen),
        ("dev.caja", "Caja (prueba)", DefaultRoles.Cashier),
    ];

    public static async Task SeedAsync(
        UserAdministrationService users,
        Application.Abstractions.IUserStore store,
        string? devPassword,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(devPassword))
        {
            LogMissingPassword(logger);
            return;
        }

        foreach (var (userName, displayName, role) in TestUsers)
        {
            if (await store.UserNameExistsAsync(userName, cancellationToken))
            {
                continue;
            }

            await users.CreateUserAsync(userName, displayName, devPassword, [role], null, cancellationToken);
        }
    }

    [LoggerMessage(
        EventId = 200,
        Level = LogLevel.Warning,
        Message = "No se crean usuarios de prueba: falta la clave de configuración Seed:DevUserPassword (ejecute scripts/dev-db.ps1).")]
    private static partial void LogMissingPassword(ILogger logger);
}
