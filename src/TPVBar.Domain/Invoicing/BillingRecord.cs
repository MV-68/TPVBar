using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Invoicing;

public enum BillingRecordType
{
    /// <summary>Registro de alta (se genera al emitir cada factura).</summary>
    Alta = 1,

    /// <summary>Registro de anulación de una factura emitida por error.</summary>
    Anulacion = 2,
}

public enum SubmissionStatus
{
    /// <summary>Generado y conservado, aún no aceptado por la AEAT (o con envío desactivado).</summary>
    Pending = 1,

    /// <summary>Reservado (sin uso): el envío es síncrono, no hay estado intermedio.</summary>
    Sent = 2,

    /// <summary>La AEAT lo aceptó (estado «Correcto»).</summary>
    Accepted = 3,

    /// <summary>La AEAT lo rechazó («Incorrecto»): hay que corregirlo con el asesor; la cola se detiene.</summary>
    Rejected = 4,

    /// <summary>Aceptado por la AEAT pero con errores no bloqueantes («AceptadoConErrores»).</summary>
    AcceptedWithErrors = 5,

    /// <summary>Sustituido por un registro de subsanación posterior (el registro original se conserva, pero ya no se envía).</summary>
    Superseded = 6,
}

/// <summary>
/// Registro de facturación encadenado: cada uno incluye la huella del anterior. Inmutable (trigger en base de datos).
/// La huella permite detectar que se ha alterado o eliminado un registro intermedio.
/// </summary>
public sealed class BillingRecord
{
    private BillingRecord()
    {
        IssuerNif = string.Empty;
        InvoiceNumber = string.Empty;
        IssueDate = string.Empty;
        TypeCode = string.Empty;
        PreviousHash = string.Empty;
        Hash = string.Empty;
        GeneratedAtText = string.Empty;
    }

    /// <summary>Posición en la cadena (1, 2, 3…), sin huecos.</summary>
    public long Sequence { get; private set; }

    public BillingRecordType Type { get; private set; }

    public long InvoiceId { get; private set; }

    /// <summary>Navegación para que la factura y su registro se guarden juntos (el identificador se asigna al guardar).</summary>
    public Invoice? Invoice { get; private set; }

    public string IssuerNif { get; private set; }

    public string InvoiceNumber { get; private set; }

    /// <summary>Fecha de expedición en el formato del registro (dd-MM-yyyy).</summary>
    public string IssueDate { get; private set; }

    /// <summary>F1, F2, R1, R5… (vacío en las anulaciones).</summary>
    public string TypeCode { get; private set; }

    public decimal TaxTotal { get; private set; }

    public decimal GrandTotal { get; private set; }

    /// <summary>Huella del registro anterior (vacía en el primero de la cadena).</summary>
    public string PreviousHash { get; private set; }

    public string Hash { get; private set; }

    /// <summary>Fecha-hora de generación exactamente como se incluyó en la huella.</summary>
    public string GeneratedAtText { get; private set; }

    public DateTimeOffset GeneratedAt { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>Si es un registro de subsanación, el número del registro que corrige (alta rechazada o aceptada con errores; anulación rechazada).</summary>
    public long? CorrectsSequence { get; private set; }

    /// <summary>El registro corregido había sido rechazado por la AEAT (indicador «RechazoPrevio = S» del envío).</summary>
    public bool RejectedBefore { get; private set; }

    public Guid CreatedBy { get; private set; }

    internal static string FormatLocal(DateTimeOffset localInstant) => BillingHash.FormatTimestamp(localInstant);

    public static BillingRecord CreateAlta(Invoice invoice, long sequence, string previousHash, DateTimeOffset generatedAtLocal, Guid by, long? corrects = null, bool rejectedBefore = false)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var date = BillingHash.FormatDate(invoice.IssueDate);
        var text = FormatLocal(generatedAtLocal);
        var hash = BillingHash.Sha256Hex(BillingHash.AltaInput(invoice.IssuerNif, invoice.FullNumber, date, invoice.TypeCode, invoice.Tax, invoice.Total, previousHash, text));
        return new BillingRecord
        {
            Sequence = sequence,
            Type = BillingRecordType.Alta,
            Invoice = invoice,
            InvoiceId = invoice.Id,
            IssuerNif = invoice.IssuerNif,
            InvoiceNumber = invoice.FullNumber,
            IssueDate = date,
            TypeCode = invoice.TypeCode,
            TaxTotal = invoice.Tax,
            GrandTotal = invoice.Total,
            PreviousHash = previousHash,
            Hash = hash,
            GeneratedAtText = text,
            GeneratedAt = generatedAtLocal.ToUniversalTime(),
            CreatedBy = by,
            CorrectsSequence = corrects,
            RejectedBefore = rejectedBefore,
        };
    }

