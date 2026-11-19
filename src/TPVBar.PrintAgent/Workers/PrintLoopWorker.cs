using Microsoft.Extensions.Options;
using TPVBar.PrintContracts;

namespace TPVBar.PrintAgent.Workers;

/// <summary>
/// Bucle del agente: reclama trabajos al servidor, los imprime y comunica el resultado. Si el servidor no
/// responde, espera con retroceso y vuelve a intentarlo: el agente nunca se detiene por un fallo de red.
/// </summary>
internal sealed partial class PrintLoopWorker(
    ServerClient server,
    PrintJobProcessor processor,
    IOptions<AgentOptions> options,
    ILogger<PrintLoopWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PrinterCacheTime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private IReadOnlyList<PrinterConfig> _printers = [];
    private DateTimeOffset _printersLoadedAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        var failures = 0;
        LogStarted(logger, options.Value.EffectiveName, options.Value.ServerUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = poll;
            try
            {
                var processed = await RunOnceAsync(stoppingToken);
                failures = 0;
                if (processed > 0)
                {
                    continue; // más trabajo probable: sin esperar
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, poll.TotalSeconds * Math.Pow(2, Math.Min(failures, 5))));
                LogServerUnreachable(logger, ex.Message, delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Un ciclo completo. Devuelve cuántos trabajos se han procesado.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        // Reclamar sirve también de latido: el servidor sabe que el agente está vivo.
        var jobs = await server.ClaimAsync(cancellationToken);
        foreach (var job in jobs)
        {
            await HandleAsync(job, cancellationToken);
        }

        return jobs.Count;
    }

    private async Task HandleAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        var printers = await GetPrintersAsync(job.PrinterId, cancellationToken);
        var outcome = await processor.ProcessAsync(job, printers, cancellationToken);

        if (outcome.Success)
        {
            var level = outcome.Level.ToString();
            LogPrinted(logger, job.Id, level, job.Attempt);
        }
        else
        {
            LogFailed(logger, job.Id, outcome.Error ?? string.Empty, job.Attempt);
        }

        await ReportWithRetryAsync(job.Id, outcome, cancellationToken);
    }

    private async Task<IReadOnlyList<PrinterConfig>> GetPrintersAsync(int neededPrinterId, CancellationToken cancellationToken)
    {
        var stale = DateTimeOffset.UtcNow - _printersLoadedAt > PrinterCacheTime;
        if (stale || _printers.All(p => p.Id != neededPrinterId))
        {
            _printers = await server.GetPrintersAsync(cancellationToken);
            _printersLoadedAt = DateTimeOffset.UtcNow;
        }

        return _printers;
    }

    private async Task ReportWithRetryAsync(long jobId, JobOutcome outcome, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await server.ReportAsync(jobId, outcome.Success, outcome.Level, outcome.Error, cancellationToken);
                return;
            }
            catch (ServerRejectedException ex) when (ex.Status is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.NotFound)
            {
                // El servidor ya no nos considera dueños del trabajo (arrendamiento caducado): no hay nada que hacer.
                LogReportRejected(logger, jobId, ex.Message);
                return;
            }
            catch (Exception ex) when (attempt < 3 && ex is not OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Si no se puede informar, el arrendamiento caducará y el trabajo se ofrecerá de nuevo (puede reimprimirse).
                LogReportLost(logger, jobId, ex.Message);
                return;
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Agente '{Agent}' iniciado. Servidor: {Server}.")]
    private static partial void LogStarted(ILogger logger, string agent, string server);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "No se puede hablar con el servidor: {Reason}. Reintento en {Seconds:0} s.")]
    private static partial void LogServerUnreachable(ILogger logger, string reason, double seconds);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Trabajo {JobId} entregado ({Level}), intento {Attempt}.")]
    private static partial void LogPrinted(ILogger logger, long jobId, string level, int attempt);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Trabajo {JobId} falló (intento {Attempt}): {Error}")]
    private static partial void LogFailed(ILogger logger, long jobId, string error, int attempt);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "El servidor rechazó el resultado del trabajo {JobId}: {Reason}")]
    private static partial void LogReportRejected(ILogger logger, long jobId, string reason);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "No se pudo comunicar el resultado del trabajo {JobId}: {Reason}. Puede reimprimirse al caducar su arrendamiento.")]
    private static partial void LogReportLost(ILogger logger, long jobId, string reason);
}
