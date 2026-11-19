using TPVBar.Application.Common;
using TPVBar.Application.Features.Cash;
using TPVBar.Application.Features.Orders;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Cash;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Printing;
using TPVBar.Domain.Sales;
using TPVBar.Domain.Security;
using TPVBar.PrintContracts;

namespace TPVBar.Application.Tests;

public class PaymentAndCashTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Permission[] All =
    [
        Permission.TakeOrders, Permission.TakePayments, Permission.OpenCloseCash, Permission.PerformRefunds, Permission.ReopenTables, Permission.VoidLines,
    ];

    private static SalesFixture NewFixture(params Permission[] permissions) =>
        new SalesFixture(permissions.Length == 0 ? All : permissions).WithPrinters().WithPaymentMethods();

    /// <summary>Caja abierta con 100 € y una comanda de 7,10 € (2 cañas + 1 tortilla) ya enviada.</summary>
    private static async Task<(SalesFixture F, long OrderId)> ReadyAsync(params Permission[] permissions)
    {
        var f = NewFixture(permissions);
        await f.Cash().OpenAsync(100m, Ct);
        var orderId = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 2, null, Guid.NewGuid(), Ct);
        await f.Orders().AddLineAsync(orderId, f.Tortilla.Id, 1, null, Guid.NewGuid(), Ct);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        return (f, orderId);
    }

    private static PayRequest Pay(SalesFixture f, long orderId, PaymentMethod method, decimal amount, decimal? tendered = null, string? reference = null, Guid? key = null) =>
        new(orderId, method.Id, amount, tendered, reference, key ?? Guid.NewGuid());

    // ---------- Cobro ----------

    [Fact]
    public async Task Cobro_Total_EnEfectivoConCambio_CierraLaComandaYLiberaLaMesa()
    {
        var (f, orderId) = await ReadyAsync();

        var result = await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 7.10m, tendered: 10m), Ct);

        Assert.Equal(2.90m, result.Change);
        Assert.True(result.OrderPaid);
        Assert.Equal(0m, result.Balance);
        var order = f.Data.OrderList.Single();
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.NotNull(order.ClosedAt);
        var floor = await f.Floor().GetFloorAsync(Ct);
        Assert.Equal(TableStatus.Free, floor.Zones.Single().Tables.Single(t => t.Id == f.Mesa1.Id).Status);
        var payment = Assert.Single(f.Data.PaymentList);
        Assert.Equal(7.10m, payment.Amount);
        Assert.Equal(10m, payment.TenderedAmount);
        Assert.Equal(2.90m, payment.ChangeGiven);
        Assert.Equal(f.UserId, payment.ReceivedBy);
    }

    [Fact]
    public async Task Cobro_DivididoEntreVariosMetodos_QuedaSaldadoAlSumarElTotal()
    {
        var (f, orderId) = await ReadyAsync();

        var first = await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 4.00m), Ct);
        Assert.False(first.OrderPaid);
        Assert.Equal(3.10m, first.Balance);
        Assert.Equal(OrderStatus.PendingPayment, f.Data.OrderList.Single().Status);

        var second = await f.Payments().PayAsync(Pay(f, orderId, f.CardMethod, 3.10m), Ct);

        Assert.True(second.OrderPaid);
        Assert.Equal(2, f.Data.PaymentList.Count);
        Assert.Equal(7.10m, f.Data.OrderList.Single().PaidTotal);
        Assert.Equal(OrderStatus.Paid, f.Data.OrderList.Single().Status);
    }

    [Theory]
    [InlineData(7.11)]
    [InlineData(100)]
    public async Task Cobro_MasQueElSaldo_SeRechaza(double amount)
    {
        var (f, orderId) = await ReadyAsync();

        var ex = await Assert.ThrowsAsync<DomainException>(() => f.Payments().PayAsync(Pay(f, orderId, f.CardMethod, (decimal)amount), Ct));

        Assert.Contains("saldo pendiente", ex.Message, StringComparison.Ordinal);
        Assert.Empty(f.Data.PaymentList);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.005)]
    public async Task Cobro_ImporteInvalido_SeRechaza(double amount)
    {
        var (f, orderId) = await ReadyAsync();

        await Assert.ThrowsAsync<DomainException>(() => f.Payments().PayAsync(Pay(f, orderId, f.CardMethod, (decimal)amount), Ct));
    }

    [Fact]
    public async Task Cobro_EfectivoEntregadoMenorQueElImporte_SeRechaza()
    {
        var (f, orderId) = await ReadyAsync();

        await Assert.ThrowsAsync<DomainException>(() => f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 5m, tendered: 4m), Ct));
    }

    [Fact]
    public async Task Cobro_ConTarjetaNoAdmiteEntregadoNiCambio()
    {
        var (f, orderId) = await ReadyAsync();

        await Assert.ThrowsAsync<DomainException>(() => f.Payments().PayAsync(Pay(f, orderId, f.CardMethod, 5m, tendered: 10m), Ct));
        Assert.Empty(f.Data.PaymentList);
    }

    [Fact]
    public async Task Cobro_SinCajaAbierta_SeRechaza()
    {
        var f = NewFixture();
        var orderId = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, Guid.NewGuid(), Ct);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        var ex = await Assert.ThrowsAsync<DomainException>(() => f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 1.80m), Ct));

        Assert.Contains("caja", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cobro_ConLineasSinEnviar_SeRechaza()
    {
        var f = NewFixture();
        await f.Cash().OpenAsync(0m, Ct);
        var orderId = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, Guid.NewGuid(), Ct);

        await Assert.ThrowsAsync<DomainException>(() => f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 1.80m), Ct));
    }

    [Fact]
    public async Task Cobro_ComandaYaCobrada_SeRechaza()
    {
        var (f, orderId) = await ReadyAsync();
        await f.Payments().PayAsync(Pay(f, orderId, f.CardMethod, 7.10m), Ct);

        await Assert.ThrowsAsync<DomainException>(() => f.Payments().PayAsync(Pay(f, orderId, f.CardMethod, 1m), Ct));
        Assert.Single(f.Data.PaymentList);
    }

    [Fact]
    public async Task Cobro_MismaClaveDeIdempotencia_NoCobraDosVeces()
    {
        var (f, orderId) = await ReadyAsync();
        var key = Guid.NewGuid();

        await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 2m, key: key), Ct);
        var again = await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 2m, key: key), Ct);

        Assert.True(again.AlreadyProcessed);
        Assert.Single(f.Data.PaymentList);
        Assert.Equal(2m, f.Data.OrderList.Single().PaidTotal);
    }

    [Fact]
    public async Task Cobro_MetodoQueExigeReferencia_NoSeAceptaSinElla()
    {
        var (f, orderId) = await ReadyAsync();

        await Assert.ThrowsAsync<DomainException>(() => f.Payments().PayAsync(Pay(f, orderId, f.RefMethod, 3m), Ct));
        await f.Payments().PayAsync(Pay(f, orderId, f.RefMethod, 3m, reference: "BZ-123"), Ct);

        Assert.Equal("BZ-123", Assert.Single(f.Data.PaymentList).ExternalReference);
    }

    [Fact]
    public async Task Cobro_MetodoDesactivado_SeRechaza()
    {
        var (f, orderId) = await ReadyAsync();
        f.CardMethod.SetActive(false);

        await Assert.ThrowsAsync<DomainException>(() => f.Payments().PayAsync(Pay(f, orderId, f.CardMethod, 1m), Ct));
    }

    [Fact]
    public async Task Cobro_SinPermisoTakePayments_EsDenegado()
    {
        var (f, orderId) = await ReadyAsync();
        f.User.Set(f.UserId, [Permission.TakeOrders], "test");

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 1m), Ct));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Payments().GetCheckoutAsync(orderId, Ct));
    }

    [Fact]
    public async Task Cobro_ConUnPagoParcial_LaComandaQuedaBloqueadaParaEditarReabrirYCancelar()
    {
        var (f, orderId) = await ReadyAsync();
        await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 2m), Ct);

        await Assert.ThrowsAsync<DomainException>(() => f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Orders().ReopenAsync(orderId, Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Orders().CancelAsync(orderId, Ct));
    }

    [Fact]
    public async Task Cobro_QuedaAuditadoSinDatosSensibles()
    {
        var (f, orderId) = await ReadyAsync();

        await f.Payments().PayAsync(Pay(f, orderId, f.RefMethod, 7.10m, reference: "SECRETO-9999"), Ct);

        var audit = Assert.Single(f.Store.Audit, e => e.Action == AuditActions.PaymentRegistered);
        Assert.Equal(f.UserId, audit.UserId);
        Assert.DoesNotContain("SECRETO-9999", audit.DetailsJson, StringComparison.Ordinal); // la referencia no se copia a auditoría
        Assert.Contains("BIZUM", audit.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cobro_EmiteAvisosDeTiempoReal()
    {
        var (f, orderId) = await ReadyAsync();
        f.Events.Drain();

        await f.Payments().PayAsync(Pay(f, orderId, f.CardMethod, 7.10m), Ct);

        var events = f.Events.Drain();
        Assert.Contains(events, e => e.Name == OperationEventNames.PaymentRegistered && e.OrderId == orderId);
        Assert.Contains(events, e => e.Name == OperationEventNames.CashSessionChanged);
        Assert.Contains(events, e => e.Name == OperationEventNames.TableStatusChanged);
    }

    // ---------- Justificante ----------

    [Fact]
    public async Task Justificante_SeEncolaAlSaldarConDesgloseDeIvaPagosYCambio()
    {
        var (f, orderId) = await ReadyAsync();

        var result = await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 7.10m, tendered: 10m), Ct);

        Assert.True(result.ReceiptQueued);
        var job = f.Data.JobList.Single(j => j.JobType == PrintJobType.Receipt);
        Assert.Equal(f.ReceiptPrinter.Id, job.PrinterId);
        var document = PrintJson.Deserialize<PrintDocument>(job.PayloadJson)!;
        Assert.Equal(PrintDocumentKind.Receipt, document.Kind);
        Assert.Equal(7.10m, document.Total);
        Assert.Contains("No es una factura", document.Footer, StringComparison.Ordinal);
        var iva = document.Sections!.Single(s => s.Title == "IVA incluido").Lines;
        Assert.Equal(2, iva.Count);
        Assert.Equal([0.32m, 0.62m], iva.Select(l => l.Amount!.Value).Order()); // 10 %: 0,32 € · 21 %: 0,62 €
        Assert.Equal(0.94m, iva.Sum(l => l.Amount));
        var pagos = document.Sections!.Single(s => s.Title == "Pagos").Lines;
        Assert.Contains(pagos, l => l.Text == "Efectivo" && l.Amount == 7.10m);
        Assert.Contains(pagos, l => l.Text == "  Cambio" && l.Amount == 2.90m);
    }

    [Fact]
    public async Task Justificante_NoSeEncolaConUnPagoParcial()
    {
        var (f, orderId) = await ReadyAsync();

        var result = await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 2m), Ct);

        Assert.False(result.ReceiptQueued);
        Assert.DoesNotContain(f.Data.JobList, j => j.JobType == PrintJobType.Receipt);
    }

    [Fact]
    public async Task Justificante_SinImpresoraDeTickets_AvisaDeQueNoSeHaImpreso()
    {
        var (f, orderId) = await ReadyAsync();
        f.ReceiptPrinter.SetActive(false);

        var result = await f.Payments().PayAsync(Pay(f, orderId, f.CardMethod, 7.10m), Ct);

        Assert.True(result.OrderPaid);
        Assert.False(result.ReceiptQueued);
    }

    [Fact]
    public async Task Justificante_Reimpresion_SoloDeComandasCobradasYQuedaAuditada()
    {
        var (f, orderId) = await ReadyAsync();
        await Assert.ThrowsAsync<DomainException>(() => f.Payments().ReprintReceiptAsync(orderId, Ct));
        await f.Payments().PayAsync(Pay(f, orderId, f.CardMethod, 7.10m), Ct);

        var queued = await f.Payments().ReprintReceiptAsync(orderId, Ct);

        Assert.True(queued);
        var receipts = f.Data.JobList.Where(j => j.JobType == PrintJobType.Receipt).ToList();
        Assert.Equal(2, receipts.Count);
        Assert.NotEqual(receipts[0].DedupeKey, receipts[1].DedupeKey);
        Assert.Contains("COPIA", PrintJson.Deserialize<PrintDocument>(receipts[1].PayloadJson)!.Subtitle, StringComparison.Ordinal);
        Assert.Contains(f.Store.Audit, e => e.Action == AuditActions.ReceiptReprinted);
    }

    // ---------- Devoluciones ----------

    [Fact]
    public async Task Devolucion_ExigePermisoMotivoYCajaAbierta()
    {
        var (f, orderId) = await ReadyAsync();
        var paid = await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 7.10m), Ct);

        f.User.Set(f.UserId, [Permission.TakePayments], "test");
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Payments().RefundAsync(paid.PaymentId, 1m, "x", Guid.NewGuid(), Ct));

        f.User.Set(f.UserId, All, "test");
        await Assert.ThrowsAsync<DomainException>(() => f.Payments().RefundAsync(paid.PaymentId, 1m, " ", Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Payments().RefundAsync(paid.PaymentId, 0m, "motivo", Guid.NewGuid(), Ct));
        Assert.Empty(f.Data.RefundList);
    }

    [Fact]
    public async Task Devolucion_EsUnaOperacionCompensatoria_ElPagoOriginalNoCambia()
    {
        var (f, orderId) = await ReadyAsync();
        var paid = await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 7.10m), Ct);

        await f.Payments().RefundAsync(paid.PaymentId, 2.00m, "Producto en mal estado", Guid.NewGuid(), Ct);

        var payment = Assert.Single(f.Data.PaymentList);
        Assert.Equal(7.10m, payment.Amount); // inmutable
        var refund = Assert.Single(f.Data.RefundList);
        Assert.Equal(2.00m, refund.Amount);
        Assert.Equal(payment.PaymentMethodId, refund.PaymentMethodId);
        Assert.Equal("Producto en mal estado", refund.Reason);
        Assert.Equal(5.10m, f.Data.OrderList.Single().PaidTotal);
        Assert.Equal(OrderStatus.Paid, f.Data.OrderList.Single().Status);
        Assert.Contains(f.Store.Audit, e => e.Action == AuditActions.PaymentRefunded && e.DetailsJson.Contains("Producto en mal estado", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Devolucion_NoPuedeSuperarLoCobradoMenosLoYaDevuelto()
    {
        var (f, orderId) = await ReadyAsync();
        var paid = await f.Payments().PayAsync(Pay(f, orderId, f.CardMethod, 7.10m), Ct);
        await f.Payments().RefundAsync(paid.PaymentId, 5m, "motivo", Guid.NewGuid(), Ct);

        var ex = await Assert.ThrowsAsync<DomainException>(() => f.Payments().RefundAsync(paid.PaymentId, 2.11m, "motivo", Guid.NewGuid(), Ct));

        Assert.Contains("2,10", ex.Message.Replace('.', ','), StringComparison.Ordinal);
        await f.Payments().RefundAsync(paid.PaymentId, 2.10m, "resto", Guid.NewGuid(), Ct);
        Assert.Equal(0m, f.Data.OrderList.Single().PaidTotal);
    }

    [Fact]
    public async Task Devolucion_MismaClave_NoDevuelveDosVeces()
    {
        var (f, orderId) = await ReadyAsync();
        var paid = await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 7.10m), Ct);
        var key = Guid.NewGuid();

        await f.Payments().RefundAsync(paid.PaymentId, 1m, "motivo", key, Ct);
        await f.Payments().RefundAsync(paid.PaymentId, 1m, "motivo", key, Ct);

        Assert.Single(f.Data.RefundList);
    }

    [Fact]
    public async Task Devolucion_SinCajaAbierta_SeRechaza()
    {
        var (f, orderId) = await ReadyAsync();
        var paid = await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 7.10m), Ct);
        await f.Cash().CloseAsync(100m, null, Ct);

        await Assert.ThrowsAsync<DomainException>(() => f.Payments().RefundAsync(paid.PaymentId, 1m, "motivo", Guid.NewGuid(), Ct));
    }

    // ---------- Caja ----------

    [Fact]
    public async Task Caja_AbrirExigePermisoYNoPermiteDosAbiertas()
    {
        var f = NewFixture(Permission.TakePayments);
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Cash().OpenAsync(50m, Ct));

        f.User.Set(f.UserId, All, "test");
        await f.Cash().OpenAsync(50m, Ct);

        await Assert.ThrowsAsync<DomainException>(() => f.Cash().OpenAsync(50m, Ct));
        Assert.Single(f.Data.SessionList);
        Assert.Contains(f.Store.Audit, e => e.Action == AuditActions.CashOpened);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10.005)]
    public async Task Caja_FondoInicialInvalido_SeRechaza(double amount)
    {
        var f = NewFixture();

        await Assert.ThrowsAsync<DomainException>(() => f.Cash().OpenAsync((decimal)amount, Ct));
    }

    [Fact]
    public async Task Caja_EfectivoEsperado_CuentaSoloElCajonConMovimientosYDevoluciones()
    {
        var (f, orderId) = await ReadyAsync(); // fondo 100
        var cash = await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 4.00m, tendered: 20m), Ct); // el cambio no cuenta: entran 4,00
        await f.Payments().PayAsync(Pay(f, orderId, f.CardMethod, 3.10m), Ct); // tarjeta: no entra en el cajón
        await f.Payments().RefundAsync(cash.PaymentId, 1.00m, "Error de cobro", Guid.NewGuid(), Ct);
        await f.Cash().AddMovementAsync(CashMovementType.CashOut, 20m, "Pago a proveedor", Ct);
        await f.Cash().AddMovementAsync(CashMovementType.CashIn, 5m, "Aporte de cambio", Ct);

        var result = await f.Cash().CloseAsync(87.50m, "Cierre de prueba", Ct);

        // 100 + 4,00 − 1,00 + 5 − 20 = 88,00
        Assert.Equal(88.00m, result.Report.ExpectedCash);
        Assert.Equal(87.50m, result.Report.CountedCash);
        Assert.Equal(-0.50m, result.Report.Difference);
        var session = Assert.Single(f.Data.SessionList);
        Assert.Equal(CashSessionStatus.Closed, session.Status);
        Assert.Equal(88.00m, session.ExpectedCash);
        Assert.Equal(f.UserId, session.ClosedBy);
        Assert.Equal(1, result.Report.OrdersCount);
        Assert.Equal(2, result.Report.PaymentsCount);
        var cashRow = result.Report.Methods.Single(m => m.IsCash);
        Assert.Equal((4.00m, 1.00m, 3.00m), (cashRow.Payments, cashRow.Refunds, cashRow.Net));
        Assert.Equal(3.00m + 3.10m, result.Report.NetTotal);
        Assert.Contains(f.Store.Audit, e => e.Action == AuditActions.CashClosed && e.DetailsJson.Contains("88", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Caja_ElRecuentoEsACiegas_ElEsperadoSoloSeRevelaAlCerrar()
    {
        var (f, orderId) = await ReadyAsync();
        await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 7.10m), Ct);

        var open = await f.Cash().GetCurrentAsync(Ct);

        Assert.NotNull(open);
        Assert.Null(open.ExpectedCash);
        Assert.Null(open.Difference);
        Assert.Equal(7.10m, open.TotalPayments);
    }

    [Fact]
    public async Task Caja_NoSePuedeCerrarConUnCobroParcialSinCompletar()
    {
        var (f, orderId) = await ReadyAsync();
        await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 2m), Ct);

        var ex = await Assert.ThrowsAsync<DomainException>(() => f.Cash().CloseAsync(102m, null, Ct));

        Assert.Contains("cobro parcial", ex.Message, StringComparison.Ordinal);
        await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 5.10m), Ct);
        await f.Cash().CloseAsync(107.10m, null, Ct); // ya sí
    }

    [Fact]
    public async Task Caja_CerradaNoAdmiteCobrosNiMovimientosNiSegundoCierre()
    {
        var (f, orderId) = await ReadyAsync();
        await f.Cash().CloseAsync(100m, null, Ct);

        await Assert.ThrowsAsync<DomainException>(() => f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 1m), Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Cash().AddMovementAsync(CashMovementType.CashIn, 1m, "x", Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Cash().CloseAsync(100m, null, Ct));
    }

    [Fact]
    public async Task Caja_CerrarEncolaElResumenInternoDeCierre()
    {
        var (f, orderId) = await ReadyAsync();
        await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 7.10m), Ct);

        var result = await f.Cash().CloseAsync(107.10m, null, Ct);

        Assert.True(result.ReportPrinted);
        var document = PrintJson.Deserialize<PrintDocument>(f.Data.JobList.Single(j => j.JobType == PrintJobType.CashClose).PayloadJson)!;
        Assert.Equal(PrintDocumentKind.CashClose, document.Kind);
        Assert.Contains("no es un documento fiscal", document.Footer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(document.Sections!.Single(s => s.Title == "Efectivo").Lines, l => l.Text == "DIFERENCIA" && l.Amount == 0m);
    }

    [Fact]
    public async Task Caja_MovimientosValidanImporteConceptoYCajaAbierta()
    {
        var f = NewFixture();
        await Assert.ThrowsAsync<DomainException>(() => f.Cash().AddMovementAsync(CashMovementType.CashIn, 5m, "sin caja", Ct));
        await f.Cash().OpenAsync(0m, Ct);

        await Assert.ThrowsAsync<DomainException>(() => f.Cash().AddMovementAsync(CashMovementType.CashIn, 0m, "cero", Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Cash().AddMovementAsync(CashMovementType.CashOut, 5m, " ", Ct));
        await f.Cash().AddMovementAsync(CashMovementType.CashOut, 5m, "Retirada", Ct);

        var view = await f.Cash().GetCurrentAsync(Ct);
        Assert.Equal(5m, view!.CashOut);
        Assert.Equal("Cajero de prueba", Assert.Single(view.Movements).By);
        Assert.Contains(f.Store.Audit, e => e.Action == AuditActions.CashMovementRegistered);
    }

    [Fact]
    public async Task Caja_SinAccesoNoPuedeVerNiCerrar()
    {
        var f = NewFixture(Permission.TakeOrders);

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Cash().GetStatusAsync(Ct));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Cash().CloseAsync(0m, null, Ct));
    }

    [Fact]
    public async Task Cobro_VistaDeCobro_IndicaSiSePuedeCobrar()
    {
        var f = NewFixture();
        var orderId = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, Guid.NewGuid(), Ct);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        Assert.False((await f.Payments().GetCheckoutAsync(orderId, Ct)).CanPay); // caja cerrada

        await f.Cash().OpenAsync(0m, Ct);
        var checkout = await f.Payments().GetCheckoutAsync(orderId, Ct);
        Assert.True(checkout.CanPay);
        Assert.Equal(1.80m, checkout.Balance);
        Assert.Equal(["Efectivo", "Tarjeta", "Bizum"], checkout.Methods.Select(m => m.Name));
    }

    // ---------- Métodos de pago ----------

    [Fact]
    public async Task Metodos_CodigoSeNormalizaYNoSePuedeDejarSinMetodosActivos()
    {
        var f = NewFixture(Permission.ManageCatalog);

        var id = await f.MethodAdmin().CreateAsync(" bizum_2 ", "Bizum 2", false, false, 9, Ct);

        Assert.Equal("BIZUM_2", f.Data.MethodList.Single(m => m.Id == id).Code);
        await Assert.ThrowsAsync<DomainException>(() => f.MethodAdmin().CreateAsync("mal código!", "X", false, false, 1, Ct));
        foreach (var method in f.Data.MethodList.Where(m => m.Id != f.CashMethod.Id).ToList())
        {
            await f.MethodAdmin().SetActiveAsync(method.Id, false, Ct);
        }

        await Assert.ThrowsAsync<DomainException>(() => f.MethodAdmin().SetActiveAsync(f.CashMethod.Id, false, Ct));
    }

    [Fact]
    public async Task Metodos_NoSePuedeCambiarSiUsaElCajonCuandoYaTieneCobros()
    {
        var (f, orderId) = await ReadyAsync(Permission.TakeOrders, Permission.TakePayments, Permission.OpenCloseCash, Permission.ManageCatalog);
        await f.Payments().PayAsync(Pay(f, orderId, f.CashMethod, 1m), Ct);

        await Assert.ThrowsAsync<DomainException>(() => f.MethodAdmin().UpdateAsync(f.CashMethod.Id, "Efectivo", false, false, 1, Ct));
        await f.MethodAdmin().UpdateAsync(f.CashMethod.Id, "Efectivo (caja)", false, true, 1, Ct);

        Assert.Equal("Efectivo (caja)", f.CashMethod.Name);
    }

    [Fact]
    public async Task Metodos_SinPermisoDeCatalogo_EsDenegado()
    {
        var f = NewFixture(Permission.TakePayments);

        await Assert.ThrowsAsync<ForbiddenException>(() => f.MethodAdmin().ListAsync(Ct));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.MethodAdmin().CreateAsync("X", "X", false, false, 1, Ct));
    }
}
