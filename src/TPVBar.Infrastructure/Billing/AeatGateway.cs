using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TPVBar.Application.Features.Invoicing;

namespace TPVBar.Infrastructure.Billing;

/// <summary>
/// Configuración del envío (sección <c>Billing:Aeat</c>). Por defecto <b>desactivado</b>. El certificado se toma del almacén
/// de Windows (huella) o de un archivo PFX; la contraseña del PFX va en la configuración protegida del servidor, nunca en el repositorio.
/// </summary>
public sealed class AeatOptions
{
    public const string SectionName = "Billing:Aeat";

    public AeatMode Mode { get; set; } = AeatMode.Disabled;

    /// <summary>Usar el puerto de certificado de sello (www10/prewww10) en lugar del de certificado personal o de representante (www1/prewww1).</summary>
    public bool UseSealCertificate { get; set; }

    public string? CertificateThumbprint { get; set; }

    /// <summary>LocalMachine (por defecto, para el servicio de Windows) o CurrentUser.</summary>
    public string CertificateStore { get; set; } = "LocalMachine";

    public string? CertificatePfxPath { get; set; }

    public string? CertificatePfxPassword { get; set; }

    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Cliente SOAP del servicio <c>RegFactuSistemaFacturacion</c> (VERI*FACTU) de la AEAT, con autenticación por certificado de cliente.
/// <para>
/// Las URLs salen del WSDL oficial. Antes de enviar, el XML se valida contra los XSD oficiales. <b>No se ha probado contra la AEAT</b>
/// (hace falta un certificado real): solo contra respuestas simuladas con la estructura del esquema oficial.
/// </para>
/// </summary>
public sealed class AeatGateway : IAeatGateway, IDisposable
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Sf = AeatXmlBuilder.NamespaceSf;
    private static readonly XNamespace SfR = "https://www2.agenciatributaria.gob.es/static_files/common/internet/dep/aplicaciones/es/aeat/tike/cont/ws/RespuestaSuministro.xsd";

    private readonly AeatOptions _options;
    private readonly HttpClient? _client;
    private readonly X509Certificate2? _certificate;
    private readonly string? _problem;

