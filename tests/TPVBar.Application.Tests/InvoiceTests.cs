using System.Reflection;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Cash;
using TPVBar.Application.Features.Invoicing;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Invoicing;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Tests;

public class InvoiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Permission[] All =
    [
        Permission.TakeOrders, Permission.TakePayments, Permission.OpenCloseCash, Permission.IssueInvoices, Permission.RectifyInvoices,
        Permission.ManageConfiguration, Permission.ViewReports,
    ];

    private static readonly InvoiceRecipient Customer = new("Cliente Ejemplo S.L.", "a58818501", "Calle Falsa 123, Madrid");

    private static SalesFixture NewFixture(decimal? limit = null, params Permission[] permissions) =>
        new SalesFixture(permissions.Length == 0 ? All : permissions).WithPrinters().WithPaymentMethods().WithIssuer(limit);

    /// <summary>Comanda cobrada por completo: 2 cañas (21 %) + 1 tortilla (10 %) = 7,10 €.</summary>
    private static async Task<long> PaidOrderAsync(SalesFixture f, bool openCash = true)
    {
        if (openCash)
        {
            await f.Cash().OpenAsync(100m, Ct);
        }

        var orderId = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 2, null, Guid.NewGuid(), Ct);
        await f.Orders().AddLineAsync(orderId, f.Tortilla.Id, 1, null, Guid.NewGuid(), Ct);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        await f.Payments().PayAsync(new PayRequest(orderId, f.CashMethod.Id, 7.10m, 10m, null, Guid.NewGuid()), Ct);
        return orderId;
    }

    private static IssueInvoiceRequest Simplified(long orderId, Guid? key = null) => new(orderId, InvoiceKind.Simplified, null, key ?? Guid.NewGuid());

    // ---------- Emisión ----------

    [Fact]
    public async Task Emitir_Simplificada_NumeraGuardaTotalesYGeneraElPrimerRegistro()
    {
        var f = NewFixture();
        var orderId = await PaidOrderAsync(f);

        var result = await f.Invoices().IssueAsync(Simplified(orderId), Ct);

        Assert.Equal("S2026-000001", result.FullNumber);
        var invoice = f.Data.InvoiceList.Single();
        Assert.Equal("F2", invoice.TypeCode);
        Assert.Equal(7.10m, invoice.Total);
        Assert.Equal(invoice.Total, invoice.Base + invoice.Tax);
        Assert.Equal("Bar de Prueba S.L.", invoice.IssuerName);
        Assert.Null(invoice.RecipientNif);
        Assert.Equal(new DateOnly(2026, 11, 16), invoice.IssueDate);

        var record = Assert.Single(f.Data.RecordList);
        Assert.Equal(1, record.Sequence);
        Assert.Equal(string.Empty, record.PreviousHash);
        Assert.Equal(64, record.Hash.Length);
        Assert.Equal(record.Hash, record.RecomputeHash());
        Assert.Equal(invoice.Id, record.InvoiceId);
        Assert.Equal(SubmissionStatus.Pending, Assert.Single(f.Data.SubmissionList).Status);
        Assert.Equal(1, f.Data.HeadList.Single().LastSequence);
        Assert.Contains(f.Store.Audit, a => a.Action == AuditActions.InvoiceIssued);
    }

    [Fact]
    public async Task Emitir_ConsecutivasEncadenaLosRegistrosYLaCadenaEsValida()
    {
        var f = NewFixture();
        var first = await PaidOrderAsync(f);
        var second = await PaidOrderAsync(f, openCash: false);

        var a = await f.Invoices().IssueAsync(Simplified(first), Ct);
        var b = await f.Invoices().IssueAsync(Simplified(second), Ct);

        Assert.Equal("S2026-000001", a.FullNumber);
        Assert.Equal("S2026-000002", b.FullNumber);
        Assert.Equal(f.Data.RecordList[0].Hash, f.Data.RecordList[1].PreviousHash);
        var check = await f.Invoices().VerifyChainAsync(Ct);
        Assert.True(check.Ok);
        Assert.Equal(2, check.Records);
    }

    [Fact]
    public async Task Emitir_Completa_GuardaClienteConNifNormalizadoYUsaTipoF1()
    {
        var f = NewFixture();
        var orderId = await PaidOrderAsync(f);

        var result = await f.Invoices().IssueAsync(new IssueInvoiceRequest(orderId, InvoiceKind.Complete, Customer, Guid.NewGuid()), Ct);

        Assert.StartsWith("F2026-", result.FullNumber, StringComparison.Ordinal);
        var invoice = f.Data.InvoiceList.Single();
        Assert.Equal("F1", invoice.TypeCode);
        Assert.Equal("A58818501", invoice.RecipientNif);
        Assert.Equal("Cliente Ejemplo S.L.", invoice.RecipientName);
    }

    [Fact]
    public async Task Emitir_CompletaSinClienteONifInvalido_SeRechaza()
    {
        var f = NewFixture();
        var orderId = await PaidOrderAsync(f);

        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().IssueAsync(new IssueInvoiceRequest(orderId, InvoiceKind.Complete, null, Guid.NewGuid()), Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().IssueAsync(new IssueInvoiceRequest(orderId, InvoiceKind.Complete, Customer with { Nif = "12345678A" }, Guid.NewGuid()), Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().IssueAsync(new IssueInvoiceRequest(orderId, InvoiceKind.Complete, Customer with { Address = " " }, Guid.NewGuid()), Ct));
        Assert.Empty(f.Data.InvoiceList);
        Assert.Empty(f.Data.RecordList);
    }

    [Fact]
    public async Task Emitir_SimplificadaPorEncimaDelLimite_ExigeFacturaCompleta()
    {
        var f = NewFixture(limit: 5m);
        var orderId = await PaidOrderAsync(f);

        var ex = await Assert.ThrowsAsync<DomainException>(() => f.Invoices().IssueAsync(Simplified(orderId), Ct));

        Assert.Contains("límite", ex.Message, StringComparison.Ordinal);
        var ok = await f.Invoices().IssueAsync(new IssueInvoiceRequest(orderId, InvoiceKind.Complete, Customer, Guid.NewGuid()), Ct);
        Assert.NotEmpty(ok.FullNumber);
    }

    [Fact]
    public async Task Emitir_SinDatosDelEmisor_SeRechaza()
    {
        var f = new SalesFixture(All).WithPrinters().WithPaymentMethods();
        var orderId = await PaidOrderAsync(f);

        var ex = await Assert.ThrowsAsync<DomainException>(() => f.Invoices().IssueAsync(Simplified(orderId), Ct));

        Assert.Contains("emisor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emitir_ComandaNoCobrada_SeRechaza()
    {
        var f = NewFixture();
        await f.Cash().OpenAsync(100m, Ct);
        var orderId = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 1, null, Guid.NewGuid(), Ct);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().IssueAsync(Simplified(orderId), Ct));
        Assert.Empty(f.Data.InvoiceList);
    }

    [Fact]
    public async Task Emitir_SinPermiso_EsDenegado()
    {
        var f = NewFixture(null, Permission.TakeOrders, Permission.TakePayments, Permission.OpenCloseCash);
        var orderId = await PaidOrderAsync(f);

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Invoices().IssueAsync(Simplified(orderId), Ct));
    }

    [Fact]
    public async Task Emitir_DosVecesLaMismaComanda_SeRechazaYLaMismaClaveEsIdempotente()
    {
        var f = NewFixture();
        var orderId = await PaidOrderAsync(f);
        var key = Guid.NewGuid();

        var first = await f.Invoices().IssueAsync(Simplified(orderId, key), Ct);
        var retry = await f.Invoices().IssueAsync(Simplified(orderId, key), Ct);

        Assert.False(first.AlreadyProcessed);
        Assert.True(retry.AlreadyProcessed);
        Assert.Equal(first.InvoiceId, retry.InvoiceId);
        var ex = await Assert.ThrowsAsync<DomainException>(() => f.Invoices().IssueAsync(Simplified(orderId), Ct));
        Assert.Contains("ya tiene la factura", ex.Message, StringComparison.Ordinal);
        Assert.Single(f.Data.InvoiceList);
    }

    // ---------- Rectificación ----------

    [Fact]
    public async Task Rectificar_Total_DejaLaOperacionACeroYPermiteRefacturar()
    {
        var f = NewFixture();
        var orderId = await PaidOrderAsync(f);
        var issued = await f.Invoices().IssueAsync(Simplified(orderId), Ct);
        var original = f.Data.InvoiceList.Single();
        var all = original.Lines.Select(l => new RectifyLineRequest(l.Id, l.Quantity)).ToList();

        var result = await f.Invoices().RectifyAsync(new RectifyInvoiceRequest(issued.InvoiceId, "Error en el cobro", all, Guid.NewGuid()), Ct);

        Assert.StartsWith("R2026-", result.FullNumber, StringComparison.Ordinal);
        var rect = f.Data.InvoiceList.Single(i => i.Kind == InvoiceKind.Rectificative);
        Assert.Equal("R5", rect.TypeCode);
        Assert.Equal(-original.Total, rect.Total);
        Assert.Equal(-original.Tax, rect.Tax);
        Assert.Equal(original.Id, rect.RectifiedInvoiceId);
        Assert.Equal(2, f.Data.RecordList.Count);

        // Operación a cero: ya se puede emitir la factura completa que pide el cliente.
        var complete = await f.Invoices().IssueAsync(new IssueInvoiceRequest(orderId, InvoiceKind.Complete, Customer, Guid.NewGuid()), Ct);
        Assert.NotEmpty(complete.FullNumber);
        Assert.True((await f.Invoices().VerifyChainAsync(Ct)).Ok);
    }

    [Fact]
    public async Task Rectificar_Parcial_NoPermiteRectificarMasDeLoFacturado_YLaSumaFinalCuadra()
    {
        var f = NewFixture();
        var orderId = await PaidOrderAsync(f);
        var issued = await f.Invoices().IssueAsync(new IssueInvoiceRequest(orderId, InvoiceKind.Complete, Customer, Guid.NewGuid()), Ct);
        var caña = f.Data.InvoiceList.Single().Lines.Single(l => l.Description == "Caña");

        await f.Invoices().RectifyAsync(new RectifyInvoiceRequest(issued.InvoiceId, "Una caña devuelta", [new(caña.Id, 1)], Guid.NewGuid()), Ct);
        var rect1 = f.Data.InvoiceList.Single(i => i.Kind == InvoiceKind.Rectificative);
        Assert.Equal("R1", rect1.TypeCode);
        Assert.Equal(-1.80m, rect1.Total);

        // Queda 1 caña: pedir 2 es más de lo facturado.
        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().RectifyAsync(new RectifyInvoiceRequest(issued.InvoiceId, "Otra", [new(caña.Id, 2)], Guid.NewGuid()), Ct));
        await f.Invoices().RectifyAsync(new RectifyInvoiceRequest(issued.InvoiceId, "La otra caña", [new(caña.Id, 1)], Guid.NewGuid()), Ct);

        var rectified = f.Data.InvoiceList.Where(i => i.Kind == InvoiceKind.Rectificative).SelectMany(i => i.Lines).Sum(l => l.Total);
        Assert.Equal(-caña.Total, rectified);
        var view = await f.Invoices().GetAsync(issued.InvoiceId, Ct);
        Assert.Equal(0m, view.Lines.Single(l => l.Description == "Caña").RectifiableQuantity);
        Assert.Equal(2, view.Rectifications.Count);
    }

    [Fact]
    public async Task Rectificar_SinMotivoSinLineasORectificativa_SeRechaza()
    {
        var f = NewFixture();
        var orderId = await PaidOrderAsync(f);
        var issued = await f.Invoices().IssueAsync(Simplified(orderId), Ct);
        var line = f.Data.InvoiceList.Single().Lines.First();

        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().RectifyAsync(new RectifyInvoiceRequest(issued.InvoiceId, " ", [new(line.Id, 1)], Guid.NewGuid()), Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().RectifyAsync(new RectifyInvoiceRequest(issued.InvoiceId, "x", [], Guid.NewGuid()), Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().RectifyAsync(new RectifyInvoiceRequest(issued.InvoiceId, "x", [new(999, 1)], Guid.NewGuid()), Ct));

        var rect = await f.Invoices().RectifyAsync(new RectifyInvoiceRequest(issued.InvoiceId, "Motivo", [new(line.Id, 1)], Guid.NewGuid()), Ct);
        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().RectifyAsync(new RectifyInvoiceRequest(rect.InvoiceId, "Otra vez", [new(1, 1)], Guid.NewGuid()), Ct));
    }

    [Fact]
    public async Task Rectificar_SinPermisoDeRectificar_EsDenegado()
    {
        var f = NewFixture(null, Permission.TakeOrders, Permission.TakePayments, Permission.OpenCloseCash, Permission.IssueInvoices);
        var orderId = await PaidOrderAsync(f);
        var issued = await f.Invoices().IssueAsync(Simplified(orderId), Ct);

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Invoices().RectifyAsync(new RectifyInvoiceRequest(issued.InvoiceId, "x", [new(1, 1)], Guid.NewGuid()), Ct));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Invoices().CancelAsync(issued.InvoiceId, "x", Guid.NewGuid(), Ct));
    }

    // ---------- Anulación ----------

    [Fact]
    public async Task Anular_GeneraRegistroDeAnulacion_BloqueaRectificarYPermiteRefacturar()
    {
        var f = NewFixture();
        var orderId = await PaidOrderAsync(f);
        var issued = await f.Invoices().IssueAsync(Simplified(orderId), Ct);

        await f.Invoices().CancelAsync(issued.InvoiceId, "Emitida por error", Guid.NewGuid(), Ct);

        var cancel = f.Data.RecordList.Last();
        Assert.Equal(BillingRecordType.Anulacion, cancel.Type);
        Assert.Equal(2, cancel.Sequence);
        Assert.Equal(f.Data.RecordList[0].Hash, cancel.PreviousHash);
        Assert.True((await f.Invoices().GetAsync(issued.InvoiceId, Ct)).Cancelled);
        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().CancelAsync(issued.InvoiceId, "otra", Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().RectifyAsync(new RectifyInvoiceRequest(issued.InvoiceId, "x", [new(1, 1)], Guid.NewGuid()), Ct));
        var again = await f.Invoices().IssueAsync(Simplified(orderId), Ct);
        Assert.Equal("S2026-000002", again.FullNumber);
        Assert.True((await f.Invoices().VerifyChainAsync(Ct)).Ok);
    }

    [Fact]
    public async Task Anular_SinMotivoOConRectificativas_SeRechaza()
    {
        var f = NewFixture();
        var orderId = await PaidOrderAsync(f);
        var issued = await f.Invoices().IssueAsync(Simplified(orderId), Ct);
        var line = f.Data.InvoiceList.Single().Lines.First();

        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().CancelAsync(issued.InvoiceId, " ", Guid.NewGuid(), Ct));
        await f.Invoices().RectifyAsync(new RectifyInvoiceRequest(issued.InvoiceId, "x", [new(line.Id, 1)], Guid.NewGuid()), Ct);
        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().CancelAsync(issued.InvoiceId, "tarde", Guid.NewGuid(), Ct));
    }

    // ---------- Cadena ----------

    [Fact]
    public async Task Cadena_DetectaRegistroAlteradoEliminadoOReordenado()
    {
        var f = NewFixture();
        var a = await PaidOrderAsync(f);
        var b = await PaidOrderAsync(f, openCash: false);
        await f.Invoices().IssueAsync(Simplified(a), Ct);
        await f.Invoices().IssueAsync(Simplified(b), Ct);
        var head = f.Data.HeadList.Single();

        // Alterar un importe (lo que haría alguien con acceso directo a la base de datos).
        var records = f.Data.RecordList;
        typeof(BillingRecord).GetProperty(nameof(BillingRecord.GrandTotal))!.SetValue(records[0], 1.00m);
        var altered = InvoiceService.Verify(records, head);
        Assert.False(altered.Ok);
        Assert.Equal(1, altered.BrokenAtSequence);
        typeof(BillingRecord).GetProperty(nameof(BillingRecord.GrandTotal))!.SetValue(records[0], 7.10m);
        Assert.True(InvoiceService.Verify(records, head).Ok);

        // Eliminar el primero: la numeración deja de empezar en 1.
        Assert.False(InvoiceService.Verify([.. records.Skip(1)], head).Ok);

        // Eliminar el último: la cabeza apunta a un registro que ya no está.
        var truncated = InvoiceService.Verify([.. records.Take(1)], head);
        Assert.False(truncated.Ok);
        Assert.Contains("cabeza", truncated.Problem, StringComparison.Ordinal);

        // Reordenar.
        Assert.False(InvoiceService.Verify([.. records.AsEnumerable().Reverse()], head).Ok);
    }

    [Fact]
    public void Huella_UsaLaCadenaDeEntradaDocumentadaYDaSha256EnMayusculas()
    {
        var input = BillingHash.AltaInput("B12345674", "S2026-000001", "16-11-2026", "F2", 0.84m, 7.10m, string.Empty, "2026-11-16T13:00:00+01:00");

        Assert.Equal(
            "IDEmisorFactura=B12345674&NumSerieFactura=S2026-000001&FechaExpedicionFactura=16-11-2026&TipoFactura=F2&CuotaTotal=0.84&ImporteTotal=7.1&Huella=&FechaHoraHusoGenRegistro=2026-11-16T13:00:00+01:00",
            input);
        var hash = BillingHash.Sha256Hex(input);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToUpperInvariant(), hash);
        Assert.Equal(
            "IDEmisorFacturaAnulada=B12345674&NumSerieFacturaAnulada=S2026-000001&FechaExpedicionFacturaAnulada=16-11-2026&Huella=ABC&FechaHoraHusoGenRegistro=2026-11-16T13:00:00+01:00",
            BillingHash.CancellationInput("B12345674", "S2026-000001", "16-11-2026", "ABC", "2026-11-16T13:00:00+01:00"));
    }

    [Fact]
    public async Task Qr_ApuntaALaUrlConfiguradaConLosDatosDeLaFactura()
    {
        var f = NewFixture();
        var orderId = await PaidOrderAsync(f);
        var issued = await f.Invoices().IssueAsync(Simplified(orderId), Ct);

        var view = await f.Invoices().GetAsync(issued.InvoiceId, Ct);

        Assert.StartsWith(BillingHash.PreProductionQrUrl, view.QrUrl, StringComparison.Ordinal);
        Assert.Contains("nif=B12345674", view.QrUrl, StringComparison.Ordinal);
        Assert.Contains("numserie=S2026-000001", view.QrUrl, StringComparison.Ordinal);
        Assert.Contains("fecha=16-11-2026", view.QrUrl, StringComparison.Ordinal);
        Assert.Contains("importe=7.10", view.QrUrl, StringComparison.Ordinal);
        Assert.Equal(SubmissionStatus.Pending, view.Submission);
    }

    // ---------- Configuración ----------

    [Fact]
    public async Task Configuracion_ValidaNifYExigePermiso()
    {
        var f = new SalesFixture(All);
        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().SaveSettingsAsync("Bar", "12345678A", "Calle 1", 400m, Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().SaveSettingsAsync("", "B12345674", "Calle 1", 400m, Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Invoices().SaveSettingsAsync("Bar", "B12345674", "Calle 1", 0m, Ct));

        await f.Invoices().SaveSettingsAsync(" Bar Nuevo ", "b-1234567-4", "Calle 1", 300m, Ct);

        var settings = await f.Invoices().GetSettingsAsync(Ct);
        Assert.Equal("Bar Nuevo", settings.IssuerName);
        Assert.Equal("B12345674", settings.IssuerNif);
        Assert.Equal(300m, settings.SimplifiedLimit);
        Assert.True(settings.IssuerComplete);
        Assert.Contains(f.Store.Audit, a => a.Action == AuditActions.InvoiceSettingsChanged);

        var limited = new SalesFixture(Permission.IssueInvoices);
        await Assert.ThrowsAsync<ForbiddenException>(() => limited.Invoices().SaveSettingsAsync("Bar", "B12345674", "Calle 1", 400m, Ct));
    }

    // ---------- Informes ----------

    [Fact]
    public async Task Informes_IvaPorTipo_IncluyeRectificativasYExcluyeAnuladas()
    {
        var f = NewFixture();
        var a = await PaidOrderAsync(f);
        var b = await PaidOrderAsync(f, openCash: false);
        var ia = await f.Invoices().IssueAsync(Simplified(a), Ct);
        var ib = await f.Invoices().IssueAsync(Simplified(b), Ct);
        var line = f.Data.InvoiceList.First().Lines.Single(l => l.Description == "Caña");
        await f.Invoices().RectifyAsync(new RectifyInvoiceRequest(ia.InvoiceId, "Devolución", [new(line.Id, 1)], Guid.NewGuid()), Ct);
        await f.Invoices().CancelAsync(ib.InvoiceId, "Duplicada", Guid.NewGuid(), Ct);
        var day = new DateOnly(2026, 11, 16);

        var vat = await f.InvoiceReports().GetVatAsync(day, day, Ct);

        // Vigente: 7,10 € menos una caña de 1,80 € = 5,30 € (tortilla 3,50 al 10 %; caña 1,80 al 21 %).
        Assert.Equal(5.30m, vat.Total);
        Assert.Equal(vat.Total, vat.Base + vat.Tax);
        Assert.Equal(2, vat.Rows.Count);
        Assert.Equal(3.50m, vat.Rows.Single(r => r.TaxRate == 10m).Total);
        Assert.Equal(1.80m, vat.Rows.Single(r => r.TaxRate == 21m).Total);

        var book = await f.InvoiceReports().GetBookAsync(day, day, audited: true, Ct);
        Assert.Contains(book, r => r.Cancelled && r.Total == 0m);
        Assert.Contains(f.Store.Audit, x => x.Action == AuditActions.InvoiceReportExported);
        var csv = InvoiceCsv.Book(book);
        Assert.Contains("Anulada", csv, StringComparison.Ordinal);
        Assert.Contains("1,80", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Informes_SinPermisoOFechasInvertidas_SeRechazan()
    {
        var f = NewFixture();
        var day = new DateOnly(2026, 11, 16);
        await Assert.ThrowsAsync<DomainException>(() => f.InvoiceReports().GetBookAsync(day, day.AddDays(-1), audited: false, Ct));

        var limited = new SalesFixture(Permission.IssueInvoices);
        await Assert.ThrowsAsync<ForbiddenException>(() => limited.InvoiceReports().GetVatAsync(day, day, Ct));
    }

    [Fact]
    public void Csv_ProtegeFormulasEnNombresDeCliente()
    {
        var rows = new List<BookRow> { new("F2026-000001", new DateOnly(2026, 11, 16), "F1", "A58818501", "=HYPERLINK(\"x\")", 21m, 1m, 0.21m, 1.21m, null, false) };

        var csv = InvoiceCsv.Book(rows);

        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv, StringComparison.Ordinal);
    }
}
