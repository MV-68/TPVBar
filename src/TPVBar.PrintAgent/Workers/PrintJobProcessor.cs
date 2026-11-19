using TPVBar.PrintAgent.Printers;
using TPVBar.PrintContracts;

namespace TPVBar.PrintAgent.Workers;

/// <summary>Resultado de intentar imprimir un trabajo, listo para informar al servidor.</summary>
internal sealed record JobOutcome(bool Success, DeliveryLevel Level, string? Error);

/// <summary>
/// Imprime UN trabajo: localiza la impresora, renderiza el documento y lo envía al dispositivo. Nunca
/// lanza: cualquier fallo se convierte en un resultado erróneo para que el servidor reintente con espera.
/// </summary>
internal sealed class PrintJobProcessor(Func<PrinterConfig, IPrinterDevice> deviceFactory, TimeSpan jobTimeout)
{
    public PrintJobProcessor()
        : this(PrinterDeviceFactory.For, TimeSpan.FromSeconds(30))
    {
    }

    public async Task<JobOutcome> ProcessAsync(ClaimedJob job, IReadOnlyList<PrinterConfig> printers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(printers);

        var printer = printers.FirstOrDefault(p => p.Id == job.PrinterId);
        if (printer is null)
        {
            return new JobOutcome(false, DeliveryLevel.None, $"La impresora {job.PrinterId} no existe o está desactivada.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(jobTimeout);

        try
        {
            var ticket = TicketRenderer.Render(job.Document, printer.PaperWidthMm, job.IsRetry);
            var level = await deviceFactory(printer).SendAsync(printer, job.Id, ticket, timeout.Token);
            return new JobOutcome(true, level, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // parada del servicio: no es un fallo del trabajo
        }
        catch (OperationCanceledException)
        {
            return new JobOutcome(false, DeliveryLevel.None, $"Tiempo de espera agotado ({jobTimeout.TotalSeconds:0} s) con la impresora «{printer.Name}».");
        }
        catch (Exception ex)
        {
            return new JobOutcome(false, DeliveryLevel.None, $"{printer.Name}: {ex.Message}");
        }
    }
}
