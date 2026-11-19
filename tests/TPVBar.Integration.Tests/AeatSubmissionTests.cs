using System.Net;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Invoicing;
using TPVBar.Domain.Invoicing;
using TPVBar.Infrastructure.Billing;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class AeatSubmissionTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly XNamespace Sf = AeatXmlBuilder.NamespaceSf;
    private static readonly XNamespace SfR = "https://www2.agenciatributaria.gob.es/static_files/common/internet/dep/aplicaciones/es/aeat/tike/cont/ws/RespuestaSuministro.xsd";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";

    private static Task<T> Full<TService, T>(IServiceProvider sp, Func<TService, Task<T>> op)
        where TService : notnull => CashHelpers.Full(sp, op);

    // ───────────── Utilidades ─────────────

    private sealed class AdvancingTime : TimeProvider
    {
        private TimeSpan _offset;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;

        public void Advance(TimeSpan by) => _offset += by;
    }

    /// <summary>Pasarela que valida cada cuerpo contra los XSD oficiales y lo guarda; responde «Correcto» a todo.</summary>
    private sealed class ValidatingGateway : IAeatGateway
    {
        public AeatMode Mode => AeatMode.PreProduction;

        public string? ConfigurationProblem => null;

        public List<XElement> Sent { get; } = [];

        public List<string> SchemaErrors { get; } = [];

        public Task<AeatResponse> SubmitAsync(XElement requestBody, CancellationToken cancellationToken)
        {
            SchemaErrors.AddRange(AeatSchemas.Validate(requestBody));
            Sent.Add(requestBody);
            var lines = requestBody.Elements().Where(e => e.Name.LocalName == "RegistroFactura").Select(r =>
            {
                var inner = r.Elements().Single();
                var number = inner.Element(Sf + "IDFactura")!.Elements().Single(e => e.Name.LocalName.StartsWith("NumSerie", StringComparison.Ordinal)).Value;
                return new AeatLineResult(number, inner.Name.LocalName == "RegistroAlta" ? "Alta" : "Anulacion", "Correcto", null, null, null);
            }).ToList();
            return Task.FromResult(new AeatResponse("CSVTEST000001", 1, "Correcto", lines));
        }
    }

    private static async Task<long> PaidOrderAsync(IServiceProvider sp)
    {
        await Full<InvoiceService, bool>(sp, async s =>
        {
            await s.SaveSettingsAsync("Bar de Pruebas S.L.", "B12345674", "Calle Mayor 1, 28001 Madrid", 400m, Ct);
            return true;
        });
        await CashHelpers.EnsureOpenAsync(sp);
        var orderId = await CashHelpers.SentOrderAsync(sp);
        await CashHelpers.PayAsync(sp, orderId, "CASH", 7.10m, 10m);
        return orderId;
    }

    /// <summary>La base de datos de pruebas es compartida: deja la cola sin rechazos de otras pruebas (que la detendrían).</summary>
    private static async Task ClearRejectedAsync(IServiceProvider sp)
    {
        List<long> rejected;
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().Database.ExecuteSqlRawAsync("UPDATE tpv.billing_submissions SET next_attempt_at = NULL WHERE next_attempt_at IS NOT NULL", Ct);
            rejected = await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().BillingSubmissions.AsNoTracking()
                .Where(s => s.Status == SubmissionStatus.Rejected).Select(s => s.RecordSequence).ToListAsync(Ct);
        }

        foreach (var sequence in rejected)
        {
            await Full<BillingSubmissionService, bool>(sp, async s =>
            {
                await s.RequeueAsync(sequence, Ct);
                return true;
            });
        }
    }

    /// <summary>Ejecuta el servicio de envío con una pasarela de prueba, dentro de un ámbito con todos los permisos.</summary>
    private static async Task<SubmissionRunResult> RunAsync(IServiceProvider sp, IAeatGateway gateway, TimeProvider time)
    {
        await using var scope = sp.CreateAsyncScope();
        var s = scope.ServiceProvider;
        s.GetRequiredService<CurrentUserAccessor>().Set(Guid.NewGuid(), SalesHelpers.Full, "pruebas");
        var service = new BillingSubmissionService(
            s.GetRequiredService<IAppData>(),
            s.GetRequiredService<IUnitOfWork>(),
            s.GetRequiredService<IAuditLog>(),
            s.GetRequiredService<ICurrentUser>(),
            gateway,
            time);
        return await service.ProcessPendingAsync(Ct);
    }

    private static XElement ValidAltaBody() =>
        new(
            XNamespace.Get(AeatXmlBuilder.NamespaceLr) + "RegFactuSistemaFacturacion",
            new XElement(XNamespace.Get(AeatXmlBuilder.NamespaceLr) + "Cabecera", new XElement(Sf + "ObligadoEmision", new XElement(Sf + "NombreRazon", "Bar de Pruebas S.L."), new XElement(Sf + "NIF", "B12345674"))),
            new XElement(
                XNamespace.Get(AeatXmlBuilder.NamespaceLr) + "RegistroFactura",
                new XElement(
                    Sf + "RegistroAlta",
                    new XElement(Sf + "IDVersion", "1.0"),
                    new XElement(Sf + "IDFactura", new XElement(Sf + "IDEmisorFactura", "B12345674"), new XElement(Sf + "NumSerieFactura", "S2026-000001"), new XElement(Sf + "FechaExpedicionFactura", "16-11-2026")),
                    new XElement(Sf + "NombreRazonEmisor", "Bar de Pruebas S.L."),
                    new XElement(Sf + "TipoFactura", "F2"),
                    new XElement(Sf + "DescripcionOperacion", "Venta"),
                    new XElement(
                        Sf + "Desglose",
                        new XElement(
                            Sf + "DetalleDesglose",
                            new XElement(Sf + "Impuesto", "01"),
                            new XElement(Sf + "ClaveRegimen", "01"),
                            new XElement(Sf + "CalificacionOperacion", "S1"),
                            new XElement(Sf + "TipoImpositivo", "21"),
                            new XElement(Sf + "BaseImponibleOimporteNoSujeto", "10.00"),
                            new XElement(Sf + "CuotaRepercutida", "2.10"))),
                    new XElement(Sf + "CuotaTotal", "2.1"),
                    new XElement(Sf + "ImporteTotal", "12.1"),
                    new XElement(Sf + "Encadenamiento", new XElement(Sf + "PrimerRegistro", "S")),
                    new XElement(
                        Sf + "SistemaInformatico",
                        new XElement(Sf + "NombreRazon", "Productor"),
                        new XElement(Sf + "NIF", "B12345674"),
                        new XElement(Sf + "NombreSistemaInformatico", "TPVBar"),
                        new XElement(Sf + "IdSistemaInformatico", "TB"),
                        new XElement(Sf + "Version", "1.0.0"),
                        new XElement(Sf + "NumeroInstalacion", "TPVBAR-TEST"),
                        new XElement(Sf + "TipoUsoPosibleSoloVerifactu", "S"),
                        new XElement(Sf + "TipoUsoPosibleMultiOT", "N"),
                        new XElement(Sf + "IndicadorMultiplesOT", "N")),
                    new XElement(Sf + "FechaHoraHusoGenRegistro", "2026-11-16T13:00:00+01:00"),
                    new XElement(Sf + "TipoHuella", "01"),
                    new XElement(Sf + "Huella", new string('A', 64)))));

    private static string SoapResponse(string estadoEnvio, string lines, string csv = "CSV123", int wait = 60) =>
        $"""
        <soapenv:Envelope xmlns:soapenv="{Soap.NamespaceName}"><soapenv:Body>
        <sfR:RespuestaRegFactuSistemaFacturacion xmlns:sfR="{SfR.NamespaceName}" xmlns:sf="{Sf.NamespaceName}">
          <sfR:CSV>{csv}</sfR:CSV>
          <sfR:DatosPresentacion><sf:NIFPresentador>B12345674</sf:NIFPresentador><sf:TimestampPresentacion>2026-11-16T13:00:01+01:00</sf:TimestampPresentacion></sfR:DatosPresentacion>
          <sfR:Cabecera><sf:ObligadoEmision><sf:NombreRazon>Bar de Pruebas S.L.</sf:NombreRazon><sf:NIF>B12345674</sf:NIF></sf:ObligadoEmision></sfR:Cabecera>
          <sfR:TiempoEsperaEnvio>{wait}</sfR:TiempoEsperaEnvio>
          <sfR:EstadoEnvio>{estadoEnvio}</sfR:EstadoEnvio>
          {lines}
        </sfR:RespuestaRegFactuSistemaFacturacion>
        </soapenv:Body></soapenv:Envelope>
        """;

    private static string Line(string number, string operation, string estado, int? code = null, string? description = null, string? duplicate = null) =>
        $"""
        <sfR:RespuestaLinea>
          <sfR:IDFactura><sf:IDEmisorFactura>B12345674</sf:IDEmisorFactura><sf:NumSerieFactura>{number}</sf:NumSerieFactura><sf:FechaExpedicionFactura>16-11-2026</sf:FechaExpedicionFactura></sfR:IDFactura>
          <sfR:Operacion><sf:TipoOperacion>{operation}</sf:TipoOperacion></sfR:Operacion>
          <sfR:EstadoRegistro>{estado}</sfR:EstadoRegistro>
          {(code is null ? "" : $"<sfR:CodigoErrorRegistro>{code}</sfR:CodigoErrorRegistro><sfR:DescripcionErrorRegistro>{description}</sfR:DescripcionErrorRegistro>")}
          {(duplicate is null ? "" : $"<sfR:RegistroDuplicado><sf:IdPeticionRegistroDuplicado>P1</sf:IdPeticionRegistroDuplicado><sf:EstadoRegistroDuplicado>{duplicate}</sf:EstadoRegistroDuplicado></sfR:RegistroDuplicado>")}
        </sfR:RespuestaLinea>
        """;

    private sealed class CapturingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string Body, string? ContentType, string? SoapAction)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!, body, request.Content.Headers.ContentType?.ToString(), request.Headers.TryGetValues("SOAPAction", out var v) ? v.Single() : null));
            return respond(request, body);
        }
    }

    // ───────────── XML contra los XSD oficiales (datos reales de la base de datos) ─────────────

    [Fact]
    public async Task ElXmlDeTodosLosTiposDeRegistro_ValidaContraLosEsquemasOficialesDeLaAeat()
    {
        await using var services = fixture.BuildServices();
        await ClearRejectedAsync(services);
        await Full<BillingSubmissionService, bool>(services, async s =>
        {
            await s.SaveSystemInfoAsync("Productor Software S.L.", "B12345674", "TB", Ct);
            return true;
        });

        var first = await PaidOrderAsync(services);
        var second = await PaidOrderAsync(services);
        var third = await PaidOrderAsync(services);
        var complete = await Full<InvoiceService, IssueInvoiceResult>(services, s => s.IssueAsync(new IssueInvoiceRequest(first, InvoiceKind.Complete, new InvoiceRecipient("Cliente & Hijos <S.L.>", "A58818501", "Calle Falsa 123, Madrid"), Guid.NewGuid()), Ct));
        var simplified = await Full<InvoiceService, IssueInvoiceResult>(services, s => s.IssueAsync(new IssueInvoiceRequest(second, InvoiceKind.Simplified, null, Guid.NewGuid()), Ct));
        var other = await Full<InvoiceService, IssueInvoiceResult>(services, s => s.IssueAsync(new IssueInvoiceRequest(third, InvoiceKind.Simplified, null, Guid.NewGuid()), Ct));
        var lineId = (await Full<InvoiceService, InvoiceView>(services, s => s.GetAsync(complete.InvoiceId, Ct))).Lines[0].Id;
        await Full<InvoiceService, IssueInvoiceResult>(services, s => s.RectifyAsync(new RectifyInvoiceRequest(complete.InvoiceId, "Devolución", [new(lineId, 1)], Guid.NewGuid()), Ct));
        await Full<InvoiceService, bool>(services, async s =>
        {
            await s.CancelAsync(simplified.InvoiceId, "Emitida por error", Guid.NewGuid(), Ct);
            return true;
        });
        Assert.NotEqual(0, other.InvoiceId);

        var gateway = new ValidatingGateway();
        var time = new AdvancingTime();
        SubmissionRunResult run;
        var total = 0;
        do
        {
            run = await RunAsync(services, gateway, time);
            total += run.Attempted;
            time.Advance(TimeSpan.FromMinutes(2));
        }
        while (run.Attempted > 0);

        Assert.True(total >= 5, $"Se esperaban al menos 5 registros enviados y fueron {total}.");
        Assert.Empty(gateway.SchemaErrors);
        var names = gateway.Sent.SelectMany(b => b.Descendants()).Select(e => e.Name.LocalName).ToHashSet();
        Assert.Contains("RegistroAlta", names);
        Assert.Contains("RegistroAnulacion", names);
        Assert.Contains("TipoRectificativa", names);
        Assert.Contains("FacturasRectificadas", names);
        Assert.Contains("Destinatarios", names);
        Assert.Contains("RegistroAnterior", names);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        Assert.Equal(0, await db.BillingSubmissions.AsNoTracking().CountAsync(s => s.Status == SubmissionStatus.Pending, Ct));
        Assert.Equal("CSVTEST000001", (await db.BillingSubmissions.AsNoTracking().OrderByDescending(s => s.RecordSequence).FirstAsync(Ct)).Csv);
    }

    /// <summary>Rechaza todo lo que recibe (para probar la subsanación).</summary>
    private sealed class RejectingGateway : IAeatGateway
    {
        public AeatMode Mode => AeatMode.PreProduction;

        public string? ConfigurationProblem => null;

        public Task<AeatResponse> SubmitAsync(XElement requestBody, CancellationToken cancellationToken)
        {
            var lines = requestBody.Elements().Where(e => e.Name.LocalName == "RegistroFactura").Select(r =>
            {
                var inner = r.Elements().Single();
                var number = inner.Element(Sf + "IDFactura")!.Elements().Single(e => e.Name.LocalName.StartsWith("NumSerie", StringComparison.Ordinal)).Value;
                return new AeatLineResult(number, inner.Name.LocalName == "RegistroAlta" ? "Alta" : "Anulacion", "Incorrecto", 1111, "Rechazo de prueba", null);
            }).ToList();
            return Task.FromResult(new AeatResponse("CSVREJ", 1, "Incorrecto", lines));
        }
    }

    [Fact]
    public async Task LaSubsanacion_GeneraXmlValidoConLosIndicadoresDeSubsanacionYLaCadenaSigueIntegra()
    {
        await using var services = fixture.BuildServices();
        await ClearRejectedAsync(services);
        await Full<BillingSubmissionService, bool>(services, async s =>
        {
            await s.SaveSystemInfoAsync("Productor Software S.L.", "B12345674", "TB", Ct);
            return true;
        });
        var orderId = await PaidOrderAsync(services);
        await Full<InvoiceService, IssueInvoiceResult>(services, s => s.IssueAsync(new IssueInvoiceRequest(orderId, InvoiceKind.Simplified, null, Guid.NewGuid()), Ct));

        // La AEAT (simulada) rechaza todo lo pendiente; se reintentan los anteriores y se subsana el último.
        var time = new AdvancingTime();
        await RunAsync(services, new RejectingGateway(), time);
        time.Advance(TimeSpan.FromMinutes(5));
        List<long> rejected;
        await using (var scope = services.CreateAsyncScope())
        {
            rejected = await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().BillingSubmissions.AsNoTracking()
                .Where(s => s.Status == SubmissionStatus.Rejected).OrderBy(s => s.RecordSequence).Select(s => s.RecordSequence).ToListAsync(Ct);
        }

        Assert.NotEmpty(rejected);
        foreach (var sequence in rejected.SkipLast(1))
        {
            await Full<BillingSubmissionService, bool>(services, async s =>
            {
                await s.RequeueAsync(sequence, Ct);
                return true;
            });
        }

        var lastRejected = rejected[^1];
        var corrected = await Full<BillingSubmissionService, long>(services, s => s.CorrectAsync(lastRejected, Guid.NewGuid(), Ct));

        var gateway = new ValidatingGateway();
        SubmissionRunResult run;
        do
        {
            run = await RunAsync(services, gateway, time);
            time.Advance(TimeSpan.FromMinutes(2));
        }
        while (run.Attempted > 0);

        Assert.Empty(gateway.SchemaErrors);
        var correction = gateway.Sent.SelectMany(b => b.Descendants(Sf + "RegistroAlta")).Single(a => a.Element(Sf + "Subsanacion") is not null);
        Assert.Equal("S", correction.Element(Sf + "Subsanacion")!.Value);
        Assert.Equal("S", correction.Element(Sf + "RechazoPrevio")!.Value);

        await using var verify = services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        Assert.Equal(lastRejected, (await db.BillingRecords.AsNoTracking().SingleAsync(r => r.Sequence == corrected, Ct)).CorrectsSequence);
        Assert.Equal(SubmissionStatus.Superseded, (await db.BillingSubmissions.AsNoTracking().SingleAsync(s => s.RecordSequence == lastRejected, Ct)).Status);
        var check = await Full<InvoiceService, ChainVerification>(services, s => s.VerifyChainAsync(Ct));
        Assert.True(check.Ok, check.Problem);
    }

    [Fact]
    public void ElValidadorRechazaUnXmlQueNoCumpleElEsquema()
    {
        var body = ValidAltaBody();
        Assert.Empty(AeatSchemas.Validate(body));

        body.Descendants(Sf + "TipoFactura").Single().Value = "ZZ";
        body.Descendants(Sf + "Huella").Single().Value = new string('B', 80);

        var errors = AeatSchemas.Validate(body);
        Assert.NotEmpty(errors);
    }

    // ───────────── Pasarela HTTP (con respuestas simuladas, no la AEAT) ─────────────

    [Theory]
    [InlineData(AeatMode.PreProduction, false, "https://prewww1.aeat.es/wlpl/TIKE-CONT/ws/SistemaFacturacion/VerifactuSOAP")]
    [InlineData(AeatMode.PreProduction, true, "https://prewww10.aeat.es/wlpl/TIKE-CONT/ws/SistemaFacturacion/VerifactuSOAP")]
    [InlineData(AeatMode.Production, false, "https://www1.agenciatributaria.gob.es/wlpl/TIKE-CONT/ws/SistemaFacturacion/VerifactuSOAP")]
    [InlineData(AeatMode.Production, true, "https://www10.agenciatributaria.gob.es/wlpl/TIKE-CONT/ws/SistemaFacturacion/VerifactuSOAP")]
    public async Task LaPasarela_EnviaUnSobreSoapAlEndpointDelWsdlOficial(AeatMode mode, bool seal, string expected)
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SoapResponse("Correcto", Line("S2026-000001", "Alta", "Correcto"))) });
        using var gateway = new AeatGateway(new AeatOptions { Mode = mode, UseSealCertificate = seal }, handler);

        var response = await gateway.SubmitAsync(ValidAltaBody(), Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(expected, request.Uri.ToString());
        Assert.Equal("text/xml; charset=utf-8", request.ContentType);
        Assert.Equal("\"\"", request.SoapAction);
        var envelope = XDocument.Parse(request.Body).Root!;
        Assert.Equal(Soap + "Envelope", envelope.Name);
        Assert.NotNull(envelope.Element(Soap + "Body")!.Element(XNamespace.Get(AeatXmlBuilder.NamespaceLr) + "RegFactuSistemaFacturacion"));
        Assert.Equal("CSV123", response.Csv);
        Assert.Equal(60, response.WaitSeconds);
        Assert.Equal("Correcto", response.SendStatus);
        var line = Assert.Single(response.Lines);
        Assert.Equal(("S2026-000001", "Alta", "Correcto"), (line.InvoiceNumber, line.Operation, line.RecordStatus));
    }

    [Fact]
    public async Task LaPasarela_NoEnviaUnXmlInvalido()
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using var gateway = new AeatGateway(new AeatOptions { Mode = AeatMode.PreProduction }, handler);
        var body = ValidAltaBody();
        body.Descendants(Sf + "TipoFactura").Single().Value = "ZZ";

        var ex = await Assert.ThrowsAsync<AeatCommunicationException>(() => gateway.SubmitAsync(body, Ct));

        Assert.Contains("esquema", ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void LaRespuestaConRechazosYDuplicados_SeInterpretaCorrectamenteYValidaContraElXsdDeRespuesta()
    {
        var xml = SoapResponse(
            "ParcialmenteCorrecto",
            Line("S2026-000001", "Alta", "Correcto") +
            Line("S2026-000002", "Alta", "AceptadoConErrores", 2000, "Aviso") +
            Line("S2026-000003", "Anulacion", "Incorrecto", 4102, "Error de prueba") +
            Line("S2026-000004", "Alta", "Incorrecto", 3000, "Duplicado", "Correcta"));

        var doc = XDocument.Parse(xml);
        var schemaErrors = AeatSchemas.Validate(doc.Root!.Element(Soap + "Body")!.Elements().Single());
        Assert.True(schemaErrors.Count == 0, string.Join(" | ", schemaErrors));
        var response = AeatGateway.ParseResponse(xml, HttpStatusCode.OK);

        Assert.Equal("ParcialmenteCorrecto", response.SendStatus);
        Assert.Equal(4, response.Lines.Count);
        Assert.Equal("AceptadoConErrores", response.Lines[1].RecordStatus);
        Assert.Equal((4102, "Error de prueba", "Anulacion"), (response.Lines[2].ErrorCode, response.Lines[2].ErrorDescription, response.Lines[2].Operation));
        Assert.Equal("Correcta", response.Lines[3].DuplicateStatus);
    }

    [Fact]
    public async Task LosFallosSoapHttpYRespuestasIlegibles_SeTratanComoFalloDeComunicacion()
    {
        const string fault = $"""<soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"><soapenv:Body><soapenv:Fault><faultcode>soapenv:Client</faultcode><faultstring>Codigo[4104].Certificado no válido</faultstring></soapenv:Fault></soapenv:Body></soapenv:Envelope>""";
        Assert.Contains("4104", Assert.Throws<AeatCommunicationException>(() => AeatGateway.ParseResponse(fault, HttpStatusCode.InternalServerError)).Message, StringComparison.Ordinal);
        Assert.Throws<AeatCommunicationException>(() => AeatGateway.ParseResponse("<html>Bad gateway</html>", HttpStatusCode.BadGateway));
        Assert.Throws<AeatCommunicationException>(() => AeatGateway.ParseResponse("no es xml", HttpStatusCode.OK));

        // Una respuesta con DTD (entidades) se rechaza: no se procesa nunca.
        Assert.Throws<AeatCommunicationException>(() => AeatGateway.ParseResponse("<!DOCTYPE x [<!ENTITY a \"aaaa\">]><x>&a;</x>", HttpStatusCode.OK));

        var failing = new CapturingHandler((_, _) => throw new HttpRequestException("sin red"));
        using var gateway = new AeatGateway(new AeatOptions { Mode = AeatMode.PreProduction }, failing);
        var ex = await Assert.ThrowsAsync<AeatCommunicationException>(() => gateway.SubmitAsync(ValidAltaBody(), Ct));
        Assert.Contains("sin red", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaConfiguracion_PorDefectoEstaDesactivadaYSinCertificadoNoSePuedeActivar()
    {
        using var disabled = new AeatGateway(new AeatOptions());
        Assert.Equal(AeatMode.Disabled, disabled.Mode);
        Assert.Null(disabled.ConfigurationProblem);
        await Assert.ThrowsAsync<AeatCommunicationException>(() => disabled.SubmitAsync(ValidAltaBody(), Ct));

        using var noCertificate = new AeatGateway(new AeatOptions { Mode = AeatMode.PreProduction });
        Assert.Contains("certificado", noCertificate.ConfigurationProblem, StringComparison.OrdinalIgnoreCase);

        using var missingFile = new AeatGateway(new AeatOptions { Mode = AeatMode.Production, CertificatePfxPath = Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N") + ".pfx") });
        Assert.Contains("archivo", missingFile.ConfigurationProblem, StringComparison.OrdinalIgnoreCase);

        using var unknownThumbprint = new AeatGateway(new AeatOptions { Mode = AeatMode.PreProduction, CertificateThumbprint = new string('A', 40), CertificateStore = "CurrentUser" });
        Assert.Contains("almacén", unknownThumbprint.ConfigurationProblem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaAplicacionWeb_ArrancaConElEnvioDesactivadoPorDefecto()
    {
        var gateway = fixture.Web.Services.GetRequiredService<IAeatGateway>();
        Assert.Equal(AeatMode.Disabled, gateway.Mode);

        await using var services = fixture.BuildServices();
        var overview = await Full<BillingSubmissionService, SubmissionOverview>(services, s => s.GetOverviewAsync(Ct));
        Assert.Equal(AeatMode.Disabled, overview.Mode);
        var run = await Full<BillingSubmissionService, SubmissionRunResult>(services, s => s.SendNowAsync(Ct));
        Assert.Equal(0, run.Attempted);
        Assert.Contains("desactivado", run.Skipped, StringComparison.Ordinal);
    }
}
