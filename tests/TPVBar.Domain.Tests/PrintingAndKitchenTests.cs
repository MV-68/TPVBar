using TPVBar.Domain.Catalog;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Printing;
using TPVBar.Domain.Sales;

namespace TPVBar.Domain.Tests;

public class PrintingAndKitchenTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Cook = Guid.NewGuid();

    private static PrintJob NewJob() =>
        PrintJob.Create(1, PrintJobType.KitchenTicket, "order", "7", "{\"x\":1}", "kitchen:abc:Kitchen", Now);

    // ---------- PrintJob ----------

    [Fact]
    public void Job_Nuevo_EstaPendienteYDisponibleYa()
    {
        var job = NewJob();

        Assert.Equal(PrintJobStatus.Pending, job.Status);
        Assert.True(job.IsClaimable(Now));
        Assert.Equal(PrintJob.DefaultMaxAttempts, job.MaxAttempts);
    }

    [Fact]
    public void Job_Reclamar_LoPasaAEnProcesoConArrendamiento()
    {
        var job = NewJob();

        job.Claim("agente-1", Now, TimeSpan.FromMinutes(2));

        Assert.Equal(PrintJobStatus.Processing, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.True(job.IsHeldBy("agente-1"));
        Assert.False(job.IsHeldBy("agente-2"));
        Assert.False(job.IsClaimable(Now + TimeSpan.FromMinutes(1)));
        Assert.True(job.IsClaimable(Now + TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Job_NoSePuedeReclamarDosVeces()
    {
        var job = NewJob();
        job.Claim("agente-1", Now, TimeSpan.FromMinutes(2));

        Assert.Throws<DomainException>(() => job.Claim("agente-2", Now, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Job_Exito_QuedaEnviadoConNivelDeEntrega()
    {
        var job = NewJob();
        job.Claim("a", Now, TimeSpan.FromMinutes(2));

        job.Succeed(DeliveryLevel.SentToDevice, Now);

        Assert.Equal(PrintJobStatus.Sent, job.Status);
        Assert.Equal(DeliveryLevel.SentToDevice, job.DeliveryLevel);
        Assert.Null(job.LockedBy);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 45)]
    public void Job_Fallo_ProgramaReintentoConEsperaIncremental(int attempts, int expectedSeconds)
    {
        var job = NewJob();
        var clock = Now;
        for (var i = 1; i <= attempts; i++)
        {
            clock += TimeSpan.FromHours(1); // el trabajo ya es reclamable en cada vuelta
            job.Claim("a", clock, TimeSpan.FromMinutes(2));
            Assert.True(job.Fail("Sin papel", clock));
        }

        Assert.Equal(PrintJobStatus.Pending, job.Status);
        Assert.Equal("Sin papel", job.LastError);
        Assert.Equal(clock + TimeSpan.FromSeconds(expectedSeconds), job.NextAttemptAt);
        Assert.False(job.IsClaimable(clock));
        Assert.True(job.IsClaimable(job.NextAttemptAt));
    }

    [Fact]
    public void Job_Backoff_CreceYSeEstabiliza()
    {
        var waits = Enumerable.Range(1, 8).Select(PrintJob.Backoff).ToList();

        Assert.Equal(waits.Order(), waits);
        Assert.Equal(TimeSpan.FromMinutes(5), waits[^1]);
        Assert.Equal(TimeSpan.FromMinutes(5), waits[^2]);
    }

    [Fact]
    public void Job_AgotadosLosIntentos_QuedaFallidoYNoSeOfreceMas()
    {
        var job = NewJob();
        var clock = Now;
        for (var i = 0; i < PrintJob.DefaultMaxAttempts; i++)
        {
            clock += TimeSpan.FromHours(1);
            job.Claim("a", clock, TimeSpan.FromMinutes(2));
            job.Fail("Error", clock);
        }

        Assert.Equal(PrintJobStatus.Failed, job.Status);
        Assert.False(job.IsClaimable(clock + TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Job_ReintentoManual_ConcedeTresIntentosMasSoloSiHaFallado()
    {
        var job = NewJob();
        Assert.Throws<DomainException>(() => job.RetryManually(Now));

        var clock = Now;
        for (var i = 0; i < PrintJob.DefaultMaxAttempts; i++)
        {
            clock += TimeSpan.FromHours(1);
            job.Claim("a", clock, TimeSpan.FromMinutes(2));
            job.Fail("Error", clock);
        }

        job.RetryManually(clock);

        Assert.Equal(PrintJobStatus.Pending, job.Status);
        Assert.Equal(PrintJob.DefaultMaxAttempts + 3, job.MaxAttempts);
        Assert.True(job.IsClaimable(clock));
    }

    [Fact]
    public void Job_Cancelar_SoloPendienteOFallido()
    {
        var pending = NewJob();
        pending.Cancel(Now);
        Assert.Equal(PrintJobStatus.Cancelled, pending.Status);

        var sent = NewJob();
        sent.Claim("a", Now, TimeSpan.FromMinutes(2));
        sent.Succeed(DeliveryLevel.Simulated, Now);
        Assert.Throws<DomainException>(() => sent.Cancel(Now));
    }

    [Fact]
    public void Job_ResultadoSinReclamar_Falla()
    {
        var job = NewJob();

        Assert.Throws<DomainException>(() => job.Succeed(DeliveryLevel.Simulated, Now));
        Assert.Throws<DomainException>(() => job.Fail("x", Now));
    }

    [Fact]
    public void Job_SinContenidoOSinClave_Falla()
    {
        Assert.Throws<DomainException>(() => PrintJob.Create(1, PrintJobType.Test, "printer", "1", " ", "k", Now));
        Assert.Throws<DomainException>(() => PrintJob.Create(1, PrintJobType.Test, "printer", "1", "{}", "", Now));
    }

    // ---------- Printer ----------

    [Fact]
    public void Printer_Valida_ConexionDeRedYPapel()
    {
        Assert.NotNull(Printer.Create("Cocina", PrinterType.Kitchen, PrinterConnection.Network, "192.168.1.50:9100", 80));
        Assert.Throws<DomainException>(() => Printer.Create("Cocina", PrinterType.Kitchen, PrinterConnection.Network, "192.168.1.50", 80));
        Assert.Throws<DomainException>(() => Printer.Create("Cocina", PrinterType.Kitchen, PrinterConnection.Network, "host:99999", 80));
        Assert.Throws<DomainException>(() => Printer.Create("Cocina", PrinterType.Kitchen, PrinterConnection.Windows, "Epson", 70));
        Assert.Throws<DomainException>(() => Printer.Create(" ", PrinterType.Kitchen, PrinterConnection.Windows, "Epson", 80));
        Assert.Throws<DomainException>(() => Printer.Create("X", PrinterType.Kitchen, PrinterConnection.Windows, "", 80));
    }

    [Fact]
    public void Printer_RegistraUltimoExitoYUltimoError()
    {
        var printer = Printer.Create("Cocina", PrinterType.Kitchen, PrinterConnection.Windows, "Epson TM", 80);

        printer.RecordError("Sin papel", Now);
        Assert.Equal("Sin papel", printer.LastError);

        printer.RecordSuccess(Now);
        Assert.Null(printer.LastError);
        Assert.Equal(Now, printer.LastSuccessAt);
    }

    [Fact]
    public void Printer_SimuladaSeIdentificaComoTal() =>
        Assert.True(Printer.Create("Prueba", PrinterType.Receipt, PrinterConnection.Simulated, "carpeta", 80).IsSimulated);

    // ---------- Ticket de cocina ----------

    private static (Order Order, KitchenTicket Ticket) SentOrder()
    {
        var order = Order.Open(1, Cook, 2, Now);
        var product = Product.Create(1, 1, "Caña", 1.80m, PreparationStation.Bar, null, null, 1, Now);
        typeof(Product).GetProperty("Id")!.SetValue(product, 5);
        var line = order.AddLine(product, 21m, 1, null, Cook, Now);
        typeof(OrderLine).GetProperty("Id")!.SetValue(line, 10L);
        var sent = order.MarkPendingLinesSent(Now);
        return (order, KitchenTicket.Create(1, PreparationStation.Bar, "Salón · Mesa 1", sent, Cook, Now));
    }

    [Fact]
    public void Ticket_AvanzaSoloHaciaDelanteYRegistraQuienYCuando()
    {
        var (_, ticket) = SentOrder();
        var later = Now.AddMinutes(3);

        ticket.Advance(KitchenTicketStatus.Preparing, Cook, later);

        Assert.Equal(KitchenTicketStatus.Preparing, ticket.Status);
        Assert.Equal(later, ticket.UpdatedAt);
        Assert.Equal(Cook, ticket.UpdatedBy);
        Assert.All(ticket.Lines, l => Assert.Equal(KitchenTicketStatus.Preparing, l.Status));
        Assert.Throws<DomainException>(() => ticket.Advance(KitchenTicketStatus.Pending, Cook, later));
        Assert.Throws<DomainException>(() => ticket.Advance(KitchenTicketStatus.Cancelled, Cook, later));
    }

    [Fact]
    public void Ticket_Entregado_SeCierraYNoAdmiteMasCambios()
    {
        var (_, ticket) = SentOrder();
        ticket.Advance(KitchenTicketStatus.Delivered, Cook, Now);

        Assert.False(ticket.IsLive);
        Assert.NotNull(ticket.CompletedAt);
        Assert.Throws<DomainException>(() => ticket.Advance(KitchenTicketStatus.Ready, Cook, Now));
    }

    [Fact]
    public void Ticket_AnularLaUnicaLinea_CancelaElTicket()
    {
        var (_, ticket) = SentOrder();

        var found = ticket.CancelLine(10, Cook, Now);

        Assert.True(found);
        Assert.Equal(KitchenTicketStatus.Cancelled, ticket.Status);
        Assert.False(ticket.CancelLine(10, Cook, Now)); // ya anulada
        Assert.False(ticket.CancelLine(999, Cook, Now)); // no pertenece
    }

    [Fact]
    public void Order_AvanceDeCocina_NoTocaLineasAnuladas()
    {
        var (order, _) = SentOrder();
        order.VoidLine(10, "error", Cook, Now);

        order.SetLinesPreparationStatus([10], OrderLineStatus.Ready, Now);

        Assert.Equal(OrderLineStatus.Cancelled, order.Lines.Single().Status);
    }
}
