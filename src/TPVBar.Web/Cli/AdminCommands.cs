using System.Globalization;
using System.Text;
using TPVBar.Application.Features.Authentication;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Security;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Web.Cli;

/// <summary>
/// Comandos de administración que se ejecutan en lugar de arrancar el servidor:
/// <c>--migrate</c> y <c>--create-admin &lt;usuario&gt;</c> (contraseña desde TPVBAR_ADMIN_PASSWORD o por consola).
/// </summary>
internal static class AdminCommands
{
    public static bool IsAdminCommand(string[] args) =>
        args.Contains("--migrate", StringComparer.Ordinal) || args.Contains("--create-admin", StringComparer.Ordinal);

    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        try
        {
            if (args.Contains("--migrate", StringComparer.Ordinal))
            {
                await DatabaseInitializer.MigrateAsync(app.Services);
                await DatabaseInitializer.SeedReferenceDataAsync(app.Services);
                Console.WriteLine("Migraciones aplicadas y roles iniciales verificados.");
                return 0;
            }

            var index = Array.IndexOf(args, "--create-admin");
            var userName = index + 1 < args.Length ? args[index + 1] : null;
            if (string.IsNullOrWhiteSpace(userName) || userName.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Uso: TPVBar.Web --create-admin <usuario>");
                return 2;
            }

            var password = Environment.GetEnvironmentVariable("TPVBAR_ADMIN_PASSWORD") ?? ReadPasswordFromConsole();

            await DatabaseInitializer.SeedReferenceDataAsync(app.Services);
            await using var scope = app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserAdministrationService>();
            await users.CreateUserAsync(userName, userName, password, [DefaultRoles.Administrator], null, CancellationToken.None);

            Console.WriteLine(string.Create(CultureInfo.CurrentCulture, $"Administrador '{userName}' creado."));
            return 0;
        }
        catch (DomainException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string ReadPasswordFromConsole()
    {
        if (Console.IsInputRedirected)
        {
            throw new DomainException("No hay consola interactiva: defina la variable de entorno TPVBAR_ADMIN_PASSWORD.");
        }

        Console.Write("Contraseña del administrador: ");
        var buffer = new StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }

        Console.WriteLine();
        return buffer.ToString();
    }
}
