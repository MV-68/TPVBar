using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using TPVBar.Domain.Printing;
using TPVBar.Infrastructure.Persistence;
using TPVBar.Web.Realtime;

namespace TPVBar.Web.Diagnostics;

public sealed record DatabaseDiagnostics(
    bool Reachable,
    long? LatencyMs,
    string? ServerVersion,
    string? LastMigration,
    int PendingMigrations,
    string? Error);

public sealed record PrintDiagnostics(
    DateTimeOffset? AgentLastSeenAt,
    string? AgentVersion,
    bool AgentOnline,
    int PendingJobs,
    int FailedJobs,
    DateTimeOffset? LastFailureAt,
    string? LastFailureError);

public sealed record DiagnosticsSnapshot(
    DateTimeOffset GeneratedAt,
    string AppVersion,
    string Environment,
    string Runtime,
    DatabaseDiagnostics Database,
    int RealtimeConnections,
    int OpenScreens,
    PrintDiagnostics? Print,
    BackupStatusResult Backup,
    bool BackupIsStale,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Lee el estado operativo para la pantalla de diagnóstico del administrador. Solo expone estados y
/// recuentos: nunca cadenas de conexión, claves ni datos de clientes.
/// </summary>
public sealed class DiagnosticsService(
    TpvBarDbContext db,
    ConnectionTracker connections,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    TimeProvider time)
{
    /// <summary>Una copia con más de este tiempo se considera obsoleta (RPO objetivo: 24 h + margen).</summary>
    public static readonly TimeSpan BackupMaxAge = TimeSpan.FromHours(30);

    public static string AppVersion { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "desconocida")
        .Split('+')[0];

    public async Task<DiagnosticsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var warnings = new List<string>();

        var database = await ReadDatabaseAsync(cancellationToken);
        if (!database.Reachable)
        {
            warnings.Add("No se puede conectar con PostgreSQL.");
        }
        else if (database.PendingMigrations > 0)
        {
            warnings.Add("Hay migraciones pendientes: el esquema no corresponde a esta versión de la aplicación.");
        }

        PrintDiagnostics? print = null;
        if (database.Reachable)
        {
            print = await ReadPrintAsync(now, cancellationToken);
            if (!print.AgentOnline)
            {
                warnings.Add("El agente de impresión no se ha comunicado recientemente.");
            }

            if (print.FailedJobs > 0)
            {
                warnings.Add("Hay trabajos de impresión fallidos pendientes de revisar.");
            }
        }

        var backup = await BackupStatusReader.ReadAsync(configuration["Backup:StatusFile"], cancellationToken);
        var stale = false;
        switch (backup.Kind)
        {
            case BackupStatusKind.NotConfigured:
                warnings.Add("No hay una ubicación de estado de copias configurada (Backup:StatusFile).");
                break;
            case BackupStatusKind.NeverRun:
                warnings.Add("Todavía no consta ninguna copia de seguridad.");
                break;
            case BackupStatusKind.Unreadable:
                warnings.Add("No se puede leer el estado de la última copia de seguridad.");
                break;
            default:
                var status = backup.Status!;
                stale = now - status.FinishedAt > BackupMaxAge;
                if (!status.Success)
                {
                    warnings.Add("La última copia de seguridad falló.");
                }
                else if (stale)
                {
                    warnings.Add("La última copia correcta tiene más de 30 horas.");
                }
                else if (!status.CopiedToSecondaryLocation)
                {
                    warnings.Add("La última copia no se duplicó en otra ubicación: un fallo del disco del servidor la perdería.");
                }

                break;
        }

        return new DiagnosticsSnapshot(
            now,
            AppVersion,
            environment.EnvironmentName,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            database,
            connections.Count,
            connections.Circuits,
            print,
            backup,
            stale,
            warnings);
    }

    private async Task<DatabaseDiagnostics> ReadDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            var reachable = await db.Database.CanConnectAsync(cancellationToken);
            watch.Stop();
            if (!reachable)
            {
                return new DatabaseDiagnostics(false, null, null, null, 0, "Sin respuesta.");
            }

            var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
            var version = await db.Database.SqlQueryRaw<string>("SELECT current_setting('server_version') AS \"Value\"")
                .FirstOrDefaultAsync(cancellationToken);
            return new DatabaseDiagnostics(true, watch.ElapsedMilliseconds, version, applied.LastOrDefault(), pending, null);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            // Solo el tipo de fallo: el mensaje puede incluir el servidor o el usuario de la conexión.
            return new DatabaseDiagnostics(false, null, null, null, 0, ex.GetType().Name);
        }
    }

    private async Task<PrintDiagnostics> ReadPrintAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var beats = await db.PrintAgentHeartbeats.AsNoTracking().ToListAsync(cancellationToken);
        var latest = beats.OrderByDescending(b => b.LastSeenAt).FirstOrDefault();
        var pending = await db.PrintJobs.CountAsync(j => j.Status == PrintJobStatus.Pending || j.Status == PrintJobStatus.Processing, cancellationToken);
        var failed = await db.PrintJobs.CountAsync(j => j.Status == PrintJobStatus.Failed, cancellationToken);
        var lastFailed = await db.PrintJobs.AsNoTracking()
            .Where(j => j.LastError != null)
            .OrderByDescending(j => j.ProcessedAt ?? j.CreatedAt)
            .Select(j => new { When = j.ProcessedAt ?? j.CreatedAt, j.LastError })
            .FirstOrDefaultAsync(cancellationToken);

        return new PrintDiagnostics(
            latest?.LastSeenAt,
            latest?.Version,
            latest is not null && now - latest.LastSeenAt <= TPVBar.Application.Features.Printing.PrintAdminService.AgentOnlineWindow,
            pending,
            failed,
            lastFailed?.When,
            lastFailed?.LastError);
    }

    public static string FormatAge(DateTimeOffset moment, DateTimeOffset now)
    {
        var age = now - moment;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "hace unos segundos";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return string.Create(CultureInfo.CurrentCulture, $"hace {(int)age.TotalMinutes} min");
        }

        return age < TimeSpan.FromDays(2)
            ? string.Create(CultureInfo.CurrentCulture, $"hace {(int)age.TotalHours} h")
            : string.Create(CultureInfo.CurrentCulture, $"hace {(int)age.TotalDays} días");
    }
}
