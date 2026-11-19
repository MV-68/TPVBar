using TPVBar.Application.Common;
using TPVBar.Application.Features.Kitchen;
using TPVBar.Application.Features.Orders;
using TPVBar.Application.Features.Printing;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Printing;
using TPVBar.Domain.Sales;
using TPVBar.Domain.Security;
using TPVBar.PrintContracts;

namespace TPVBar.Application.Tests;

public class KitchenAndPrintingServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<long> OrderWithBothStationsAsync(SalesFixture f)
    {
        var orderId = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 2, null, Guid.NewGuid(), Ct);
        await f.Orders().AddLineAsync(orderId, f.Tortilla.Id, 1, "poco hecha", Guid.NewGuid(), Ct);
        return orderId;
    }

    // ---------- Envío e impresión ----------

    [Fact]
    public async Task Enviar_EncolaUnTrabajoDeImpresionPorDestinoConElContenidoCorrecto()
    {
        var f = new SalesFixture().WithPrinters();
        var orderId = await OrderWithBothStationsAsync(f);
        var key = Guid.NewGuid();

        var result = await f.Orders().SendToKitchenAsync(orderId, key, Ct);

        Assert.Empty(result.Unprinted);
        Assert.Equal(2, f.Data.JobList.Count);
        Assert.All(f.Data.JobList, j => Assert.Equal(f.KitchenPrinter.Id, j.PrinterId));
        var kitchen = f.Data.JobList.Single(j => j.DedupeKey == $"kitchen:{key:N}:Kitchen");
        var document = PrintJson.Deserialize<PrintDocument>(kitchen.PayloadJson)!;
        Assert.Equal(PrintDocumentKind.KitchenTicket, document.Kind);
        Assert.Equal("Salón · Mesa 1", document.TableLabel);
        Assert.Equal("Tortilla", Assert.Single(document.Lines).Text);
        Assert.Equal("poco hecha", document.Lines[0].Detail);
        Assert.Contains("no es un documento fiscal", document.Footer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enviar_SinImpresoraConfigurada_AvisaPeroElPedidoQuedaEnCocina()
    {
        var f = new SalesFixture();
        var orderId = await OrderWithBothStationsAsync(f);

        var result = await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        Assert.Equal([PreparationStation.Kitchen, PreparationStation.Bar], result.Unprinted.Order());
        Assert.Empty(f.Data.JobList);
        Assert.Equal(2, f.Data.TicketList.Count);
    }

    [Fact]
    public async Task Enviar_ConImpresoraDesactivada_NoEncolaYAvisa()
    {
        var f = new SalesFixture().WithPrinters();
        f.KitchenPrinter.SetActive(false);
        var orderId = await OrderWithBothStationsAsync(f);

        var result = await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        Assert.Equal(2, result.Unprinted.Count);
        Assert.Empty(f.Data.JobList);
    }

    [Fact]
    public async Task Enviar_RepetirLaClave_NoEncolaOtraVez()
    {
        var f = new SalesFixture().WithPrinters();
        var orderId = await OrderWithBothStationsAsync(f);
        var key = Guid.NewGuid();

        await f.Orders().SendToKitchenAsync(orderId, key, Ct);
        await f.Orders().SendToKitchenAsync(orderId, key, Ct);

        Assert.Equal(2, f.Data.JobList.Count);
    }

    [Fact]
    public async Task Enviar_EmiteAvisosDeTiempoRealParaCadaTicket()
    {
        var f = new SalesFixture().WithPrinters();
        var orderId = await OrderWithBothStationsAsync(f);
        f.Events.Drain();

        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        var events = f.Events.Drain();
        Assert.Equal(2, events.Count(e => e.Name == OperationEventNames.KitchenTicketCreated && e.TicketId > 0));
        Assert.Contains(events, e => e.Name == OperationEventNames.OrderSentToKitchen);
        Assert.Contains(events, e => e.Name == OperationEventNames.PrintJobCreated);
    }

    [Fact]
    public async Task PedirLaCuenta_EncolaPreCuentaNoFiscalEnLaImpresoraDeTickets()
    {
        var f = new SalesFixture().WithPrinters();
        var orderId = await OrderWithBothStationsAsync(f);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        await f.Orders().RequestBillAsync(orderId, Ct);

        var job = f.Data.JobList.Single(j => j.JobType == PrintJobType.PreBill);
        Assert.Equal(f.ReceiptPrinter.Id, job.PrinterId);
        var document = PrintJson.Deserialize<PrintDocument>(job.PayloadJson)!;
        Assert.Equal(7.10m, document.Total);
        Assert.Contains("sin validez fiscal", document.Footer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnularLineaEnviada_LlegaAlTicketDeCocinaYImprimeAviso()
    {
        var f = new SalesFixture(Permission.TakeOrders, Permission.VoidLines).WithPrinters();
        var orderId = await OrderWithBothStationsAsync(f);
        var view = await f.Orders().GetAsync(orderId, Ct);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        var tortilla = view.Lines.Single(l => l.Name == "Tortilla");
        f.Events.Drain();

        await f.Orders().VoidLineAsync(orderId, tortilla.Id, "Cliente se fue", Ct);

        var kitchenTicket = f.Data.TicketList.Single(t => t.Station == PreparationStation.Kitchen);
        Assert.Equal(KitchenTicketStatus.Cancelled, kitchenTicket.Status);
        Assert.Equal(KitchenTicketStatus.Cancelled, kitchenTicket.Lines.Single().Status);
        Assert.Equal(KitchenTicketStatus.Pending, f.Data.TicketList.Single(t => t.Station == PreparationStation.Bar).Status);
        var job = f.Data.JobList.Single(j => j.JobType == PrintJobType.KitchenVoid);
        var document = PrintJson.Deserialize<PrintDocument>(job.PayloadJson)!;
        Assert.Contains("ANULADO", document.Title, StringComparison.Ordinal);
        Assert.Equal("Cliente se fue", document.Lines[0].Detail);
        Assert.Contains(f.Events.Drain(), e => e.Name == OperationEventNames.KitchenTicketStatusChanged);
    }

    // ---------- Cocina (KDS) ----------

    [Fact]
    public async Task Cocina_SoloVeLosTicketsVivosYSePuedeFiltrarPorDestino()
    {
        var f = new SalesFixture(Permission.TakeOrders, Permission.UseKitchen).WithPrinters();
        var orderId = await OrderWithBothStationsAsync(f);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        var all = await f.Kitchen().GetBoardAsync(null, Ct);
        var bar = await f.Kitchen().GetBoardAsync(PreparationStation.Bar, Ct);

        Assert.Equal(2, all.Count);
        var ticket = Assert.Single(bar);
        Assert.Equal("Caña", Assert.Single(ticket.Lines).Name);
        Assert.Equal("Salón · Mesa 1", ticket.TableLabel);
    }

    [Fact]
    public async Task Cocina_SinPermisoUseKitchen_EsDenegado()
    {
        var f = new SalesFixture(Permission.TakeOrders).WithPrinters();

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Kitchen().GetBoardAsync(null, Ct));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Kitchen().AdvanceAsync(1, KitchenTicketStatus.Preparing, Ct));
    }

    [Fact]
    public async Task Cocina_AvanzarElTicket_ActualizaLasLineasDeLaComandaYAudita()
    {
        var f = new SalesFixture(Permission.TakeOrders, Permission.UseKitchen).WithPrinters();
        var orderId = await OrderWithBothStationsAsync(f);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        var barTicket = f.Data.TicketList.Single(t => t.Station == PreparationStation.Bar);

        await f.Kitchen().AdvanceAsync(barTicket.Id, KitchenTicketStatus.Preparing, Ct);
        await f.Kitchen().AdvanceAsync(barTicket.Id, KitchenTicketStatus.Ready, Ct);

        var order = (await f.Orders().GetAsync(orderId, Ct));
        Assert.Equal(OrderLineStatus.Ready, order.Lines.Single(l => l.Name == "Caña").Status);
        Assert.Equal(OrderLineStatus.Sent, order.Lines.Single(l => l.Name == "Tortilla").Status);
        Assert.Equal(f.UserId, barTicket.UpdatedBy);
        Assert.Equal(2, f.Store.Audit.Count(e => e.Action == AuditActions.KitchenTicketAdvanced));
    }

    [Fact]
    public async Task Cocina_RepetirElMismoCambio_NoHaceNada()
    {
        var f = new SalesFixture(Permission.TakeOrders, Permission.UseKitchen).WithPrinters();
        var orderId = await OrderWithBothStationsAsync(f);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        var ticket = f.Data.TicketList.First();

        await f.Kitchen().AdvanceAsync(ticket.Id, KitchenTicketStatus.Preparing, Ct);
        var auditCount = f.Store.Audit.Count(e => e.Action == AuditActions.KitchenTicketAdvanced);
        await f.Kitchen().AdvanceAsync(ticket.Id, KitchenTicketStatus.Preparing, Ct);

        Assert.Equal(auditCount, f.Store.Audit.Count(e => e.Action == AuditActions.KitchenTicketAdvanced));
    }

    [Fact]
    public async Task Cocina_NoSePuedeRetrocederUnTicket()
    {
        var f = new SalesFixture(Permission.TakeOrders, Permission.UseKitchen).WithPrinters();
        var orderId = await OrderWithBothStationsAsync(f);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        var ticket = f.Data.TicketList.First();
        await f.Kitchen().AdvanceAsync(ticket.Id, KitchenTicketStatus.Ready, Ct);

        await Assert.ThrowsAsync<DomainException>(() => f.Kitchen().AdvanceAsync(ticket.Id, KitchenTicketStatus.Preparing, Ct));
    }

    [Fact]
    public async Task Cocina_UnTicketEntregadoDesapareceDelTablero()
    {
        var f = new SalesFixture(Permission.TakeOrders, Permission.UseKitchen).WithPrinters();
        var orderId = await OrderWithBothStationsAsync(f);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        var ticket = f.Data.TicketList.First();

        await f.Kitchen().AdvanceAsync(ticket.Id, KitchenTicketStatus.Delivered, Ct);

        Assert.DoesNotContain(await f.Kitchen().GetBoardAsync(null, Ct), t => t.Id == ticket.Id);
        Assert.NotNull(ticket.CompletedAt);
    }

    // ---------- Cola de impresión (lado agente) ----------

    private static async Task<(SalesFixture F, PrintJob Job)> WithPendingJobAsync()
    {
        var f = new SalesFixture(Permission.TakeOrders, Permission.ManagePrinting).WithPrinters();
        var orderId = await OrderWithBothStationsAsync(f);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        return (f, f.Data.JobList.First());
    }

    [Fact]
    public async Task Cola_ReclamarMarcaEnProcesoCuentaElIntentoYRegistraElLatido()
    {
        var (f, _) = await WithPendingJobAsync();

        var claimed = await f.Queue().ClaimAsync("agente-1", "1.0", 10, Ct);

        Assert.Equal(2, claimed.Count);
        Assert.All(claimed, c => Assert.Equal(1, c.Attempt));
        Assert.All(f.Data.JobList, j => Assert.Equal(PrintJobStatus.Processing, j.Status));
        Assert.Equal("agente-1", Assert.Single(f.Data.HeartbeatList).AgentName);
    }

    [Fact]
    public async Task Cola_UnTrabajoYaReclamadoNoSeOfreceAOtroAgenteHastaQueCaduqueElArrendamiento()
    {
        var (f, _) = await WithPendingJobAsync();
        await f.Queue().ClaimAsync("agente-1", null, 10, Ct);

        Assert.Empty(await f.Queue().ClaimAsync("agente-2", null, 10, Ct));

        f.Time.Advance(PrintQueueService.Lease + TimeSpan.FromSeconds(1));
        var reclaimed = await f.Queue().ClaimAsync("agente-2", null, 10, Ct);

        Assert.Equal(2, reclaimed.Count);
        Assert.All(reclaimed, c => Assert.True(c.IsRetry && c.Attempt == 2));
    }

    [Fact]
    public async Task Cola_NoOfreceTrabajosDeImpresorasDesactivadas()
    {
        var (f, _) = await WithPendingJobAsync();
        f.KitchenPrinter.SetActive(false);

        Assert.Empty(await f.Queue().ClaimAsync("agente-1", null, 10, Ct));
    }

    [Fact]
    public async Task Cola_ResultadoCorrecto_MarcaEnviadoConSuNivelDeEntrega()
    {
        var (f, job) = await WithPendingJobAsync();
        await f.Queue().ClaimAsync("agente-1", null, 10, Ct);

        var outcome = await f.Queue().ReportResultAsync(job.Id, "agente-1", true, TPVBar.PrintContracts.DeliveryLevel.AcceptedBySpooler, null, Ct);

        Assert.Equal(ResultOutcome.Recorded, outcome);
        Assert.Equal(PrintJobStatus.Sent, job.Status);
        Assert.Equal(TPVBar.Domain.Printing.DeliveryLevel.AcceptedBySpooler, job.DeliveryLevel);
        Assert.NotNull(f.KitchenPrinter.LastSuccessAt);
        Assert.Single(f.Data.AttemptList, a => a.Success && a.JobId == job.Id);
    }

    [Fact]
    public async Task Cola_Fallo_ReintentaConEsperaIncrementalYRegistraElError()
    {
        var (f, job) = await WithPendingJobAsync();
        await f.Queue().ClaimAsync("agente-1", null, 10, Ct);

        await f.Queue().ReportResultAsync(job.Id, "agente-1", false, TPVBar.PrintContracts.DeliveryLevel.None, "Sin papel", Ct);

        Assert.Equal(PrintJobStatus.Pending, job.Status);
        Assert.Equal("Sin papel", job.LastError);
        Assert.Equal(SalesFixture.Start + PrintJob.Backoff(1), job.NextAttemptAt);
        Assert.Equal("Sin papel", f.KitchenPrinter.LastError);
        Assert.DoesNotContain(await f.Queue().ClaimAsync("agente-1", null, 10, Ct), c => c.Id == job.Id); // aún en espera
    }

    [Fact]
    public async Task Cola_UnTrabajoFallidoNoBloqueaAlResto()
    {
        var (f, bad) = await WithPendingJobAsync();
        var claimed = await f.Queue().ClaimAsync("agente-1", null, 10, Ct);
        var good = claimed.Single(c => c.Id != bad.Id);

        await f.Queue().ReportResultAsync(bad.Id, "agente-1", false, TPVBar.PrintContracts.DeliveryLevel.None, "Error", Ct);
        await f.Queue().ReportResultAsync(good.Id, "agente-1", true, TPVBar.PrintContracts.DeliveryLevel.SentToDevice, null, Ct);

        Assert.Equal(PrintJobStatus.Sent, f.Data.JobList.Single(j => j.Id == good.Id).Status);
        Assert.Equal(PrintJobStatus.Pending, bad.Status);
    }

    [Fact]
    public async Task Cola_AgotadosLosIntentos_QuedaFallidoYAuditado()
    {
        var (f, job) = await WithPendingJobAsync();

        for (var i = 0; i < PrintJob.DefaultMaxAttempts; i++)
        {
            f.Time.Advance(TimeSpan.FromMinutes(10));
            await f.Queue().ClaimAsync("agente-1", null, 10, Ct);
            await f.Queue().ReportResultAsync(job.Id, "agente-1", false, TPVBar.PrintContracts.DeliveryLevel.None, "Impresora apagada", Ct);
        }

        Assert.Equal(PrintJobStatus.Failed, job.Status);
        Assert.Equal(PrintJob.DefaultMaxAttempts, job.Attempts);
        Assert.Contains(f.Store.Audit, e => e.Action == AuditActions.PrintJobFailed && e.EntityId == job.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Cola_ResultadoDeOtroAgenteODeUnArrendamientoCaducado_SeRechaza()
    {
        var (f, job) = await WithPendingJobAsync();
        await f.Queue().ClaimAsync("agente-1", null, 10, Ct);

        await Assert.ThrowsAsync<DomainException>(() =>
            f.Queue().ReportResultAsync(job.Id, "intruso", true, TPVBar.PrintContracts.DeliveryLevel.SentToDevice, null, Ct));
    }

    [Fact]
    public async Task Cola_RepetirElResultado_EsIdempotente()
    {
        var (f, job) = await WithPendingJobAsync();
        await f.Queue().ClaimAsync("agente-1", null, 10, Ct);
        await f.Queue().ReportResultAsync(job.Id, "agente-1", true, TPVBar.PrintContracts.DeliveryLevel.SentToDevice, null, Ct);

        var again = await f.Queue().ReportResultAsync(job.Id, "agente-1", true, TPVBar.PrintContracts.DeliveryLevel.SentToDevice, null, Ct);

        Assert.Equal(ResultOutcome.AlreadyRecorded, again);
        Assert.Single(f.Data.AttemptList, a => a.JobId == job.Id);
    }

    // ---------- Administración de impresión ----------

    [Fact]
    public async Task Admin_SinPermiso_EsDenegado()
    {
        var f = new SalesFixture(Permission.TakeOrders).WithPrinters();

        await Assert.ThrowsAsync<ForbiddenException>(() => f.PrintAdmin().GetOverviewAsync(Ct));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.PrintAdmin().TestPrintAsync(f.KitchenPrinter.Id, Ct));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.PrintAdmin().RetryAsync(1, Ct));
    }

    [Fact]
    public async Task Admin_ImpresionDePrueba_EncolaYAudita()
    {
        var f = new SalesFixture(Permission.ManagePrinting).WithPrinters();

        await f.PrintAdmin().TestPrintAsync(f.KitchenPrinter.Id, Ct);

        var job = Assert.Single(f.Data.JobList);
        Assert.Equal(PrintJobType.Test, job.JobType);
        var document = PrintJson.Deserialize<PrintDocument>(job.PayloadJson)!;
        Assert.Contains("SIMULADA", document.Subtitle, StringComparison.Ordinal);
        Assert.Contains(f.Store.Audit, e => e.Action == AuditActions.PrintTestRequested);
    }

    [Fact]
    public async Task Admin_ReintentarSoloTrabajosFallidos_YReimprimirCreaCopiaAuditada()
    {
        var (f, job) = await WithPendingJobAsync();
        await Assert.ThrowsAsync<DomainException>(() => f.PrintAdmin().RetryAsync(job.Id, Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.PrintAdmin().ReprintAsync(job.Id, Ct)); // aún en cola

        await f.Queue().ClaimAsync("agente-1", null, 10, Ct);
        await f.Queue().ReportResultAsync(job.Id, "agente-1", true, TPVBar.PrintContracts.DeliveryLevel.SentToDevice, null, Ct);
        var copyId = await f.PrintAdmin().ReprintAsync(job.Id, Ct);

        var copy = f.Data.JobList.Single(j => j.DedupeKey.StartsWith("reprint:", StringComparison.Ordinal));
        Assert.NotEqual(job.DedupeKey, copy.DedupeKey);
        Assert.Equal(job.PayloadJson, copy.PayloadJson);
        Assert.Equal(PrintJobStatus.Pending, copy.Status);
        Assert.Contains(f.Store.Audit, e => e.Action == AuditActions.PrintJobReprinted && e.EntityId == job.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(copyId >= 0);
    }

    [Fact]
    public async Task Admin_ResumenDeImpresion_ReflejaColaFallosYAgente()
    {
        var f = new SalesFixture(Permission.TakeOrders, Permission.ManagePrinting).WithPrinters();
        var orderId = await OrderWithBothStationsAsync(f);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        var before = await f.PrintAdmin().GetSummaryAsync(Ct);
        Assert.Equal(2, before.Pending);
        Assert.False(before.AgentOnline);
        Assert.True(before.AnyPrinterConfigured);

        await f.Queue().ClaimAsync("agente-1", null, 10, Ct);
        var during = await f.PrintAdmin().GetSummaryAsync(Ct);
        Assert.True(during.AgentOnline);

        f.Time.Advance(PrintAdminService.AgentOnlineWindow + TimeSpan.FromSeconds(1));
        Assert.False((await f.PrintAdmin().GetSummaryAsync(Ct)).AgentOnline);
    }

    [Fact]
    public async Task Admin_RutaDeDestinoYValidacionDeImpresoras()
    {
        var f = new SalesFixture(Permission.ManagePrinting);

        var id = await f.PrintAdmin().CreatePrinterAsync(
            new PrinterInput("Cocina", PrinterType.Kitchen, PrinterConnection.Network, "192.168.1.50:9100", 80), Ct);
        await f.PrintAdmin().SetRouteAsync(PreparationStation.Kitchen, id, Ct);

        Assert.Equal(id, Assert.Single(f.Data.RouteList).PrinterId);
        await Assert.ThrowsAsync<DomainException>(() => f.PrintAdmin().CreatePrinterAsync(
            new PrinterInput("Mala", PrinterType.Kitchen, PrinterConnection.Network, "sin-puerto", 80), Ct));
    }
}