    public static BillingRecord CreateCancellation(Invoice invoice, string reason, long sequence, string previousHash, DateTimeOffset generatedAtLocal, Guid by, long? corrects = null, bool rejectedBefore = false)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > 250)
        {
            throw new DomainException("Indique el motivo de la anulación (máximo 250 caracteres).");
        }

        var date = BillingHash.FormatDate(invoice.IssueDate);
        var text = FormatLocal(generatedAtLocal);
        var hash = BillingHash.Sha256Hex(BillingHash.CancellationInput(invoice.IssuerNif, invoice.FullNumber, date, previousHash, text));
        return new BillingRecord
        {
            Sequence = sequence,
            Type = BillingRecordType.Anulacion,
            Invoice = invoice,
            InvoiceId = invoice.Id,
            IssuerNif = invoice.IssuerNif,
            InvoiceNumber = invoice.FullNumber,
            IssueDate = date,
            TypeCode = string.Empty,
            TaxTotal = invoice.Tax,
            GrandTotal = invoice.Total,
            PreviousHash = previousHash,
            Hash = hash,
            GeneratedAtText = text,
            GeneratedAt = generatedAtLocal.ToUniversalTime(),
            Reason = trimmed,
            CreatedBy = by,
            CorrectsSequence = corrects,
            RejectedBefore = rejectedBefore,
        };
    }

    /// <summary>Recalcula la huella con los datos guardados.</summary>
    public string RecomputeHash() => Type == BillingRecordType.Alta
        ? BillingHash.Sha256Hex(BillingHash.AltaInput(IssuerNif, InvoiceNumber, IssueDate, TypeCode, TaxTotal, GrandTotal, PreviousHash, GeneratedAtText))
        : BillingHash.Sha256Hex(BillingHash.CancellationInput(IssuerNif, InvoiceNumber, IssueDate, PreviousHash, GeneratedAtText));
}

/// <summary>Cabeza de la cadena: último número y huella. Es lo único mutable; serializa la emisión (xmin).</summary>
public sealed class BillingChainHead
{
    public const int SingleId = 1;

    private BillingChainHead()
    {
        LastHash = string.Empty;
    }

    /// <summary>Cabeza vacía (cadena sin registros). En la base de datos la crea la migración.</summary>
    public static BillingChainHead Initial() => new();

    public int Id { get; private set; } = SingleId;

    public long LastSequence { get; private set; }

    public string LastHash { get; private set; }

    public uint RowVersion { get; private set; }

    public void Advance(BillingRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Sequence != LastSequence + 1 || record.PreviousHash != LastHash)
        {
            throw new DomainException("El registro no continúa la cadena de facturación.");
        }

        LastSequence = record.Sequence;
        LastHash = record.Hash;
    }
}

/// <summary>
/// Estado de envío de un registro a la AEAT. Es lo único mutable de un registro de facturación (el registro en sí no
/// cambia nunca). Transiciones: Pendiente → Aceptado / Aceptado con errores / Rechazado; un fallo de comunicación deja el
/// registro Pendiente con una hora de próximo intento.
/// </summary>
public sealed class BillingSubmission
{
    private BillingSubmission()
    {
    }

    public long RecordSequence { get; private set; }

    public SubmissionStatus Status { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public string? Message { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>No volver a intentarlo antes de esta hora (espera indicada por la AEAT o retroceso tras un fallo).</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>Código seguro de verificación que devuelve la AEAT al aceptar el envío.</summary>
    public string? Csv { get; private set; }

    public int? ErrorCode { get; private set; }

    public bool IsTerminal => Status is SubmissionStatus.Accepted or SubmissionStatus.AcceptedWithErrors or SubmissionStatus.Superseded;

    public static BillingSubmission Pending(BillingRecord record, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new BillingSubmission { RecordSequence = record.Sequence, Status = SubmissionStatus.Pending, UpdatedAt = now };
    }

    /// <summary>Resultado definitivo devuelto por la AEAT para este registro.</summary>
    public void RecordResult(SubmissionStatus status, string? csv, int? errorCode, string? message, DateTimeOffset now, DateTimeOffset? nextAttemptAt)
    {
        if (status is SubmissionStatus.Pending or SubmissionStatus.Sent)
        {
            throw new Exceptions.DomainException("El resultado de la AEAT debe ser aceptado, aceptado con errores o rechazado.");
        }

        if (IsTerminal)
        {
            throw new Exceptions.DomainException("El registro ya fue aceptado por la AEAT.");
        }

        Status = status;
        Attempts++;
        LastAttemptAt = now;
        NextAttemptAt = nextAttemptAt;
        Csv = Truncate(csv, 64);
        ErrorCode = errorCode;
        Message = Truncate(message, 500);
        UpdatedAt = now;
    }

    /// <summary>Fallo de comunicación o de servicio: el registro sigue pendiente y se reintenta más tarde.</summary>
    public void RecordTransientFailure(string message, DateTimeOffset now, DateTimeOffset nextAttemptAt)
    {
        if (IsTerminal)
        {
            throw new Exceptions.DomainException("El registro ya fue aceptado por la AEAT.");
        }

        Status = SubmissionStatus.Pending;
        Attempts++;
        LastAttemptAt = now;
        NextAttemptAt = nextAttemptAt;
        Message = Truncate(message, 500);
        UpdatedAt = now;
    }

    /// <summary>Marca el registro como sustituido por uno de subsanación. Solo un registro rechazado o aceptado con errores se puede subsanar.</summary>
    public void Supersede(long bySequence, DateTimeOffset now)
    {
        if (Status is not (SubmissionStatus.Rejected or SubmissionStatus.AcceptedWithErrors))
        {
            throw new Exceptions.DomainException("Solo se puede subsanar un registro rechazado o aceptado con errores.");
        }

        var previous = Message;
        Status = SubmissionStatus.Superseded;
        Message = Truncate($"Subsanado con el registro {bySequence}. Motivo original: {previous}", 500);
        UpdatedAt = now;
    }

    /// <summary>Un encargado vuelve a poner en cola un registro rechazado tras corregir la causa.</summary>
    public void Requeue(DateTimeOffset now)
    {
        if (Status != SubmissionStatus.Rejected)
        {
            throw new Exceptions.DomainException("Solo se puede reintentar un registro rechazado.");
        }

        Status = SubmissionStatus.Pending;
        NextAttemptAt = null;
        UpdatedAt = now;
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : (value.Length <= max ? value : value[..max]);
}
