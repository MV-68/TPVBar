using TPVBar.Domain.Catalog;
using TPVBar.Domain.Printing;

namespace TPVBar.Application.Features.Printing;

public sealed record PrinterRow(
    int Id,
    string Name,
    PrinterType Type,
    PrinterConnection Connection,
    string Target,
    int PaperWidthMm,
    bool IsActive,
    bool IsSimulated,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastErrorAt,
    string? LastError,
    int PendingJobs,
    int FailedJobs);

public sealed record PrinterInput(string Name, PrinterType Type, PrinterConnection Connection, string Target, int PaperWidthMm);

public sealed record RouteRow(PreparationStation Station, int PrinterId);

public sealed record PrintJobRow(
    long Id,
    int PrinterId,
    string PrinterName,
    PrintJobType Type,
    string Reference,
    PrintJobStatus Status,
    DeliveryLevel DeliveryLevel,
    int Attempts,
    int MaxAttempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? ProcessedAt,
    string? LastError);

public sealed record PrintOverview(
    IReadOnlyList<PrinterRow> Printers,
    IReadOnlyList<RouteRow> Routes,
    bool AgentOnline,
    DateTimeOffset? AgentLastSeenAt);

/// <summary>Resumen para el indicador de impresión de la barra superior.</summary>
public sealed record PrintSummary(int Pending, int Failed, bool AgentOnline, bool AnyPrinterConfigured, string? LastError);
