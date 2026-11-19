using System.Globalization;
using System.Text.Json;
using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Invoicing;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Invoicing;

/// <summary>
/// Cola de envío de registros de facturación a la AEAT. Reglas (diseñadas para no dañar la cadena):
/// los registros se envían SIEMPRE en el orden de la cadena y por lotes consecutivos; un registro no se envía hasta que los
/// anteriores han sido aceptados; si la AEAT rechaza uno, la cola se detiene hasta que alguien lo revise; se respeta la
/// espera (<c>TiempoEsperaEnvio</c>) que indica la AEAT; los fallos de comunicación se reintentan con retroceso.
/// Con el modo Desactivado no se envía nada.
/// </summary>
public sealed class BillingSubmissionService(
    IAppData data,
    IUnitOfWork unitOfWork,
    IAuditLog auditLog,
    ICurrentUser user,
    IAeatGateway gateway,
    TimeProvider time,
    BillingSubmissionLock? submissionLock = null)
{
    public const int MaxBatch = 100;
    public const int DefaultWaitSeconds = 60;
    public const string SystemName = "TPVBar";
    public const string DefaultSystemId = "TB";

    // ───────────── Configuración del sistema informático ─────────────

    public async Task<BillingSystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageConfiguration);
        return await LoadSystemInfoAsync(cancellationToken);
    }

    public async Task SaveSystemInfoAsync(string producerName, string producerNif, string systemId, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageConfiguration);
        var nif = SpanishTaxId.Normalize(producerNif);
        if (string.IsNullOrWhiteSpace(producerName) || producerName.Trim().Length > 120)
        {
            throw new DomainException("El nombre del productor es obligatorio (máximo 120 caracteres).");
        }

        if (!SpanishTaxId.IsValid(nif))
        {
            throw new DomainException("El NIF del productor no es válido.");
        }

        var id = (systemId ?? string.Empty).Trim().ToUpperInvariant();
        if (id.Length is 0 or > 2)
        {
            throw new DomainException("El identificador del sistema informático debe tener 1 o 2 caracteres.");
        }

        var now = time.GetUtcNow();
        await SetAsync(AppSettingKeys.BillingProducerName, producerName.Trim(), now, cancellationToken);
        await SetAsync(AppSettingKeys.BillingProducerNif, nif, now, cancellationToken);
        await SetAsync(AppSettingKeys.BillingSystemId, id, now, cancellationToken);
        _ = await EnsureInstallationNumberAsync(now, cancellationToken);
        auditLog.Add(AuditEvent.Create(AuditActions.BillingSystemChanged, "settings", "billing-system", now, user.UserId, user.Workstation, JsonSerializer.Serialize(new { nif, id })));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    // ───────────── Estado ─────────────

    public async Task<SubmissionOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.IssueInvoices);
        var rows = await data.ToListAsync(data.NoTracking(data.BillingSubmissions), cancellationToken);
        var last = rows.Where(r => r.LastAttemptAt is not null).OrderByDescending(r => r.LastAttemptAt).FirstOrDefault();
        var system = await LoadSystemInfoAsync(cancellationToken);
        return new SubmissionOverview(
            gateway.Mode,
            gateway.ConfigurationProblem,
            system.Complete,
            rows.Count(r => r.Status is SubmissionStatus.Pending or SubmissionStatus.Sent),
            rows.Count(r => r.Status == SubmissionStatus.Accepted),
            rows.Count(r => r.Status == SubmissionStatus.AcceptedWithErrors),
            rows.Count(r => r.Status == SubmissionStatus.Rejected),
            last?.LastAttemptAt,
            last?.Message);
    }

    /// <summary>Registros que requieren atención (pendientes con fallo, rechazados o aceptados con errores) y los últimos enviados.</summary>
    public async Task<IReadOnlyList<SubmissionRow>> ListAsync(bool onlyAttention, CancellationToken cancellationToken)
    {
        user.Require(Permission.IssueInvoices);
        var submissions = await data.ToListAsync(data.NoTracking(data.BillingSubmissions).OrderByDescending(s => s.RecordSequence).Take(300), cancellationToken);
        if (onlyAttention)
        {
            submissions = [.. submissions.Where(s => s.Status is SubmissionStatus.Rejected or SubmissionStatus.AcceptedWithErrors || (s.Status == SubmissionStatus.Pending && s.Message is not null))];
        }

        var sequences = submissions.Select(s => s.RecordSequence).ToList();
        var records = (await data.ToListAsync(data.NoTracking(data.BillingRecords).Where(r => sequences.Contains(r.Sequence)), cancellationToken)).ToDictionary(r => r.Sequence);
        return
        [
            .. submissions.Select(s =>
            {
                var r = records[s.RecordSequence];
                return new SubmissionRow(s.RecordSequence, r.InvoiceNumber, r.Type == BillingRecordType.Alta ? "Alta" : "Anulación", s.Status.ToString(), s.Attempts, s.LastAttemptAt, s.NextAttemptAt, s.Csv, s.ErrorCode, s.Message);
            }),
        ];
    }

    // ───────────── Acciones manuales ─────────────

    /// <summary>Envía ahora lo pendiente (respeta la espera de la AEAT).</summary>
    public Task<SubmissionRunResult> SendNowAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageConfiguration);
        return ProcessPendingAsync(cancellationToken);
    }

    /// <summary>Vuelve a poner en cola un registro rechazado (p. ej. tras resolver con el asesor el motivo del rechazo).</summary>
    public async Task RequeueAsync(long sequence, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageConfiguration);
        var submission = await data.FirstOrDefaultAsync(data.BillingSubmissions.Where(s => s.RecordSequence == sequence), cancellationToken)
            ?? throw new NotFoundException("El registro no existe.");
        var now = time.GetUtcNow();
        submission.Requeue(now);
        auditLog.Add(AuditEvent.Create(AuditActions.BillingSubmissionRequeued, "billing-record", sequence.ToString(CultureInfo.InvariantCulture), now, user.UserId, user.Workstation));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Subsana un registro: genera un registro NUEVO (nueva fecha-hora y huella, al final de la cadena) para la misma factura, marcado como
    /// subsanación, y deja el original como «Sustituido» (se conserva, no se modifica). Sirve para un alta rechazada (<c>Subsanacion = S</c>,
    /// <c>RechazoPrevio = S</c>), un alta aceptada con errores (<c>Subsanacion = S</c>, <c>RechazoPrevio = N</c>) y una anulación rechazada
    /// (<c>RechazoPrevio = S</c>). Útil cuando la causa estaba en los datos que entran en la huella; si la causa era de configuración
    /// (productor, certificado) basta con «Reintentar». Devuelve el número del registro nuevo.
    /// </summary>
    public async Task<long> CorrectAsync(long sequence, Guid requestId, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageConfiguration);

        var key = requestId.ToString("N");
        if (await data.AnyAsync(data.IdempotencyRecords.Where(r => r.Key == key), cancellationToken))
        {
            return 0;
        }

        var submission = await data.FirstOrDefaultAsync(data.BillingSubmissions.Where(s => s.RecordSequence == sequence), cancellationToken)
            ?? throw new NotFoundException("El registro no existe.");
        var original = await data.FirstOrDefaultAsync(data.NoTracking(data.BillingRecords).Where(r => r.Sequence == sequence), cancellationToken)
            ?? throw new NotFoundException("El registro no existe.");
        if (submission.Status is not (SubmissionStatus.Rejected or SubmissionStatus.AcceptedWithErrors))
        {
            throw new DomainException("Solo se puede subsanar un registro rechazado o aceptado con errores.");
        }

        if (original.Type == BillingRecordType.Anulacion && submission.Status == SubmissionStatus.AcceptedWithErrors)
        {
            throw new DomainException("Una anulación aceptada con errores no se puede subsanar desde TPVBar; consúltelo con su asesor.");
        }

        var invoice = await data.FirstOrDefaultAsync(data.Invoices.Where(i => i.Id == original.InvoiceId), cancellationToken)
            ?? throw new NotFoundException("La factura del registro no existe.");
        var head = await data.FirstOrDefaultAsync(data.BillingChainHeads.Where(h => h.Id == BillingChainHead.SingleId), cancellationToken)
            ?? throw new DomainException("Falta la cabeza de la cadena de facturación.");

        var now = time.GetUtcNow();
        var local = BusinessTime.ToLocal(now);
        var rejected = submission.Status == SubmissionStatus.Rejected;
        var record = original.Type == BillingRecordType.Alta
            ? BillingRecord.CreateAlta(invoice, head.LastSequence + 1, head.LastHash, local, user.UserId, original.Sequence, rejected)
            : BillingRecord.CreateCancellation(invoice, original.Reason ?? "Anulación", head.LastSequence + 1, head.LastHash, local, user.UserId, original.Sequence, rejected);
        head.Advance(record);
        data.Add(record);
        data.Add(BillingSubmission.Pending(record, now));
        submission.Supersede(record.Sequence, now);

        auditLog.Add(AuditEvent.Create(
            AuditActions.BillingRecordCorrected,
            "billing-record",
            sequence.ToString(CultureInfo.InvariantCulture),
            now,
            user.UserId,
            user.Workstation,
            JsonSerializer.Serialize(new { corrected = sequence, record = record.Sequence, invoice = invoice.FullNumber, type = original.Type.ToString(), rejectedBefore = rejected })));
        data.Add(IdempotencyRecord.Create(requestId, "billing.correct", now));
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateEntryException ex)
        {
            throw new ConcurrencyConflictException("Otra operación de facturación se confirmó a la vez. Se reintenta.", ex);
        }

        return record.Sequence;
    }

    // ───────────── Proceso (lo ejecuta el servicio en segundo plano) ─────────────

    /// <summary>Sin comprobación de permisos: lo invoca el servicio en segundo plano del servidor o <see cref="SendNowAsync"/>.</summary>
    public async Task<SubmissionRunResult> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        // Un solo envío a la vez en todo el servidor: el servicio en segundo plano y «Enviar ahora» no pueden mandar el mismo lote dos veces.
        if (submissionLock is not null && !await submissionLock.Semaphore.WaitAsync(0, cancellationToken))
        {
            return Skipped("Ya hay un envío en curso.");
        }

        try
        {
            return await ProcessCoreAsync(cancellationToken);
        }
        finally
        {
            submissionLock?.Semaphore.Release();
        }
    }

    private async Task<SubmissionRunResult> ProcessCoreAsync(CancellationToken cancellationToken)
    {
        if (gateway.Mode == AeatMode.Disabled)
        {
            return Skipped("El envío a la AEAT está desactivado.");
        }

        if (gateway.ConfigurationProblem is { } problem)
        {
            return Skipped(problem);
        }

        var now = time.GetUtcNow();
        var system = await LoadSystemInfoAsync(cancellationToken);
        if (!system.Complete)
        {
            return Skipped("Faltan los datos del productor del sistema informático (Facturación → Sistema informático).");
        }

        var open = await data.ToListAsync(
            data.BillingSubmissions.Where(s => s.Status != SubmissionStatus.Accepted && s.Status != SubmissionStatus.AcceptedWithErrors && s.Status != SubmissionStatus.Superseded).OrderBy(s => s.RecordSequence).Take(MaxBatch + 1),
            cancellationToken);
        if (open.Count == 0)
        {
            return new SubmissionRunResult(0, 0, 0, 0, 0, null);
        }

        var head = open[0];
        if (head.Status == SubmissionStatus.Rejected)
        {
            return Skipped($"La cola está detenida: la AEAT rechazó el registro {head.RecordSequence}. Revíselo en Facturación.");
        }

        // La espera que indica la AEAT (TiempoEsperaEnvio) vale para todo envío, no solo para el registro que se acaba de enviar.
        var lastAttempt = await data.FirstOrDefaultAsync(
            data.NoTracking(data.BillingSubmissions).Where(s => s.LastAttemptAt != null).OrderByDescending(s => s.LastAttemptAt),
            cancellationToken);
        var wait = new[] { head.NextAttemptAt, lastAttempt?.NextAttemptAt }.Where(w => w is not null).Max();
        if (wait is { } until && until > now)
        {
            return Skipped($"Esperando hasta las {BusinessTime.ToLocal(until):HH:mm:ss} (espera indicada por la AEAT o reintento).");
        }

        // Lote: registros consecutivos y pendientes, sin saltar ninguno de la cadena.
        var batch = new List<BillingSubmission>();
        foreach (var submission in open)
        {
            var expected = batch.Count == 0 ? submission.RecordSequence : batch[^1].RecordSequence + 1;
            if (submission.Status != SubmissionStatus.Pending || submission.RecordSequence != expected || (submission.NextAttemptAt is { } w && w > now))
            {
                break;
            }

            batch.Add(submission);
        }

        var sequences = batch.Select(b => b.RecordSequence).ToList();
        var firstSeq = sequences[0];
        var records = await data.ToListAsync(
            data.NoTracking(data.BillingRecords).Where(r => (r.Sequence >= firstSeq && r.Sequence <= sequences.Max()) || r.Sequence == firstSeq - 1).OrderBy(r => r.Sequence),
            cancellationToken);
        var byInvoiceIds = records.Select(r => r.InvoiceId).Distinct().ToList();
        var invoices = (await data.ToListAsync(data.NoTracking(data.Invoices).Where(i => byInvoiceIds.Contains(i.Id)), cancellationToken)).ToDictionary(i => i.Id);
        var originalIds = invoices.Values.Where(i => i.RectifiedInvoiceId is not null).Select(i => i.RectifiedInvoiceId!.Value).Distinct().ToList();
        var originals = (await data.ToListAsync(data.NoTracking(data.Invoices).Where(i => originalIds.Contains(i.Id)), cancellationToken)).ToDictionary(i => i.Id);

        var items = new List<AeatXmlBuilder.Item>();
        foreach (var seq in sequences)
        {
            var record = records.First(r => r.Sequence == seq);
            var invoice = invoices[record.InvoiceId];
            if (items.Count > 0 && invoice.IssuerNif != items[0].Invoice.IssuerNif)
            {
                break; // otro emisor: va en el siguiente lote
            }

            var previous = records.Find(r => r.Sequence == seq - 1);
            Invoice? rectified = invoice.RectifiedInvoiceId is { } id ? originals.GetValueOrDefault(id) : null;
            items.Add(new AeatXmlBuilder.Item(record, invoice, previous, rectified));
        }

        batch = [.. batch.Take(items.Count)];
        var body = AeatXmlBuilder.Build(items, system);

        AeatResponse response;
        try
        {
            response = await gateway.SubmitAsync(body, cancellationToken);
        }
        catch (AeatCommunicationException ex)
        {
            foreach (var s in batch)
            {
                s.RecordTransientFailure("Fallo de comunicación con la AEAT: " + ex.Message, now, now.AddSeconds(Backoff(s.Attempts)));
            }

            Audit(AuditActions.BillingSubmissionFailed, firstSeq, now, new { from = firstSeq, count = batch.Count, error = ex.Message });
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new SubmissionRunResult(batch.Count, 0, 0, 0, batch.Count, null);
        }

        var wait2 = response.WaitSeconds > 0 ? response.WaitSeconds : DefaultWaitSeconds;
        var next = now.AddSeconds(wait2);
        int accepted = 0, withErrors = 0, rejected = 0, failed = 0;
        foreach (var submission in batch)
        {
            var record = records.First(r => r.Sequence == submission.RecordSequence);
            var operation = record.Type == BillingRecordType.Alta ? "Alta" : "Anulacion";
            var line = response.Lines.FirstOrDefault(l => string.Equals(l.InvoiceNumber, record.InvoiceNumber, StringComparison.Ordinal) && string.Equals(l.Operation, operation, StringComparison.OrdinalIgnoreCase));
            if (line is null)
            {
                submission.RecordTransientFailure($"La respuesta de la AEAT no incluye el registro (estado del envío: {response.SendStatus}).", now, next);
                failed++;
                continue;
            }

            var (status, message) = Map(line);
            submission.RecordResult(status, response.Csv, line.ErrorCode, message, now, next);
            switch (status)
            {
                case SubmissionStatus.Accepted: accepted++; break;
                case SubmissionStatus.AcceptedWithErrors: withErrors++; break;
                default: rejected++; break;
            }
        }

        Audit(AuditActions.BillingSubmitted, firstSeq, now, new { from = firstSeq, count = batch.Count, accepted, withErrors, rejected, failed, csv = response.Csv, status = response.SendStatus });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new SubmissionRunResult(batch.Count, accepted, withErrors, rejected, failed, null);
    }

    private static (SubmissionStatus Status, string? Message) Map(AeatLineResult line)
    {
        var detail = line.ErrorCode is { } code ? $"[{code}] {line.ErrorDescription}" : line.ErrorDescription;
        switch (line.RecordStatus)
        {
            case "Correcto":
                return (SubmissionStatus.Accepted, null);
            case "AceptadoConErrores":
                return (SubmissionStatus.AcceptedWithErrors, detail);
            default:
                // Un duplicado ya aceptado en la AEAT (p. ej. se perdió la respuesta anterior) cuenta como aceptado.
                return line.DuplicateStatus switch
                {
                    "Correcta" => (SubmissionStatus.Accepted, "Ya constaba en la AEAT (registro duplicado aceptado)."),
                    "AceptadaConErrores" => (SubmissionStatus.AcceptedWithErrors, "Ya constaba en la AEAT con errores. " + detail),
                    _ => (SubmissionStatus.Rejected, detail ?? "Rechazado por la AEAT."),
                };
        }
    }

    private static double Backoff(int attempts) => Math.Min(30 * 60, 60 * Math.Pow(2, Math.Min(attempts, 5)));

    private static SubmissionRunResult Skipped(string reason) => new(0, 0, 0, 0, 0, reason);

    private void Audit(string action, long sequence, DateTimeOffset now, object details) =>
        auditLog.Add(AuditEvent.Create(action, "billing-record", sequence.ToString(CultureInfo.InvariantCulture), now, null, "sistema", JsonSerializer.Serialize(details)));

    private async Task<BillingSystemInfo> LoadSystemInfoAsync(CancellationToken cancellationToken)
    {
        var all = (await data.ToListAsync(data.NoTracking(data.AppSettings).Where(s => s.Key.StartsWith("billing.")), cancellationToken)).ToDictionary(s => s.Key, s => s.Value);
        return new BillingSystemInfo(
            all.GetValueOrDefault(AppSettingKeys.BillingProducerName, string.Empty),
            all.GetValueOrDefault(AppSettingKeys.BillingProducerNif, string.Empty),
            SystemName,
            all.GetValueOrDefault(AppSettingKeys.BillingSystemId, DefaultSystemId),
            typeof(BillingSubmissionService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            all.GetValueOrDefault(AppSettingKeys.BillingInstallationNumber, string.Empty));
    }

    private async Task<string> EnsureInstallationNumberAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await data.FirstOrDefaultAsync(data.AppSettings.Where(s => s.Key == AppSettingKeys.BillingInstallationNumber), cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var number = "TPVBAR-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        data.Add(new AppSetting(AppSettingKeys.BillingInstallationNumber, number, user.UserId, now));
        return number;
    }

    private async Task SetAsync(string key, string value, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await data.FirstOrDefaultAsync(data.AppSettings.Where(s => s.Key == key), cancellationToken);
        if (existing is null)
        {
            data.Add(new AppSetting(key, value, user.UserId, now));
        }
        else
        {
            existing.Change(value, user.UserId, now);
        }
    }
}