    /// <param name="options">Configuración.</param>
    /// <param name="handler">Manejador HTTP (solo para pruebas). Si es <c>null</c> se crea uno con el certificado de cliente.</param>
    public AeatGateway(AeatOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        if (options.Mode == AeatMode.Disabled)
        {
            return;
        }

        try
        {
            if (handler is null)
            {
                _certificate = LoadCertificate(options);
                var sockets = new SocketsHttpHandler();
                sockets.SslOptions.ClientCertificates = [_certificate];
                handler = sockets;
            }

            _client = new HttpClient(handler, disposeHandler: handler is SocketsHttpHandler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 300)) };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            _problem = ex.Message;
        }
    }

    public AeatMode Mode => _options.Mode;

    public string? ConfigurationProblem => _options.Mode == AeatMode.Disabled ? null : _problem;

    /// <summary>URL del servicio según el modo y el tipo de certificado (WSDL oficial: VerifactuSOAP).</summary>
    public static Uri Endpoint(AeatMode mode, bool seal) => (mode, seal) switch
    {
        (AeatMode.Production, false) => new Uri("https://www1.agenciatributaria.gob.es/wlpl/TIKE-CONT/ws/SistemaFacturacion/VerifactuSOAP"),
        (AeatMode.Production, true) => new Uri("https://www10.agenciatributaria.gob.es/wlpl/TIKE-CONT/ws/SistemaFacturacion/VerifactuSOAP"),
        (AeatMode.PreProduction, false) => new Uri("https://prewww1.aeat.es/wlpl/TIKE-CONT/ws/SistemaFacturacion/VerifactuSOAP"),
        (AeatMode.PreProduction, true) => new Uri("https://prewww10.aeat.es/wlpl/TIKE-CONT/ws/SistemaFacturacion/VerifactuSOAP"),
        _ => throw new InvalidOperationException("El envío a la AEAT está desactivado."),
    };

    public async Task<AeatResponse> SubmitAsync(XElement requestBody, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestBody);
        if (_client is null)
        {
            throw new AeatCommunicationException(ConfigurationProblem ?? "El envío a la AEAT está desactivado.");
        }

        var errors = AeatSchemas.Validate(requestBody);
        if (errors.Count > 0)
        {
            throw new AeatCommunicationException("El XML generado no cumple el esquema oficial y no se envía: " + string.Join(" | ", errors.Take(3)));
        }

        var envelope = new XElement(Soap + "Envelope", new XAttribute(XNamespace.Xmlns + "soapenv", Soap.NamespaceName), new XElement(Soap + "Header"), new XElement(Soap + "Body", requestBody));
        using var content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false), "text/xml");
        content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(_options.Mode, _options.UseSealCertificate)) { Content = content };
        request.Headers.TryAddWithoutValidation("SOAPAction", "\"\"");

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new AeatCommunicationException(ex.Message, ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseResponse(text, response.StatusCode);
        }
    }

    /// <summary>Interpreta la respuesta SOAP. Un Fault o una respuesta ilegible se notifican como fallo de comunicación.</summary>
    public static AeatResponse ParseResponse(string xml, HttpStatusCode status)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new AeatCommunicationException($"Respuesta no válida de la AEAT (HTTP {(int)status}).", ex);
        }

        var body = doc.Root?.Element(Soap + "Body");
        var fault = body?.Element(Soap + "Fault");
        if (fault is not null)
        {
            var code = fault.Element("faultcode")?.Value ?? "?";
            var message = fault.Element("faultstring")?.Value ?? "sin descripción";
            throw new AeatCommunicationException($"La AEAT devolvió un error SOAP ({code}): {message}");
        }

        var root = body?.Element(SfR + "RespuestaRegFactuSistemaFacturacion");
        if (root is null)
        {
            throw new AeatCommunicationException($"La respuesta de la AEAT no tiene el formato esperado (HTTP {(int)status}).");
        }

        var wait = int.TryParse(root.Element(SfR + "TiempoEsperaEnvio")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
        var lines = root.Elements(SfR + "RespuestaLinea").Select(l => new AeatLineResult(
            l.Element(SfR + "IDFactura")?.Element(Sf + "NumSerieFactura")?.Value ?? string.Empty,
            l.Element(SfR + "Operacion")?.Element(Sf + "TipoOperacion")?.Value ?? string.Empty,
            l.Element(SfR + "EstadoRegistro")?.Value ?? string.Empty,
            int.TryParse(l.Element(SfR + "CodigoErrorRegistro")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null,
            l.Element(SfR + "DescripcionErrorRegistro")?.Value,
            l.Element(SfR + "RegistroDuplicado")?.Element(Sf + "EstadoRegistroDuplicado")?.Value)).ToList();

        return new AeatResponse(root.Element(SfR + "CSV")?.Value, wait, root.Element(SfR + "EstadoEnvio")?.Value ?? string.Empty, lines);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _certificate?.Dispose();
    }

    private static X509Certificate2 LoadCertificate(AeatOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CertificatePfxPath))
        {
            if (!File.Exists(options.CertificatePfxPath))
            {
                throw new InvalidOperationException("No se encuentra el archivo del certificado (Billing:Aeat:CertificatePfxPath).");
            }

            return X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePfxPath, options.CertificatePfxPassword);
        }

        if (string.IsNullOrWhiteSpace(options.CertificateThumbprint))
        {
            throw new InvalidOperationException("Falta el certificado: configure Billing:Aeat:CertificateThumbprint o Billing:Aeat:CertificatePfxPath.");
        }

        var location = string.Equals(options.CertificateStore, "CurrentUser", StringComparison.OrdinalIgnoreCase) ? StoreLocation.CurrentUser : StoreLocation.LocalMachine;
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly);
        var thumbprint = new string(options.CertificateThumbprint.Where(char.IsAsciiHexDigit).ToArray());
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        if (found.Count == 0)
        {
            throw new InvalidOperationException("El certificado indicado no está en el almacén de Windows.");
        }

        var cert = found[0];
        if (!cert.HasPrivateKey)
        {
            throw new InvalidOperationException("El certificado no tiene clave privada accesible para el servicio.");
        }

        if (cert.NotAfter < DateTime.Now)
        {
            throw new InvalidOperationException($"El certificado caducó el {cert.NotAfter:dd/MM/yyyy}.");
        }

        return cert;
    }
}
