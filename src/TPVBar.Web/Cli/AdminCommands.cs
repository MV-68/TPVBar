using System.Globalization;
using System.Text;
using TPVBar.Application.Features.Authentication;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Security;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Web.Cli;

/// <summary>
/// Comandos de administración que se ejecutan en lugar de arrancar el servidor:
/// <c>--migrate</c>, <c>--create-admin &lt;usuario&gt;</c> (contraseña desde TPVBAR_ADMIN_PASSWORD o por consola) y
/// <c>--create-user &lt;usuario&gt; --role &lt;rol&gt; [--name &lt;nombre visible&gt;]</c> (contraseña desde
/// TPVBAR_USER_PASSWORD o por consola).
/// </summary>
internal static class AdminCommands
{
    private const string CreateUserUsage = "Uso: TPVBar.Web --create-user <usuario> --role <rol> [--name \"<nombre visible>\"]";

    public static bool IsAdminCommand(string[] args) =>
        args.Contains("--migrate", StringComparer.Ordinal)
        || args.Contains("--create-admin", StringComparer.Ordinal)
        || args.Contains("--create-user", StringComparer.Ordinal);

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

            if (args.Contains("--create-user", StringComparer.Ordinal))
            {
                return await CreateUserAsync(app, args);
            }

            var userName = OptionValue(args, "--create-admin");
            if (userName is null)
            {
                Console.Error.WriteLine("Uso: TPVBar.Web --create-admin <usuario>");
                return 2;
            }

            var password = Environment.GetEnvironmentVariable("TPVBAR_ADMIN_PASSWORD")
                ?? ReadPasswordFromConsole("Contraseña del administrador: ", "TPVBAR_ADMIN_PASSWORD");

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

    private static async Task<int> CreateUserAsync(WebApplication app, string[] args)
    {
        var userName = OptionValue(args, "--create-user");
        var roleArg = OptionValue(args, "--role");
        if (userName is null || roleArg is null)
        {
            Console.Error.WriteLine(CreateUserUsage);
            Console.Error.WriteLine("Roles: " + string.Join(", ", DefaultRoles.Names) + ".");
            return 2;
        }

        // Se acepta el rol sin distinguir mayúsculas, pero se guarda con su nombre exacto.
        var role = DefaultRoles.Names.FirstOrDefault(n => string.Equals(n, roleArg, StringComparison.OrdinalIgnoreCase));
        if (role is null)
        {
            Console.Error.WriteLine(string.Create(
                CultureInfo.CurrentCulture,
                $"El rol '{roleArg}' no existe. Roles: {string.Join(", ", DefaultRoles.Names)}."));
            return 2;
        }

        var displayName = OptionValue(args, "--name") ?? userName;
        var password = Environment.GetEnvironmentVariable("TPVBAR_USER_PASSWORD")
            ?? ReadPasswordFromConsole($"Contraseña de '{userName}': ", "TPVBAR_USER_PASSWORD");

        await DatabaseInitializer.SeedReferenceDataAsync(app.Services);
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserAdministrationService>();
        await users.CreateUserAsync(userName, displayName, password, [role], null, CancellationToken.None);

        Console.WriteLine(string.Create(CultureInfo.CurrentCulture, $"Usuario '{userName}' creado con el rol {role}."));
        return 0;
    }

    /// <summary>Valor que sigue a <paramref name="option"/>, o null si falta o es otra opción.</summary>
    private static string? OptionValue(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        var value = args[index + 1];
        return string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) ? null : value;
    }

    private static string ReadPasswordFromConsole(string prompt, string environmentVariable)
    {
        if (Console.IsInputRedirected)
        {
            throw new DomainException($"No hay consola interactiva: defina la variable de entorno {environmentVariable}.");
        }

        Console.Write(prompt);
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
