extern alias agent;

using System.Net;
using System.Net.Sockets;
using System.Text;
using agent::TPVBar.PrintAgent;
using agent::TPVBar.PrintAgent.Printers;
using agent::TPVBar.PrintAgent.Workers;
using Microsoft.Extensions.Options;
using TPVBar.PrintContracts;

namespace TPVBar.Integration.Tests;

/// <summary>Pruebas del agente que no necesitan servidor ni impresora: render ESC/POS, dispositivos y proceso.</summary>
public class AgentRenderingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 30, 0, TimeSpan.Zero);

    private static PrintDocument Kitchen(params PrintLine[] lines) =>
        new(PrintDocumentKind.KitchenTicket, "COCINA", null, "Salón · Mesa 1", Now, lines, "Ticket de preparación. No es un documento fiscal.");

    private static bool Contains(byte[] haystack, params byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public void Render_UsaCp858YCodificaAcentosEnyeYEuro()
    {
        var ticket = TicketRenderer.Render(
            new PrintDocument(PrintDocumentKind.Test, "PRUEBA", null, null, Now, [new PrintLine("áéíóú Ññ ¿¡ €")], null),
            80,
            isRetry: false);

        // 0x1B 0x74 0x13 = seleccionar página de códigos 19 (CP858).
        Assert.True(Contains(ticket.Bytes, 0x1B, 0x74, 0x13));
        Assert.True(Contains(ticket.Bytes, 0xA0)); // á
        Assert.True(Contains(ticket.Bytes, 0xA4)); // ñ
        Assert.True(Contains(ticket.Bytes, 0xA5)); // Ñ
        Assert.True(Contains(ticket.Bytes, 0xD5)); // €
        Assert.Contains("áéíóú Ññ ¿¡ €", ticket.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_CaracteresNoSoportados_SeSustituyenPorInterrogante()
    {
        var ticket = TicketRenderer.Render(
            new PrintDocument(PrintDocumentKind.Test, "T", null, null, Now, [new PrintLine("日本")], null), 80, false);

        Assert.True(Contains(ticket.Bytes, (byte)'?', (byte)'?'));
    }

    [Fact]
    public void Render_IniciaConResetYTerminaConCorte()
    {
        var ticket = TicketRenderer.Render(Kitchen(new PrintLine("Caña", 2)), 80, false);

        Assert.Equal([0x1B, 0x40], ticket.Bytes[..2]);
        Assert.Equal([0x1D, 0x56, 0x42, 0x00], ticket.Bytes[^4..]);
    }

    [Fact]
    public void Render_TicketDeCocina_UsaLetraGrandeYMuestraCantidadObservacionesYMesa()
    {
        var ticket = TicketRenderer.Render(Kitchen(new PrintLine("Tortilla de patatas", 1, "poco hecha")), 80, false);

        Assert.True(Contains(ticket.Bytes, 0x1D, 0x21, 0x11)); // doble ancho y alto
        Assert.Contains("1x Tortilla de patatas", ticket.Text, StringComparison.Ordinal);
        Assert.Contains(">> poco hecha", ticket.Text, StringComparison.Ordinal);
        Assert.Contains("Salón · Mesa 1", ticket.Text, StringComparison.Ordinal);
        Assert.Contains("COCINA", ticket.Text, StringComparison.Ordinal);
        Assert.Contains("16/11/2026 13:30", ticket.Text, StringComparison.Ordinal); // hora local de Madrid (UTC+1)
    }

    [Theory]
    [InlineData(80, 48)]
    [InlineData(58, 32)]
    public void Render_NingunaLineaSuperaElAnchoDelPapel(int paperMm, int columns)
    {
        Assert.Equal(columns, TicketRenderer.Columns(paperMm));
        var longName = string.Join(' ', Enumerable.Repeat("Hamburguesa-con-nombre-larguisimo", 4)) + " " + new string('x', 70);

        var ticket = TicketRenderer.Render(Kitchen(new PrintLine(longName, 3, longName)), paperMm, false);

        foreach (var line in ticket.Text.Split(Environment.NewLine))
        {
            Assert.True(line.Length <= columns, $"La línea «{line}» supera {columns} columnas.");
        }
    }

    [Fact]
    public void Render_Reintento_AvisaDeQuePuedeSerUnDuplicado()
    {
        var normal = TicketRenderer.Render(Kitchen(new PrintLine("Caña", 1)), 80, false);
        var retry = TicketRenderer.Render(Kitchen(new PrintLine("Caña", 1)), 80, true);

        Assert.DoesNotContain("REIMPRESION", normal.Text, StringComparison.Ordinal);
        Assert.Contains("REIMPRESION / REINTENTO", retry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PreCuenta_AlineaImportesConFormatoEspanolYTotal()
    {
        var document = new PrintDocument(
            PrintDocumentKind.PreBill,
            "CUENTA",
            "Pre-cuenta (no es una factura)",
            "Salón · Mesa 1",
            Now,
            [new PrintLine("Caña", 2, null, 3.60m), new PrintLine("Tortilla de patatas", 1, null, 3.50m)],
            "Documento sin validez fiscal.",
            7.10m);

        var ticket = TicketRenderer.Render(document, 80, false);

        Assert.Contains("3,60 €", ticket.Text, StringComparison.Ordinal);
        Assert.Contains("TOTAL", ticket.Text, StringComparison.Ordinal);
        Assert.Contains("7,10 €", ticket.Text, StringComparison.Ordinal);
        Assert.Contains("sin validez fiscal", ticket.Text, StringComparison.Ordinal);
        var total = ticket.Text.Split(Environment.NewLine).Single(l => l.StartsWith("TOTAL", StringComparison.Ordinal));
        Assert.EndsWith("7,10 €", total, StringComparison.Ordinal);
        Assert.Equal(48, total.Length);
    }

    [Fact]
    public void Wrap_PartePalabrasMasLargasQueLaLinea() =>
        Assert.Equal(["aaaa", "aaaa", "aa"], TicketRenderer.Wrap(new string('a', 10), 4));

    // ---------- Dispositivos ----------

    [Fact]
    public async Task Simulada_EscribeUnArchivoYNuncaDiceQueHayPapel()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tpvbar-agent-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var printer = new PrinterConfig(1, "Sim", "Kitchen", "Simulated", folder, 80);
            var ticket = TicketRenderer.Render(Kitchen(new PrintLine("Caña", 1)), 80, false);

            var level = await new SimulatedDevice().SendAsync(printer, 42, ticket, Ct);

            Assert.Equal(DeliveryLevel.Simulated, level);
            var file = Assert.Single(Directory.GetFiles(folder, "ticket-000042-*.txt"));
            var content = await File.ReadAllTextAsync(file, Ct);
            Assert.Contains("IMPRESORA SIMULADA", content, StringComparison.Ordinal);
            Assert.Contains("1x Caña", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [Fact]
    public async Task Red_EnviaLosBytesPorTcpYSoloAfirmaHaberloEnviado()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Ct);
            using var buffer = new MemoryStream();
            await client.GetStream().CopyToAsync(buffer, Ct);
            return buffer.ToArray();
        }, Ct);
        var ticket = TicketRenderer.Render(Kitchen(new PrintLine("Caña", 1)), 80, false);
        var printer = new PrinterConfig(1, "Red", "Kitchen", "Network", $"127.0.0.1:{port}", 80);

        var level = await new NetworkDevice().SendAsync(printer, 1, ticket, Ct);

        Assert.Equal(DeliveryLevel.SentToDevice, level); // nunca "Confirmed": sin confirmación de papel
        Assert.Equal(ticket.Bytes, await received);
    }

    [Fact]
    public async Task Red_PuertoCerrado_LanzaError()
    {
        var ticket = TicketRenderer.Render(Kitchen(new PrintLine("Caña", 1)), 80, false);
        var printer = new PrinterConfig(1, "Red", "Kitchen", "Network", "127.0.0.1:1", 80);

        await Assert.ThrowsAnyAsync<Exception>(() => new NetworkDevice().SendAsync(printer, 1, ticket, Ct));
    }

    [Fact]
    public void FabricaDeDispositivos_UsbNoSePretendeSoportar()
    {
        var usb = new PrinterConfig(1, "USB", "Kitchen", "Usb", "x", 80);

        var ex = Assert.Throws<NotSupportedException>(() => PrinterDeviceFactory.For(usb));
        Assert.Contains("Windows", ex.Message, StringComparison.Ordinal);
    }

    // ---------- Procesador de trabajos ----------

    private sealed class FakeDevice(Func<DeliveryLevel> result) : IPrinterDevice
    {
        public Task<DeliveryLevel> SendAsync(PrinterConfig printer, long jobId, RenderedTicket ticket, CancellationToken cancellationToken) =>
            Task.FromResult(result());
    }

    private static ClaimedJob Job(int printerId = 1, bool retry = false) =>
        new(7, printerId, retry ? 2 : 1, retry, Kitchen(new PrintLine("Caña", 1)));

    private static readonly PrinterConfig Printer1 = new(1, "Cocina", "Kitchen", "Network", "127.0.0.1:9100", 80);

    [Fact]
    public async Task Procesador_Exito_DevuelveElNivelDelDispositivo()
    {
        var processor = new PrintJobProcessor(_ => new FakeDevice(() => DeliveryLevel.AcceptedBySpooler), TimeSpan.FromSeconds(5));

        var outcome = await processor.ProcessAsync(Job(), [Printer1], Ct);

        Assert.True(outcome.Success);
        Assert.Equal(DeliveryLevel.AcceptedBySpooler, outcome.Level);
    }

    [Fact]
    public async Task Procesador_ErrorDelDispositivo_SeConvierteEnFalloSinLanzar()
    {
        var processor = new PrintJobProcessor(_ => new FakeDevice(() => throw new InvalidOperationException("Sin papel")), TimeSpan.FromSeconds(5));

        var outcome = await processor.ProcessAsync(Job(), [Printer1], Ct);

        Assert.False(outcome.Success);
        Assert.Contains("Sin papel", outcome.Error, StringComparison.Ordinal);
        Assert.Contains("Cocina", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Procesador_ImpresoraDesconocida_Falla()
    {
        var processor = new PrintJobProcessor(_ => new FakeDevice(() => DeliveryLevel.Simulated), TimeSpan.FromSeconds(5));

        var outcome = await processor.ProcessAsync(Job(printerId: 99), [Printer1], Ct);

        Assert.False(outcome.Success);
        Assert.Contains("99", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Procesador_DispositivoQueNoRespondeVenceElTiempoDeEspera()
    {
        var hung = new HangingDevice();
        var processor = new PrintJobProcessor(_ => hung, TimeSpan.FromMilliseconds(200));

        var outcome = await processor.ProcessAsync(Job(), [Printer1], Ct);

        Assert.False(outcome.Success);
        Assert.Contains("Tiempo de espera", outcome.Error, StringComparison.Ordinal);
    }

    private sealed class HangingDevice : IPrinterDevice
    {
        public async Task<DeliveryLevel> SendAsync(PrinterConfig printer, long jobId, RenderedTicket ticket, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return DeliveryLevel.SentToDevice;
        }
    }

    [Fact]
    public async Task Procesador_ParadaDelServicio_PropagaLaCancelacion()
    {
        using var cts = new CancellationTokenSource();
        var processor = new PrintJobProcessor(_ => new HangingDevice(), TimeSpan.FromSeconds(30));
        var task = processor.ProcessAsync(Job(), [Printer1], cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    // ---------- Configuración ----------

    [Fact]
    public void Opciones_SinClaveOUrlInvalida_SeRechazan()
    {
        Assert.Empty(new AgentOptions { ServerUrl = "http://localhost:5141", ApiKey = "clave" }.Validate());

        var problems = new AgentOptions { ServerUrl = "no-es-url", ApiKey = " ", PollIntervalSeconds = 0, BatchSize = 99 }.Validate();

        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.Contains("clave", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Opciones_NombrePorDefectoEsElDelEquipo() =>
        Assert.Equal(Environment.MachineName, new AgentOptions().EffectiveName);

    [Fact]
    public void El_agente_SoloConoceElContratoPublico()
    {
        var referencias = typeof(AgentOptions).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null && n.StartsWith("TPVBar", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(["TPVBar.PrintContracts"], referencias);
    }

    [Fact]
    public void ContratoJson_EsEstableYLosEnumsSonTexto()
    {
        var json = PrintJson.Serialize(new ResultRequest("agente", true, DeliveryLevel.AcceptedBySpooler, null));

        Assert.Contains("\"level\":\"AcceptedBySpooler\"", json, StringComparison.Ordinal);
        Assert.Equal(DeliveryLevel.AcceptedBySpooler, PrintJson.Deserialize<ResultRequest>(json)!.Level);
        Assert.NotNull(Options.Create(new AgentOptions()));
        Assert.NotNull(Encoding.UTF8);
    }

    [Fact]
    public void LosNivelesDeEntrega_CoincidenEntreElDominioYElContrato()
    {
        foreach (var name in Enum.GetNames<DeliveryLevel>())
        {
            Assert.Equal((int)Enum.Parse<DeliveryLevel>(name), (int)Enum.Parse<TPVBar.Domain.Printing.DeliveryLevel>(name));
        }

        Assert.Equal(Enum.GetNames<DeliveryLevel>().Length, Enum.GetNames<TPVBar.Domain.Printing.DeliveryLevel>().Length);
    }
}
