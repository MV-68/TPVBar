namespace TPVBar.PrintContracts;

/// <summary>Hasta dónde se sabe que ha llegado el trabajo. Nunca se afirma "impreso" sin confirmación del hardware.</summary>
public enum DeliveryLevel
{
    None = 0,

    /// <summary>Escrito en un archivo de simulación: NO ha salido papel.</summary>
    Simulated = 1,

    /// <summary>Enviado por red a la impresora (sin confirmación de impresión).</summary>
    SentToDevice = 2,

    /// <summary>Aceptado por la cola de impresión de Windows (sin confirmación de impresión).</summary>
    AcceptedBySpooler = 3,

    /// <summary>El hardware ha confirmado la impresión. Solo si el modelo y el controlador lo permiten.</summary>
    Confirmed = 4,
}

public static class AgentApiRoutes
{
    public const string KeyHeader = "X-Agent-Key";
    public const string Claim = "/api/print/claim";
    public const string Printers = "/api/print/printers";
    public const string Result = "/api/print/jobs/{id}/result";

    public static string ResultFor(long jobId) => $"/api/print/jobs/{jobId}/result";
}

public sealed record ClaimRequest(string Agent, string? Version, int Max);

public sealed record ClaimedJob(
    long Id,
    int PrinterId,
    int Attempt,
    bool IsRetry,
    PrintDocument Document);

public sealed record ClaimResponse(IReadOnlyList<ClaimedJob> Jobs);

public sealed record ResultRequest(string Agent, bool Success, DeliveryLevel Level, string? Error);

/// <summary>Configuración de una impresora para el agente. No incluye contraseñas ni secretos.</summary>
public sealed record PrinterConfig(
    int Id,
    string Name,
    string Type,
    string Connection,
    string Target,
    int PaperWidthMm);

public sealed record PrintersResponse(IReadOnlyList<PrinterConfig> Printers);
