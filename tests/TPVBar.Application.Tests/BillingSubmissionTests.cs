using TPVBar.Application.Common;
using TPVBar.Application.Features.Cash;
using TPVBar.Application.Features.Invoicing;
using TPVBar.Application.Tests.Fakes;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Invoicing;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Tests;

public class BillingSubmissionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Permission[] All =
    [
        Permission.TakeOrders, Permission.TakePayments, Permission.OpenCloseCash, Permission.IssueInvoices, Permission.RectifyInvoices,
        Permission.ManageConfiguration, Permission.ViewReports,
    ];

    private static async Task<SalesFixture> ReadyAsync(bool systemInfo = true)
    {
        var f = new SalesFixture(All).WithPrinters().WithPaymentMethods().WithIssuer();
        if (systemInfo)
        {
            await f.Submissions().SaveSystemInfoAsync("Productor Software S.L.", "b12345674", "tb", Ct);
        }

        await f.Cash().OpenAsync(100m, Ct);
        return f;
    }

    private static async Task<IssueInvoiceResult> InvoiceAsync(SalesFixture f)
    {
        var orderId = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 2, null, Guid.NewGuid(), Ct);
        await f.Orders().AddLineAsync(orderId, f.Tortilla.Id, 1, null, Guid.NewGuid(), Ct);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        await f.Payments().PayAsync(new PayRequest(orderId, f.CashMethod.Id, 7.10m, 10m, null, Guid.NewGuid()), Ct);
        return await f.Invoices().IssueAsync(new IssueInvoiceRequest(orderId, InvoiceKind.Simplified, null, Guid.NewGuid()), Ct);
    }

    private static string[] SentNumbers(System.Xml.Linq.XElement body) =>
        [.. body.Elements().Where(e => e.Name.LocalName == "RegistroFactura").Select(r => r.Descendants().First(e => e.Name.LocalName is "NumSerieFactura" or "NumSerieFacturaAnulada").Value)];

    [Fact]
    public async Task Enviar_Desactivado_ConProblemaDeConfiguracionOSinDatosDelProductor_NoEnviaNada()
    {
        var f = await ReadyAsync();
        await InvoiceAsync(f);

        f.Gateway.Mode = AeatMode.Disabled;
        Assert.Contains("desactivado", (await f.Submissions().ProcessPendingAsync(Ct)).Skipped, StringComparison.Ordinal);

        f.Gateway.Mode = AeatMode.PreProduction;
        f.Gateway.ConfigurationProblem = "Falta el certificado";
        Assert.Equal("Falta el certificado", (await f.Submissions().ProcessPendingAsync(Ct)).Skipped);

        var bare = await ReadyAsync(systemInfo: false);
        await InvoiceAsync(bare);
        Assert.Contains("productor", (await bare.Submissions().ProcessPendingAsync(Ct)).Skipped, StringComparison.Ordinal);

        Assert.Empty(f.Gateway.Sent);
        Assert.Empty(bare.Gateway.Sent);
        Assert.All(f.Data.SubmissionList, s => Assert.Equal(SubmissionStatus.Pending, s.Status));
    }

    [Fact]
    public async Task Enviar_LoteEnOrdenDeCadena_EncadenaConElRegistroAnteriorYMarcaAceptados()
    {
        var f = await ReadyAsync();
        await InvoiceAsync(f);
        await InvoiceAsync(f);

        var result = await f.Submissions().ProcessPendingAsync(Ct);

        Assert.Equal(new SubmissionRunResult(2, 2, 0, 0, 0, null), result);
        var body = Assert.Single(f.Gateway.Sent);
        Assert.Equal(["S2026-000001", "S2026-000002"], SentNumbers(body));
        var records = body.Elements().Where(e => e.Name.LocalName == "RegistroFactura").Select(r => r.Elements().Single()).ToList();
        Assert.Contains(records[0].Descendants(), e => e.Name.LocalName == "PrimerRegistro");
        var previous = records[1].Descendants().Single(e => e.Name.LocalName == "RegistroAnterior");
        Assert.Equal(f.Data.RecordList[0].Hash, previous.Elements().Single(e => e.Name.LocalName == "Huella").Value);
        Assert.Equal(f.Data.RecordList[1].Hash, records[1].Elements().Single(e => e.Name.LocalName == "Huella").Value);
        Assert.All(f.Data.SubmissionList, s =>
        {
            Assert.Equal(SubmissionStatus.Accepted, s.Status);
            Assert.Equal("CSV1234567890", s.Csv);
            Assert.Equal(1, s.Attempts);
        });
        Assert.Contains(f.Store.Audit, a => a.Action == AuditActions.BillingSubmitted && a.UserId is null);
    }

    [Fact]
    public async Task Enviar_RespetaLaEsperaDeLaAeatYNoReenviaLoYaAceptado()
    {
        var f = await ReadyAsync();
        await InvoiceAsync(f);
        await f.Submissions().ProcessPendingAsync(Ct);
        await InvoiceAsync(f);

        var tooSoon = await f.Submissions().ProcessPendingAsync(Ct);
        Assert.NotNull(tooSoon.Skipped);
        Assert.Single(f.Gateway.Sent);

        f.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, (await f.Submissions().ProcessPendingAsync(Ct)).Accepted);
        Assert.Equal(2, f.Gateway.Sent.Count);
        Assert.Equal(["S2026-000002"], SentNumbers(f.Gateway.Sent[1]));

        f.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, (await f.Submissions().ProcessPendingAsync(Ct)).Attempted);
        Assert.Equal(2, f.Gateway.Sent.Count);
    }

    [Fact]
    public async Task Enviar_FalloDeComunicacion_DejaPendienteConMensajeYReintentaConRetroceso()
    {
        var f = await ReadyAsync();
        await InvoiceAsync(f);
        f.Gateway.FailWith = new AeatCommunicationException("sin conexión");

        var failed = await f.Submissions().ProcessPendingAsync(Ct);

        Assert.Equal(1, failed.Failed);
        var submission = f.Data.SubmissionList.Single();
        Assert.Equal(SubmissionStatus.Pending, submission.Status);
        Assert.Contains("sin conexión", submission.Message, StringComparison.Ordinal);
        Assert.Equal(1, submission.Attempts);
        Assert.Contains(f.Store.Audit, a => a.Action == AuditActions.BillingSubmissionFailed);

        Assert.NotNull((await f.Submissions().ProcessPendingAsync(Ct)).Skipped); // aún en retroceso
        f.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, (await f.Submissions().ProcessPendingAsync(Ct)).Accepted);
        Assert.Equal(SubmissionStatus.Accepted, submission.Status);
        Assert.Equal(2, submission.Attempts);
    }

    [Fact]
    public async Task Enviar_RechazoDeLaAeat_DetieneLaColaHastaReintentarManualmente()
    {
        var f = await ReadyAsync();
        await InvoiceAsync(f);
        f.Gateway.Responder = body => FakeAeatGateway.Respond(body, _ => ("Incorrecto", 4102, "El NIF del emisor no está censado", null), "CSVX", 60);

        var rejected = await f.Submissions().ProcessPendingAsync(Ct);

        Assert.Equal(1, rejected.Rejected);
        var submission = f.Data.SubmissionList.Single();
        Assert.Equal(SubmissionStatus.Rejected, submission.Status);
        Assert.Equal(4102, submission.ErrorCode);
        Assert.Contains("censado", submission.Message, StringComparison.Ordinal);

        await InvoiceAsync(f);
        f.Time.Advance(TimeSpan.FromMinutes(5));
        var blocked = await f.Submissions().ProcessPendingAsync(Ct);
        Assert.Contains("detenida", blocked.Skipped, StringComparison.Ordinal);
        Assert.Single(f.Gateway.Sent);

        f.Gateway.Responder = null;
        await f.Submissions().RequeueAsync(1, Ct);
        Assert.Equal(2, (await f.Submissions().ProcessPendingAsync(Ct)).Accepted);
        Assert.Contains(f.Store.Audit, a => a.Action == AuditActions.BillingSubmissionRequeued);
    }

    [Fact]
    public async Task Enviar_AceptadoConErroresYDuplicadoYaAceptado_SeInterpretanBien()
    {
        var f = await ReadyAsync();
        await InvoiceAsync(f);
        await InvoiceAsync(f);
        f.Gateway.Responder = body => FakeAeatGateway.Respond(
            body,
            k => k.Number.EndsWith("000001", StringComparison.Ordinal) ? ("AceptadoConErrores", 2000, "Aviso no bloqueante", null) : ("Incorrecto", 3000, "Registro duplicado", "Correcta"),
            "CSVY",
            60);

        var result = await f.Submissions().ProcessPendingAsync(Ct);

        Assert.Equal(1, result.AcceptedWithErrors);
        Assert.Equal(1, result.Accepted);
        Assert.Equal(SubmissionStatus.AcceptedWithErrors, f.Data.SubmissionList[0].Status);
        Assert.Equal(SubmissionStatus.Accepted, f.Data.SubmissionList[1].Status);
        Assert.Contains("constaba", f.Data.SubmissionList[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enviar_AltaYAnulacionDeLaMismaFactura_SeCasanPorOperacion()
    {
        var f = await ReadyAsync();
        var issued = await InvoiceAsync(f);
        await f.Invoices().CancelAsync(issued.InvoiceId, "Emitida por error", Guid.NewGuid(), Ct);
        f.Gateway.Responder = body => FakeAeatGateway.Respond(body, k => k.Operation == "Alta" ? ("Correcto", null, null, null) : ("Incorrecto", 1234, "Anulación inválida", null), "CSVZ", 60);

        var result = await f.Submissions().ProcessPendingAsync(Ct);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(1, result.Rejected);
        Assert.Equal(SubmissionStatus.Accepted, f.Data.SubmissionList[0].Status);
        Assert.Equal(SubmissionStatus.Rejected, f.Data.SubmissionList[1].Status);
        var records = f.Gateway.Sent.Single().Elements().Where(e => e.Name.LocalName == "RegistroFactura").Select(r => r.Elements().Single().Name.LocalName).ToList();
        Assert.Equal(["RegistroAlta", "RegistroAnulacion"], records);
    }

    [Fact]
    public async Task Enviar_DosProcesosAlaVez_SoloUnoEnvia()
    {
        var f = await ReadyAsync();
        await InvoiceAsync(f);
        var gate = new TaskCompletionSource();
        f.Gateway.Hold = gate.Task;
        var shared = new BillingSubmissionLock();

        var first = f.Submissions(shared).ProcessPendingAsync(Ct); // se queda esperando la respuesta
        var second = await f.Submissions(shared).ProcessPendingAsync(Ct);
        gate.SetResult();
        var firstResult = await first;

        Assert.Contains("en curso", second.Skipped, StringComparison.Ordinal);
        Assert.Equal(1, firstResult.Accepted);
        Assert.Single(f.Gateway.Sent);
        Assert.Equal(1, f.Data.SubmissionList.Single().Attempts);
    }

    private static string? Child(System.Xml.Linq.XElement record, string name) =>
        record.Elements().Single().Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static async Task RejectAllAsync(SalesFixture f, string status = "Incorrecto")
    {
        f.Gateway.Responder = body => FakeAeatGateway.Respond(body, _ => (status, 1111, "Error de prueba", null), "CSV", 1);
        await f.Submissions().ProcessPendingAsync(Ct);
        f.Gateway.Responder = null;
        f.Time.Advance(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Subsanar_AltaRechazada_GeneraRegistroNuevoMarcadoYDesbloqueaLaCola()
    {
        var f = await ReadyAsync();
        await InvoiceAsync(f);
        await RejectAllAsync(f);
        var rejected = f.Data.RecordList.Single();

        var newSequence = await f.Submissions().CorrectAsync(1, Guid.NewGuid(), Ct);

        Assert.Equal(2, newSequence);
        var corrected = f.Data.RecordList[1];
        Assert.Equal(rejected.Hash, corrected.PreviousHash);
        Assert.NotEqual(rejected.Hash, corrected.Hash);
        Assert.Equal(corrected.Hash, corrected.RecomputeHash());
        Assert.Equal((1L, true), (corrected.CorrectsSequence, corrected.RejectedBefore));
        Assert.Equal(rejected.InvoiceId, corrected.InvoiceId);
        Assert.Equal(SubmissionStatus.Superseded, f.Data.SubmissionList[0].Status);
        Assert.Contains("Subsanado con el registro 2", f.Data.SubmissionList[0].Message, StringComparison.Ordinal);
        Assert.Contains(f.Store.Audit, a => a.Action == AuditActions.BillingRecordCorrected);

        var run = await f.Submissions().ProcessPendingAsync(Ct);

        Assert.Equal(1, run.Accepted);
        var sent = f.Gateway.Sent.Last().Elements().Single(e => e.Name.LocalName == "RegistroFactura");
        Assert.Equal("S", Child(sent, "Subsanacion"));
        Assert.Equal("S", Child(sent, "RechazoPrevio"));
        Assert.Equal(corrected.Hash, Child(sent, "Huella"));
        Assert.Equal(SubmissionStatus.Accepted, f.Data.SubmissionList[1].Status);
        Assert.True((await f.Invoices().VerifyChainAsync(Ct)).Ok);
    }

    [Fact]
    public async Task Subsanar_ConRegistrosPosterioresPendientes_LosEnviaEnOrdenDeCadena()
    {
        var f = await ReadyAsync();
        await InvoiceAsync(f);
        await InvoiceAsync(f);
        f.Gateway.Responder = body => FakeAeatGateway.Respond(body, k => k.Number.EndsWith("000001", StringComparison.Ordinal) ? ("Incorrecto", 1, "x", null) : ("Correcto", null, null, null), "CSV", 1);
        await f.Submissions().ProcessPendingAsync(Ct); // el 1 se rechaza; el 2 va en el mismo lote y la AEAT lo acepta
        f.Gateway.Responder = null;
        f.Time.Advance(TimeSpan.FromMinutes(5));

        await f.Submissions().CorrectAsync(1, Guid.NewGuid(), Ct);
        var run = await f.Submissions().ProcessPendingAsync(Ct);

        Assert.Equal(1, run.Accepted);
        Assert.Equal(["S2026-000001"], SentNumbers(f.Gateway.Sent.Last()));
        Assert.Equal(3, f.Data.RecordList.Count);
        Assert.True((await f.Invoices().VerifyChainAsync(Ct)).Ok);
    }

    [Fact]
    public async Task Subsanar_AltaAceptadaConErrores_UsaSubsanacionSYRechazoPrevioN()
    {
        var f = await ReadyAsync();
        await InvoiceAsync(f);
        await RejectAllAsync(f, "AceptadoConErrores");

        await f.Submissions().CorrectAsync(1, Guid.NewGuid(), Ct);
        await f.Submissions().ProcessPendingAsync(Ct);

        var sent = f.Gateway.Sent.Last().Elements().Single(e => e.Name.LocalName == "RegistroFactura");
        Assert.Equal("S", Child(sent, "Subsanacion"));
        Assert.Equal("N", Child(sent, "RechazoPrevio"));
        Assert.False(f.Data.RecordList[1].RejectedBefore);
    }

    [Fact]
    public async Task Subsanar_AnulacionRechazada_UsaRechazoPrevioSinSubsanacion()
    {
        var f = await ReadyAsync();
        var issued = await InvoiceAsync(f);
        await f.Invoices().CancelAsync(issued.InvoiceId, "Emitida por error", Guid.NewGuid(), Ct);
        f.Gateway.Responder = body => FakeAeatGateway.Respond(body, k => k.Operation == "Alta" ? ("Correcto", null, null, null) : ("Incorrecto", 9, "x", null), "CSV", 1);
        await f.Submissions().ProcessPendingAsync(Ct);
        f.Gateway.Responder = null;
        f.Time.Advance(TimeSpan.FromMinutes(5));

        await f.Submissions().CorrectAsync(2, Guid.NewGuid(), Ct);
        await f.Submissions().ProcessPendingAsync(Ct);

        var sent = f.Gateway.Sent.Last().Elements().Single(e => e.Name.LocalName == "RegistroFactura");
        Assert.Equal("RegistroAnulacion", sent.Elements().Single().Name.LocalName);
        Assert.Equal("S", Child(sent, "RechazoPrevio"));
        Assert.Null(Child(sent, "Subsanacion"));
        Assert.Equal(BillingRecordType.Anulacion, f.Data.RecordList[2].Type);
        Assert.True((await f.Invoices().VerifyChainAsync(Ct)).Ok);
    }

    [Fact]
    public async Task Subsanar_ReglasYPermisos()
    {
        var f = await ReadyAsync();
        await InvoiceAsync(f);

        await Assert.ThrowsAsync<DomainException>(() => f.Submissions().CorrectAsync(1, Guid.NewGuid(), Ct)); // pendiente
        await Assert.ThrowsAsync<NotFoundException>(() => f.Submissions().CorrectAsync(99, Guid.NewGuid(), Ct));

        await RejectAllAsync(f);
        var key = Guid.NewGuid();
        Assert.Equal(2, await f.Submissions().CorrectAsync(1, key, Ct));
        Assert.Equal(0, await f.Submissions().CorrectAsync(1, key, Ct)); // doble clic: misma clave
        Assert.Equal(2, f.Data.RecordList.Count);
        await Assert.ThrowsAsync<DomainException>(() => f.Submissions().CorrectAsync(1, Guid.NewGuid(), Ct)); // ya sustituido

        var limited = new SalesFixture(Permission.IssueInvoices);
        await Assert.ThrowsAsync<ForbiddenException>(() => limited.Submissions().CorrectAsync(1, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Subsanar_AnulacionAceptadaConErrores_NoSePuede()
    {
        var f = await ReadyAsync();
        var issued = await InvoiceAsync(f);
        await f.Invoices().CancelAsync(issued.InvoiceId, "Emitida por error", Guid.NewGuid(), Ct);
        f.Gateway.Responder = body => FakeAeatGateway.Respond(body, k => k.Operation == "Alta" ? ("Correcto", null, null, null) : ("AceptadoConErrores", 9, "x", null), "CSV", 1);
        await f.Submissions().ProcessPendingAsync(Ct);

        await Assert.ThrowsAsync<DomainException>(() => f.Submissions().CorrectAsync(2, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Enviar_RespuestaSinElRegistro_LoDejaPendiente()
    {
        var f = await ReadyAsync();
        await InvoiceAsync(f);
        f.Gateway.Responder = _ => new AeatResponse("CSV", 60, "Incorrecto", []);

        var result = await f.Submissions().ProcessPendingAsync(Ct);

        Assert.Equal(1, result.Failed);
        Assert.Equal(SubmissionStatus.Pending, f.Data.SubmissionList.Single().Status);
    }

    [Fact]
    public async Task DatosDelSistema_SeValidanSeGuardanUnaVezYExigenPermiso()
    {
        var f = new SalesFixture(All);
        await Assert.ThrowsAsync<DomainException>(() => f.Submissions().SaveSystemInfoAsync("", "B12345674", "TB", Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Submissions().SaveSystemInfoAsync("X", "12345678A", "TB", Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Submissions().SaveSystemInfoAsync("X", "B12345674", "ABC", Ct));

        await f.Submissions().SaveSystemInfoAsync("Productor S.L.", "B12345674", "tb", Ct);
        var first = await f.Submissions().GetSystemInfoAsync(Ct);
        await f.Submissions().SaveSystemInfoAsync("Productor S.L.", "B12345674", "TB", Ct);
        var second = await f.Submissions().GetSystemInfoAsync(Ct);

        Assert.True(first.Complete);
        Assert.Equal("TB", first.SystemId);
        Assert.StartsWith("TPVBAR-", first.InstallationNumber, StringComparison.Ordinal);
        Assert.Equal(first.InstallationNumber, second.InstallationNumber);

        var limited = new SalesFixture(Permission.IssueInvoices);
        await Assert.ThrowsAsync<ForbiddenException>(() => limited.Submissions().SaveSystemInfoAsync("X", "B12345674", "TB", Ct));
        await Assert.ThrowsAsync<ForbiddenException>(() => limited.Submissions().SendNowAsync(Ct));
        await Assert.ThrowsAsync<ForbiddenException>(() => limited.Submissions().RequeueAsync(1, Ct));
        Assert.NotNull(await limited.Submissions().GetOverviewAsync(Ct));
    }

    [Fact]
    public async Task Estado_CuentaPendientesAceptadosYRechazados()
    {
        var f = await ReadyAsync();
        await InvoiceAsync(f);
        await InvoiceAsync(f);

        var before = await f.Submissions().GetOverviewAsync(Ct);
        Assert.Equal(2, before.Pending);
        await f.Submissions().SendNowAsync(Ct);
        var after = await f.Submissions().GetOverviewAsync(Ct);

        Assert.Equal(0, after.Pending);
        Assert.Equal(2, after.Accepted);
        Assert.True(after.SystemInfoComplete);
        Assert.Equal(AeatMode.PreProduction, after.Mode);
        Assert.Empty(await f.Submissions().ListAsync(onlyAttention: true, Ct));
        Assert.Equal(2, (await f.Submissions().ListAsync(onlyAttention: false, Ct)).Count);
    }
}
