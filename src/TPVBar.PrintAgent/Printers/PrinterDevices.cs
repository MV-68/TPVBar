using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using TPVBar.PrintContracts;

namespace TPVBar.PrintAgent.Printers;

/// <summary>Un destino físico (o simulado) al que enviar un ticket ya renderizado.</summary>
internal interface IPrinterDevice
{
    /// <returns>Hasta dónde se sabe que ha llegado el trabajo. Nunca afirma "impreso" sin confirmación del hardware.</returns>
    Task<DeliveryLevel> SendAsync(PrinterConfig printer, long jobId, RenderedTicket ticket, CancellationToken cancellationToken);
}

internal static class PrinterDeviceFactory
{
    public static IPrinterDevice For(PrinterConfig printer)
    {
        ArgumentNullException.ThrowIfNull(printer);
        return printer.Connection switch
        {
            "Simulated" => new SimulatedDevice(),
            "Network" => new NetworkDevice(),
            "Windows" => new WindowsSpoolerDevice(),
            "Usb" => throw new NotSupportedException(
                "La conexión USB directa no está implementada. Instale la impresora en Windows y configúrela como tipo «Windows»."),
            _ => throw new NotSupportedException($"Tipo de conexión desconocido: {printer.Connection}."),
        };
    }
}

/// <summary>Escribe un archivo de texto. NO es un dispositivo real: no sale papel.</summary>
internal sealed class SimulatedDevice : IPrinterDevice
{
    public async Task<DeliveryLevel> SendAsync(PrinterConfig printer, long jobId, RenderedTicket ticket, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(printer.Target);
        var file = Path.Combine(printer.Target, $"ticket-{jobId:D6}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.txt");
        var content = "*** IMPRESORA SIMULADA: este archivo representa un ticket, no ha salido papel ***" + Environment.NewLine +
                      new string('=', 60) + Environment.NewLine + ticket.Text;
        await File.WriteAllTextAsync(file, content, new UTF8Encoding(false), cancellationToken);
        return DeliveryLevel.Simulated;
    }
}

/// <summary>Envía los bytes ESC/POS por un socket TCP (normalmente puerto 9100). Sin confirmación de papel.</summary>
internal sealed class NetworkDevice : IPrinterDevice
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<DeliveryLevel> SendAsync(PrinterConfig printer, long jobId, RenderedTicket ticket, CancellationToken cancellationToken)
    {
        var separator = printer.Target.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(printer.Target[(separator + 1)..], out var port))
        {
            throw new InvalidOperationException($"El destino «{printer.Target}» no tiene el formato equipo:puerto.");
        }

        var host = printer.Target[..separator];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(ticket.Bytes, timeout.Token);
            await stream.FlushAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"La impresora {printer.Target} no responde (tiempo de espera de {Timeout.TotalSeconds:0} s).");
        }

        return DeliveryLevel.SentToDevice;
    }
}

/// <summary>Entrega los bytes a la cola de impresión de Windows en modo RAW. Aceptado por la cola ≠ papel impreso.</summary>
internal sealed class WindowsSpoolerDevice : IPrinterDevice
{
    public Task<DeliveryLevel> SendAsync(PrinterConfig printer, long jobId, RenderedTicket ticket, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("La cola de impresión de Windows solo está disponible en Windows.");
        }

        WinSpool.WriteRaw(printer.Target, $"TPVBar ticket {jobId}", ticket.Bytes);
        return Task.FromResult(DeliveryLevel.AcceptedBySpooler);
    }
}

