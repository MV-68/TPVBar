using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Printing;
using TPVBar.PrintContracts;
using DomainDelivery = TPVBar.Domain.Printing.DeliveryLevel;

namespace TPVBar.Application.Features.Printing;

public enum ResultOutcome
{
    Recorded = 1,

    /// <summary>El resultado ya constaba (repetición del agente): no se hace nada.</summary>
    AlreadyRecorded = 2,
}

/// <summary>
/// Lado servidor de la cola de impresión que consume el agente (autenticado por clave, sin usuario).
/// Reparte los trabajos con arrendamiento temporal y registra cada intento.
/// </summary>
public sealed class PrintQueueService(
    IAppData data,
    IUnitOfWork unitOfWork,
    IAuditLog auditLog,
    IOperationEvents events,
    TimeProvider time)
{
    /// <summary>Tiempo máximo que un agente puede tener un trabajo antes de que otro pueda reclamarlo.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(string agent, string? version, int max, CancellationToken cancellationToken)
    {
        RequireAgent(agent);
        var now = time.GetUtcNow();

        await TouchHeartbeatAsync(agent, version, now, cancellationToken);

        var activePrinterIds = await data.ToListAsync(data.NoTracking(data.Printers).Where(p => p.IsActive).Select(p => p.Id), cancellationToken);
        var candidates = await data.ToListAsync(
            data.PrintJobs
                .Where(j => activePrinterIds.Contains(j.PrinterId))
                .Where(j => (j.Status == PrintJobStatus.Pending && j.NextAttemptAt <= now) ||
                            (j.Status == PrintJobStatus.Processing && j.LockedUntil != null && j.LockedUntil <= now))
                .OrderBy(j => j.CreatedAt)
                .Take(Math.Clamp(max, 1, 20)),
            cancellationToken);

        var claimed = new List<ClaimedJob>();
        foreach (var job in candidates)
        {
            job.Claim(agent, now, Lease);
            var document = PrintJson.Deserialize<PrintDocument>(job.PayloadJson)
                ?? throw new DomainException($"El trabajo {job.Id} tiene un documento ilegible.");
            claimed.Add(new ClaimedJob(job.Id, job.PrinterId, job.Attempts, job.Attempts > 1, document));
        }

        // Si otro agente reclamó a la vez, el token de concurrencia lanza conflicto: el llamador repite.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return claimed;
    }

    public async Task<ResultOutcome> ReportResultAsync(
        long jobId,
        string agent,
        bool success,
        TPVBar.PrintContracts.DeliveryLevel level,
        string? error,
        CancellationToken cancellationToken)
    {
        RequireAgent(agent);
        var now = time.GetUtcNow();

        var job = await data.FirstOrDefaultAsync(data.PrintJobs.Where(j => j.Id == jobId), cancellationToken)
            ?? throw new NotFoundException("El trabajo de impresión no existe.");

        if (job.Status is PrintJobStatus.Sent or PrintJobStatus.Failed or PrintJobStatus.Cancelled)
        {
            return ResultOutcome.AlreadyRecorded;
        }

        if (!job.IsHeldBy(agent))
        {
            // Resultado de un arrendamiento caducado: otro agente tiene ahora el trabajo.
            throw new DomainException("El trabajo está asignado a otro agente o su arrendamiento ha caducado.");
        }

        var printer = await data.FirstOrDefaultAsync(data.Printers.Where(p => p.Id == job.PrinterId), cancellationToken);
        var attemptNumber = job.Attempts;

        if (success)
        {
            var domainLevel = (DomainDelivery)(int)level;
            job.Succeed(domainLevel, now);
            printer?.RecordSuccess(now);
            data.Add(new PrintJobAttempt(job.Id, attemptNumber, agent, true, domainLevel, null, now));
        }
        else
        {
            var message = string.IsNullOrWhiteSpace(error) ? "Error desconocido." : error;
            var willRetry = job.Fail(message, now);
            printer?.RecordError(message, now);
            data.Add(new PrintJobAttempt(job.Id, attemptNumber, agent, false, DomainDelivery.None, message, now));

            if (!willRetry)
            {
                // Sin usuario: lo decide la cola, no una persona.
                auditLog.Add(AuditEvent.Create(
                    AuditActions.PrintJobFailed,
                    "print_job",
                    job.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    now,
                    detailsJson: System.Text.Json.JsonSerializer.Serialize(new { attempts = attemptNumber, error = job.LastError })));
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        events.Raise(new OperationEvent(OperationEventNames.PrinterStatusChanged));
        return ResultOutcome.Recorded;
    }

    public async Task<IReadOnlyList<PrinterConfig>> GetPrintersAsync(CancellationToken cancellationToken)
    {
        var printers = await data.ToListAsync(data.NoTracking(data.Printers).Where(p => p.IsActive).OrderBy(p => p.Id), cancellationToken);
        return [.. printers.Select(p => new PrinterConfig(p.Id, p.Name, p.Type.ToString(), p.Connection.ToString(), p.Target, p.PaperWidthMm))];
    }

    private async Task TouchHeartbeatAsync(string agent, string? version, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var beat = await data.FirstOrDefaultAsync(data.PrintAgentHeartbeats.Where(b => b.AgentName == agent), cancellationToken);
        if (beat is null)
        {
            data.Add(new PrintAgentHeartbeat(agent, version, now));
        }
        else
        {
            beat.Touch(version, now);
        }
    }

    private static void RequireAgent(string agent)
    {
        if (string.IsNullOrWhiteSpace(agent) || agent.Length > 80)
        {
            throw new DomainException("El nombre del agente es obligatorio (máximo 80 caracteres).");
        }
    }
}
