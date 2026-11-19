using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Printing;

/// <summary>
/// Trabajo de impresión de la cola persistente. Un agente lo reclama con un arrendamiento (lease) de
/// tiempo limitado: si el agente se cae a mitad, el trabajo vuelve a estar disponible al expirar.
/// Los fallos se reintentan con espera incremental y, agotados, el trabajo queda en <see cref="PrintJobStatus.Failed"/>
/// sin bloquear a los demás.
/// </summary>
public sealed class PrintJob
{
    public const int DefaultMaxAttempts = 5;

    private PrintJob()
    {
        ReferenceType = string.Empty;
        ReferenceId = string.Empty;
        PayloadJson = "{}";
        DedupeKey = string.Empty;
    }

    public long Id { get; private set; }

    public int PrinterId { get; private set; }

    public PrintJobType JobType { get; private set; }

    public string ReferenceType { get; private set; }

    public string ReferenceId { get; private set; }

    /// <summary>Documento serializado en JSON (ver contrato del agente). Sin secretos.</summary>
    public string PayloadJson { get; private set; }

    /// <summary>Identificador único del trabajo lógico: evita encolar el mismo ticket dos veces.</summary>
    public string DedupeKey { get; private set; }

    public PrintJobStatus Status { get; private set; }

    public DeliveryLevel DeliveryLevel { get; private set; }

    public int Attempts { get; private set; }

    public int MaxAttempts { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public string? LockedBy { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Token de concurrencia optimista (columna de sistema xmin de PostgreSQL).</summary>
    public uint RowVersion { get; private set; }

    public static PrintJob Create(
        int printerId,
        PrintJobType jobType,
        string referenceType,
        string referenceId,
        string payloadJson,
        string dedupeKey,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new DomainException("El trabajo de impresión no tiene contenido.");
        }

        if (string.IsNullOrWhiteSpace(dedupeKey))
        {
            throw new DomainException("Falta el identificador único del trabajo de impresión.");
        }

        return new PrintJob
        {
            PrinterId = printerId,
            JobType = jobType,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            PayloadJson = payloadJson,
            DedupeKey = dedupeKey,
            Status = PrintJobStatus.Pending,
            MaxAttempts = DefaultMaxAttempts,
            CreatedAt = now,
            NextAttemptAt = now,
        };
    }

    /// <summary>Espera antes del siguiente intento tras el fallo número <paramref name="failedAttempt"/>.</summary>
    public static TimeSpan Backoff(int failedAttempt) => failedAttempt switch
    {
        <= 1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(15),
        3 => TimeSpan.FromSeconds(45),
        4 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(5),
    };

    public bool IsClaimable(DateTimeOffset now) =>
        (Status == PrintJobStatus.Pending && NextAttemptAt <= now) ||
        (Status == PrintJobStatus.Processing && LockedUntil is { } until && until <= now);

    public void Claim(string agent, DateTimeOffset now, TimeSpan lease)
    {
        if (!IsClaimable(now))
        {
            throw new DomainException("El trabajo de impresión no está disponible.");
        }

        Attempts++;
        Status = PrintJobStatus.Processing;
        LockedBy = agent;
        LockedUntil = now + lease;
    }

    /// <summary>El resultado solo vale si lo envía el agente que tiene el trabajo en este momento.</summary>
    public bool IsHeldBy(string agent) => Status == PrintJobStatus.Processing && string.Equals(LockedBy, agent, StringComparison.Ordinal);

    public void Succeed(DeliveryLevel level, DateTimeOffset now)
    {
        EnsureProcessing();
        Status = PrintJobStatus.Sent;
        DeliveryLevel = level;
        ProcessedAt = now;
        LockedUntil = null;
        LockedBy = null;
        LastError = null;
    }

    /// <returns><see langword="true"/> si se reintentará; <see langword="false"/> si se han agotado los intentos.</returns>
    public bool Fail(string error, DateTimeOffset now)
    {
        EnsureProcessing();
        LastError = string.IsNullOrWhiteSpace(error) ? "Error desconocido." : (error.Length > 1000 ? error[..1000] : error);
        LockedUntil = null;
        LockedBy = null;

        if (Attempts >= MaxAttempts)
        {
            Status = PrintJobStatus.Failed;
            ProcessedAt = now;
            return false;
        }

        Status = PrintJobStatus.Pending;
        NextAttemptAt = now + Backoff(Attempts);
        return true;
    }

    /// <summary>Reintento manual de un trabajo fallido: concede tres intentos más.</summary>
    public void RetryManually(DateTimeOffset now)
    {
        if (Status != PrintJobStatus.Failed)
        {
            throw new DomainException("Solo se puede reintentar un trabajo fallido.");
        }

        Status = PrintJobStatus.Pending;
        MaxAttempts = Attempts + 3;
        NextAttemptAt = now;
        ProcessedAt = null;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status is not (PrintJobStatus.Pending or PrintJobStatus.Failed))
        {
            throw new DomainException("Solo se puede cancelar un trabajo pendiente o fallido.");
        }

        Status = PrintJobStatus.Cancelled;
        ProcessedAt = now;
    }

    private void EnsureProcessing()
    {
        if (Status != PrintJobStatus.Processing)
        {
            throw new DomainException("El trabajo de impresión no está en proceso.");
        }
    }
}
