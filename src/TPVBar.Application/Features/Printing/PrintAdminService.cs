using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Printing;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Printing;

/// <summary>Administración de impresoras y de la cola: configurar, probar, reintentar, cancelar y reimprimir.</summary>
public sealed class PrintAdminService(
    IAppData data,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    AuditWriter audit,
    PrintEnqueuer enqueuer,
    IOperationEvents events,
    TimeProvider time)
{
    /// <summary>Un agente se considera conectado si contactó con el servidor en los últimos 30 segundos.</summary>
    public static readonly TimeSpan AgentOnlineWindow = TimeSpan.FromSeconds(30);

    public async Task<PrintOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.ManagePrinting);

        var printers = await data.ToListAsync(data.NoTracking(data.Printers).OrderBy(p => p.Type).ThenBy(p => p.Name), cancellationToken);
        var routes = await data.ToListAsync(data.NoTracking(data.PrinterRoutes), cancellationToken);
        var open = await data.ToListAsync(
            data.NoTracking(data.PrintJobs).Where(j => j.Status == PrintJobStatus.Pending || j.Status == PrintJobStatus.Processing || j.Status == PrintJobStatus.Failed)
                .Select(j => new { j.PrinterId, j.Status }),
            cancellationToken);
        var lastSeen = await LastAgentSeenAsync(cancellationToken);

        var rows = printers.Select(p => new PrinterRow(
            p.Id,
            p.Name,
            p.Type,
            p.Connection,
            p.Target,
            p.PaperWidthMm,
            p.IsActive,
            p.IsSimulated,
            p.LastSuccessAt,
            p.LastErrorAt,
            p.LastError,
            open.Count(j => j.PrinterId == p.Id && j.Status != PrintJobStatus.Failed),
            open.Count(j => j.PrinterId == p.Id && j.Status == PrintJobStatus.Failed))).ToList();

        return new PrintOverview(rows, [.. routes.Select(r => new RouteRow(r.Station, r.PrinterId))], IsOnline(lastSeen), lastSeen);
    }

    /// <summary>Estado para el indicador de impresión: lo pueden consultar quienes toman comandas o gestionan impresión.</summary>
    public async Task<PrintSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        if (!user.Has(Permission.TakeOrders) && !user.Has(Permission.ManagePrinting))
        {
            throw new ForbiddenException();
        }

        var jobs = await data.ToListAsync(
            data.NoTracking(data.PrintJobs).Where(j => j.Status == PrintJobStatus.Pending || j.Status == PrintJobStatus.Processing || j.Status == PrintJobStatus.Failed)
                .Select(j => new { j.Status, j.LastError }),
            cancellationToken);
        var any = await data.AnyAsync(data.Printers.Where(p => p.IsActive), cancellationToken);
        var lastSeen = await LastAgentSeenAsync(cancellationToken);

        return new PrintSummary(
            jobs.Count(j => j.Status != PrintJobStatus.Failed),
            jobs.Count(j => j.Status == PrintJobStatus.Failed),
            IsOnline(lastSeen),
            any,
            jobs.Where(j => j.LastError != null).Select(j => j.LastError).FirstOrDefault());
    }

    public async Task<int> CreatePrinterAsync(PrinterInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        user.Require(Permission.ManagePrinting);

        var printer = Printer.Create(input.Name, input.Type, input.Connection, input.Target, input.PaperWidthMm);
        data.Add(printer);
        await SaveAsync(cancellationToken);

        audit.Write(AuditActions.PrinterChanged, "printer", printer.Id, new { action = "created", printer.Name, type = printer.Type.ToString(), connection = printer.Connection.ToString() });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        events.Raise(new OperationEvent(OperationEventNames.PrinterStatusChanged));
        return printer.Id;
    }

    public async Task UpdatePrinterAsync(int id, PrinterInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        user.Require(Permission.ManagePrinting);

        var printer = await FindPrinterAsync(id, cancellationToken);
        printer.Update(input.Name, input.Type, input.Connection, input.Target, input.PaperWidthMm);

        audit.Write(AuditActions.PrinterChanged, "printer", id, new { action = "updated", printer.Name, connection = printer.Connection.ToString() });
        await SaveAsync(cancellationToken);
        events.Raise(new OperationEvent(OperationEventNames.PrinterStatusChanged));
    }

    public async Task SetPrinterActiveAsync(int id, bool active, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManagePrinting);
        var printer = await FindPrinterAsync(id, cancellationToken);
        printer.SetActive(active);

        audit.Write(AuditActions.PrinterChanged, "printer", id, new { action = active ? "activated" : "deactivated" });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        events.Raise(new OperationEvent(OperationEventNames.PrinterStatusChanged));
    }

    /// <summary>Asigna (o quita, con <see langword="null"/>) la impresora de un destino de preparación.</summary>
    public async Task SetRouteAsync(PreparationStation station, int? printerId, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManagePrinting);

        var route = await data.FirstOrDefaultAsync(data.PrinterRoutes.Where(r => r.Station == station), cancellationToken);
        if (printerId is null)
        {
            if (route is not null)
            {
                throw new DomainException("Para dejar un destino sin impresora, desactive la impresora asignada.");
            }

            return;
        }

        var printer = await FindPrinterAsync(printerId.Value, cancellationToken);
        if (route is null)
        {
            data.Add(new PrinterRoute(station, printer.Id));
        }
        else
        {
            route.PointTo(printer.Id);
        }

        audit.Write(AuditActions.PrinterChanged, "printer_route", station.ToString(), new { printerId = printer.Id });
        await SaveAsync(cancellationToken);
    }

    public async Task<long> TestPrintAsync(int printerId, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManagePrinting);
        var printer = await FindPrinterAsync(printerId, cancellationToken);
        if (!printer.IsActive)
        {
            throw new DomainException("La impresora está desactivada.");
        }

        var job = enqueuer.EnqueueTest(printer);
        audit.Write(AuditActions.PrintTestRequested, "printer", printer.Id);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return job.Id;
    }

    public async Task<IReadOnlyList<PrintJobRow>> ListJobsAsync(int take, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManagePrinting);

        var printers = (await data.ToListAsync(data.NoTracking(data.Printers), cancellationToken)).ToDictionary(p => p.Id, p => p.Name);
        var jobs = await data.ToListAsync(data.NoTracking(data.PrintJobs).OrderByDescending(j => j.Id).Take(Math.Clamp(take, 1, 500)), cancellationToken);

        return jobs.Select(j => new PrintJobRow(
            j.Id,
            j.PrinterId,
            printers.GetValueOrDefault(j.PrinterId, "?"),
            j.JobType,
            $"{j.ReferenceType} {j.ReferenceId}",
            j.Status,
            j.DeliveryLevel,
            j.Attempts,
            j.MaxAttempts,
            j.CreatedAt,
            j.NextAttemptAt,
            j.ProcessedAt,
            j.LastError)).ToList();
    }

    public async Task RetryAsync(long jobId, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManagePrinting);
        var job = await FindJobAsync(jobId, cancellationToken);
        job.RetryManually(time.GetUtcNow());

        audit.Write(AuditActions.PrintJobRetried, "print_job", jobId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        events.Raise(new OperationEvent(OperationEventNames.PrintJobCreated));
    }

    public async Task CancelAsync(long jobId, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManagePrinting);
        var job = await FindJobAsync(jobId, cancellationToken);
        job.Cancel(time.GetUtcNow());

        audit.Write(AuditActions.PrintJobCancelled, "print_job", jobId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        events.Raise(new OperationEvent(OperationEventNames.PrinterStatusChanged));
    }

    /// <summary>Imprime de nuevo un trabajo ya enviado (copia nueva y distinta, con auditoría).</summary>
    public async Task<long> ReprintAsync(long jobId, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManagePrinting);
        var original = await FindJobAsync(jobId, cancellationToken);
        if (original.Status is PrintJobStatus.Pending or PrintJobStatus.Processing)
        {
            throw new DomainException("El trabajo todavía está en cola: espere a que se imprima o cancélelo.");
        }

        var copy = PrintJob.Create(
            original.PrinterId,
            original.JobType,
            original.ReferenceType,
            original.ReferenceId,
            original.PayloadJson,
            $"reprint:{original.Id}:{Guid.NewGuid():N}",
            time.GetUtcNow());
        data.Add(copy);

        audit.Write(AuditActions.PrintJobReprinted, "print_job", jobId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        events.Raise(new OperationEvent(OperationEventNames.PrintJobCreated));
        return copy.Id;
    }

    private async Task<DateTimeOffset?> LastAgentSeenAsync(CancellationToken cancellationToken)
    {
        var beats = await data.ToListAsync(data.NoTracking(data.PrintAgentHeartbeats), cancellationToken);
        return beats.Count == 0 ? null : beats.Max(b => b.LastSeenAt);
    }

    private bool IsOnline(DateTimeOffset? lastSeen) => lastSeen is { } seen && time.GetUtcNow() - seen <= AgentOnlineWindow;

    private async Task<Printer> FindPrinterAsync(int id, CancellationToken cancellationToken) =>
        await data.FirstOrDefaultAsync(data.Printers.Where(p => p.Id == id), cancellationToken)
            ?? throw new NotFoundException("La impresora no existe.");

    private async Task<PrintJob> FindJobAsync(long id, CancellationToken cancellationToken) =>
        await data.FirstOrDefaultAsync(data.PrintJobs.Where(j => j.Id == id), cancellationToken)
            ?? throw new NotFoundException("El trabajo de impresión no existe.");

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateEntryException ex)
        {
            throw new DomainException("Ya existe una impresora con ese nombre.", ex);
        }
    }
}
