namespace TPVBar.Application.Features.Invoicing;

/// <summary>Modo de envío a la AEAT. Se fija en la configuración del servidor, nunca desde la interfaz.</summary>
public enum AeatMode
{
    /// <summary>No se envía nada (valor por defecto).</summary>
    Disabled = 0,

    /// <summary>Entorno de pruebas de la AEAT (prewww1/prewww10): los envíos no tienen efecto fiscal.</summary>
    PreProduction = 1,

    /// <summary>Entorno real: los envíos SÍ tienen efecto fiscal.</summary>
    Production = 2,
}

/// <summary>Resultado de un registro dentro de la respuesta de la AEAT.</summary>
public sealed record AeatLineResult(
    string InvoiceNumber,
    string Operation,
    string RecordStatus,
    int? ErrorCode,
    string? ErrorDescription,
    string? DuplicateStatus);

/// <summary>Respuesta de la AEAT a un envío.</summary>
public sealed record AeatResponse(string? Csv, int WaitSeconds, string SendStatus, IReadOnlyList<AeatLineResult> Lines);

/// <summary>Fallo de comunicación o respuesta de servicio (red, tiempo agotado, HTTP 5xx, SOAP Fault, XML no válido): se reintenta más tarde.</summary>
public sealed class AeatCommunicationException : Exception
{
    public AeatCommunicationException()
    {
    }

    public AeatCommunicationException(string message)
        : base(message)
    {
    }

    public AeatCommunicationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Pasarela con la AEAT (implementada en Infraestructura). El cuerpo es el elemento RegFactuSistemaFacturacion.</summary>
public interface IAeatGateway
{
    AeatMode Mode { get; }

    /// <summary>Descripción del problema de configuración (certificado, etc.), o <c>null</c> si está lista para enviar.</summary>
    string? ConfigurationProblem { get; }

    /// <exception cref="AeatCommunicationException">No se pudo completar el envío.</exception>
    Task<AeatResponse> SubmitAsync(System.Xml.Linq.XElement requestBody, CancellationToken cancellationToken);
}

/// <summary>Datos del sistema informático que se declaran en cada registro (deben coincidir con la declaración responsable).</summary>
public sealed record BillingSystemInfo(
    string ProducerName,
    string ProducerNif,
    string SystemName,
    string SystemId,
    string Version,
    string InstallationNumber)
{
    public bool Complete =>
        !string.IsNullOrWhiteSpace(ProducerName) &&
        TPVBar.Domain.Invoicing.SpanishTaxId.IsValid(ProducerNif) &&
        SystemId is { Length: > 0 and <= 2 } &&
        !string.IsNullOrWhiteSpace(InstallationNumber);
}

public sealed record SubmissionRunResult(int Attempted, int Accepted, int AcceptedWithErrors, int Rejected, int Failed, string? Skipped);

public sealed record SubmissionOverview(
    AeatMode Mode,
    string? ConfigurationProblem,
    bool SystemInfoComplete,
    int Pending,
    int Accepted,
    int AcceptedWithErrors,
    int Rejected,
    DateTimeOffset? LastAttemptAt,
    string? LastMessage);

public sealed record SubmissionRow(long Sequence, string InvoiceNumber, string Type, string Status, int Attempts, DateTimeOffset? LastAttemptAt, DateTimeOffset? NextAttemptAt, string? Csv, int? ErrorCode, string? Message);

/// <summary>Cerrojo único (singleton) que impide dos envíos simultáneos a la AEAT en el mismo servidor.</summary>
public sealed class BillingSubmissionLock
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}
