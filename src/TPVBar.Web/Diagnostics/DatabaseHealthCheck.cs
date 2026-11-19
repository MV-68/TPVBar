using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Web.Diagnostics;

/// <summary>Comprobación de disponibilidad: PostgreSQL responde y el esquema está al día. Sin detalles en la respuesta.</summary>
public sealed class DatabaseHealthCheck(TpvBarDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("PostgreSQL no responde.");
            }

            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any();
            return pending
                ? HealthCheckResult.Degraded("Migraciones pendientes.")
                : HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL no disponible.");
        }
    }
}
