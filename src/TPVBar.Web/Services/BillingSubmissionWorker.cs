using TPVBar.Application.Features.Invoicing;

namespace TPVBar.Web.Services;

/// <summary>
/// Envía en segundo plano los registros de facturación pendientes a la AEAT. No hace nada mientras el modo sea «Desactivado»
/// (valor por defecto). Respeta la espera que indica la AEAT; los errores se registran y se reintentan, nunca detienen el servidor.
/// </summary>
public sealed partial class BillingSubmissionWorker(IServiceScopeFactory scopes, IAeatGateway gateway, IConfiguration configuration, ILogger<BillingSubmissionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (gateway.Mode == AeatMode.Disabled)
        {
            LogDisabled(logger);
            return;
        }

        LogEnabled(logger, gateway.Mode);
        var interval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Billing:Aeat:PollSeconds", 30), 10, 600));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<BillingSubmissionService>().ProcessPendingAsync(stoppingToken);
                if (result.Attempted > 0)
                {
                    LogRun(logger, result.Attempted, result.Accepted, result.AcceptedWithErrors, result.Rejected, result.Failed);
                }
                else if (result.Skipped is not null)
                {
                    LogSkipped(logger, result.Skipped);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // un fallo inesperado no debe detener el servicio: se registra y se reintenta
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogError(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Envío a la AEAT desactivado (Billing:Aeat:Mode = Disabled).")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Envío a la AEAT activado en modo {Mode}.")]
    private static partial void LogEnabled(ILogger logger, AeatMode mode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Envío a la AEAT: {Attempted} registros ({Accepted} aceptados, {WithErrors} con errores, {Rejected} rechazados, {Failed} con fallo).")]
    private static partial void LogRun(ILogger logger, int attempted, int accepted, int withErrors, int rejected, int failed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Envío a la AEAT en espera: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error inesperado en el envío a la AEAT.")]
    private static partial void LogError(ILogger logger, Exception exception);
}
