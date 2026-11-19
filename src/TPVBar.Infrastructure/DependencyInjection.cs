using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TPVBar.Application.Abstractions;
using TPVBar.Application.Features.Invoicing;
using TPVBar.Infrastructure.Billing;
using TPVBar.Infrastructure.Persistence;
using TPVBar.Infrastructure.Security;

namespace TPVBar.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "TpvBar";

    public static IServiceCollection AddTpvBarInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // La cadena de conexión se lee de forma diferida (User Secrets / variables de entorno), nunca del repositorio.
        services.AddDbContext<TpvBarDbContext>((sp, options) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Falta la cadena de conexión '{ConnectionStringName}'. Configure User Secrets o la variable de entorno ConnectionStrings__{ConnectionStringName}.");

            options
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", TpvBarDbContext.Schema))
                .UseSnakeCaseNamingConvention();
        });

        services.AddScoped<EfUnitOfWork>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<EfUnitOfWork>());
        services.AddScoped<IAuditLog>(sp => sp.GetRequiredService<EfUnitOfWork>());
        services.AddScoped<IUserStore, EfUserStore>();
        services.AddScoped<IAppData, EfAppData>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();

        // Envío a la AEAT: desactivado salvo que Billing:Aeat:Mode lo active en la configuración del servidor.
        services.AddSingleton(sp =>
        {
            var options = new AeatOptions();
            sp.GetRequiredService<IConfiguration>().GetSection(AeatOptions.SectionName).Bind(options);
            return options;
        });
        services.AddSingleton<IAeatGateway>(sp => new AeatGateway(sp.GetRequiredService<AeatOptions>()));

        return services;
    }
}
