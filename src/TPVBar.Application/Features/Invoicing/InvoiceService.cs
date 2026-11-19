using System.Globalization;
using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Invoicing;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Invoicing;

/// <summary>
/// Emisión, rectificación y anulación de facturas, con su registro de facturación encadenado. Cada operación es atómica:
/// factura, registro, cabeza de cadena, estado de envío, auditoría y clave de idempotencia se confirman juntos. Dos
/// emisiones simultáneas se serializan por la cabeza de la cadena (la segunda recibe un conflicto y debe reintentar).
/// </summary>
public sealed class InvoiceService(
    IAppData data,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    AuditWriter audit,
    TimeProvider time,
    IAeatGateway? gateway = null)
{
    // ───────────── Configuración ─────────────

    public async Task<InvoiceSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.IssueInvoices);
        return await LoadSettingsAsync(cancellationToken);
    }

    public async Task SaveSettingsAsync(string name, string nif, string address, decimal simplifiedLimit, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageConfiguration);

        var normalizedNif = SpanishTaxId.Normalize(nif);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
        {
            throw new DomainException("El nombre o razón social es obligatorio (máximo 120 caracteres).");
        }

        if (!SpanishTaxId.IsValid(normalizedNif))
        {
            throw new DomainException("El NIF no es válido.");
        }

        if (string.IsNullOrWhiteSpace(address) || address.Trim().Length > 250)
        {
            throw new DomainException("El domicilio es obligatorio (máximo 250 caracteres).");
        }

        if (simplifiedLimit is <= 0 or > 100000 || Money.Round(simplifiedLimit) != simplifiedLimit)
        {
            throw new DomainException("El límite de la factura simplificada debe ser mayor que cero, con 2 decimales.");
        }

        // Cambiar el emisor no altera facturas ya emitidas (guardan su propia copia), pero sí las próximas.
        var before = await LoadSettingsAsync(cancellationToken);
        var now = time.GetUtcNow();
        await SetAsync(AppSettingKeys.InvoiceIssuerName, name.Trim(), now, cancellationToken);
        await SetAsync(AppSettingKeys.InvoiceIssuerNif, normalizedNif, now, cancellationToken);
        await SetAsync(AppSettingKeys.InvoiceIssuerAddress, address.Trim(), now, cancellationToken);
        await SetAsync(AppSettingKeys.InvoiceSimplifiedLimit, simplifiedLimit.ToString("0.00", CultureInfo.InvariantCulture), now, cancellationToken);

        audit.Write(
            AuditActions.InvoiceSettingsChanged,
            "settings",
            "invoice",
            new { previousNif = before.IssuerNif, nif = normalizedNif, simplifiedLimit });
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    // ───────────── Emisión ─────────────

    public async Task<IssueInvoiceResult> IssueAsync(IssueInvoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        user.Require(Permission.IssueInvoices);

        if (await WasProcessedAsync(request.RequestId, cancellationToken))
        {
            var existing = await data.FirstOrDefaultAsync(
                data.NoTracking(data.Invoices).Where(i => i.OrderId == request.OrderId && i.Kind != InvoiceKind.Rectificative).OrderByDescending(i => i.Id),
                cancellationToken);
            return new IssueInvoiceResult(existing?.Id ?? 0, existing?.FullNumber ?? string.Empty, AlreadyProcessed: true);
        }

        var order = await data.FirstOrDefaultAsync(data.NoTracking(data.Orders).Where(o => o.Id == request.OrderId), cancellationToken)
            ?? throw new NotFoundException("La comanda no existe.");

        var live = await FindLiveInvoiceAsync(order.Id, cancellationToken);
        if (live is not null)
        {
            throw new DomainException($"La comanda ya tiene la factura {live.FullNumber}. Para cambiarla, rectifíquela primero.");
        }

        var settings = await LoadSettingsAsync(cancellationToken);
        var issuer = new InvoiceIssuer(settings.IssuerName, settings.IssuerNif, settings.IssuerAddress);

        var now = time.GetUtcNow();
        var local = BusinessTime.ToLocal(now);
        var issueDate = BusinessTime.ToLocalDate(now);
        var series = Invoice.SeriesFor(request.Kind, issueDate.Year);
        var number = await NextNumberAsync(series, cancellationToken);

        var invoice = Invoice.IssueFromOrder(order, request.Kind, issuer, request.Recipient, number, settings.SimplifiedLimit, user.UserId, now, issueDate);
        var record = await AppendAltaAsync(invoice, local, cancellationToken);

        audit.Write(
            AuditActions.InvoiceIssued,
            "order",
            order.Id,
            new { number = invoice.FullNumber, kind = invoice.Kind.ToString(), invoice.TypeCode, invoice.Total, record = record.Sequence, recipientNif = invoice.RecipientNif });
        await CompleteAsync(request.RequestId, "invoice.issue", cancellationToken);

        return new IssueInvoiceResult(invoice.Id, invoice.FullNumber, AlreadyProcessed: false);
    }

    public async Task<IssueInvoiceResult> RectifyAsync(RectifyInvoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        user.Require(Permission.RectifyInvoices);

        if (await WasProcessedAsync(request.RequestId, cancellationToken))
        {
            return new IssueInvoiceResult(0, string.Empty, AlreadyProcessed: true);
        }

        var original = await data.FirstOrDefaultAsync(data.NoTracking(data.Invoices).Where(i => i.Id == request.InvoiceId), cancellationToken)
            ?? throw new NotFoundException("La factura no existe.");
        if (await IsCancelledAsync(original.Id, cancellationToken))
        {
            throw new DomainException("La factura está anulada: no se puede rectificar.");
        }

        var done = await RectifiedSoFarAsync(original.Id, cancellationToken);
        var quantities = new Dictionary<long, decimal>();
        foreach (var line in request.Lines ?? [])
        {
            if (!quantities.TryAdd(line.InvoiceLineId, line.Quantity))
            {
                throw new DomainException("Una línea aparece repetida en la rectificación.");
            }
        }

        var now = time.GetUtcNow();
        var issueDate = BusinessTime.ToLocalDate(now);
        var series = Invoice.SeriesFor(InvoiceKind.Rectificative, issueDate.Year);
        var number = await NextNumberAsync(series, cancellationToken);

        var rectification = Invoice.Rectify(original, quantities, done, request.Reason, number, user.UserId, now, issueDate);
        var record = await AppendAltaAsync(rectification, BusinessTime.ToLocal(now), cancellationToken);

        audit.Write(
            AuditActions.InvoiceRectified,
            "invoice",
            original.Id,
            new { original = original.FullNumber, number = rectification.FullNumber, rectification.TypeCode, rectification.Total, reason = rectification.RectificationReason, record = record.Sequence });
        await CompleteAsync(request.RequestId, "invoice.rectify", cancellationToken);

        return new IssueInvoiceResult(rectification.Id, rectification.FullNumber, AlreadyProcessed: false);
    }

    /// <summary>
    /// Anula una factura emitida por error generando un registro de anulación (la factura no se borra ni se edita).
    /// No se puede anular una factura con rectificativas: hay que rectificar.
    /// </summary>
    public async Task CancelAsync(long invoiceId, string reason, Guid requestId, CancellationToken cancellationToken)
    {
        user.Require(Permission.RectifyInvoices);

        if (await WasProcessedAsync(requestId, cancellationToken))
        {
            return;
        }

        var invoice = await data.FirstOrDefaultAsync(data.Invoices.Where(i => i.Id == invoiceId), cancellationToken)
            ?? throw new NotFoundException("La factura no existe.");
        if (await IsCancelledAsync(invoiceId, cancellationToken))
        {
            throw new DomainException("La factura ya está anulada.");
        }

        if (invoice.Kind == InvoiceKind.Rectificative)
        {
            throw new DomainException("Una factura rectificativa no se anula: emita otra rectificación de la original si fue un error.");
        }

        if (await data.AnyAsync(data.NoTracking(data.Invoices).Where(i => i.RectifiedInvoiceId == invoiceId), cancellationToken))
        {
            throw new DomainException("La factura tiene rectificativas: no se puede anular.");
        }

        var head = await RequireHeadAsync(cancellationToken);
        var now = time.GetUtcNow();
        var record = BillingRecord.CreateCancellation(invoice, reason, head.LastSequence + 1, head.LastHash, BusinessTime.ToLocal(now), user.UserId);
        head.Advance(record);
        data.Add(record);
        data.Add(BillingSubmission.Pending(record, now));

        audit.Write(AuditActions.InvoiceCancelled, "invoice", invoiceId, new { number = invoice.FullNumber, reason = reason.Trim(), record = record.Sequence });
        await CompleteAsync(requestId, "invoice.cancel", cancellationToken);
    }

    // ───────────── Consulta ─────────────

    public async Task<IReadOnlyList<InvoiceSummary>> ListAsync(InvoiceFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        user.Require(Permission.IssueInvoices);

        var query = data.NoTracking(data.Invoices).AsQueryable();
        if (filter.From is { } from)
        {
            query = query.Where(i => i.IssueDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(i => i.IssueDate <= to);
        }

        if (filter.Kind is { } kind)
        {
            query = query.Where(i => i.Kind == kind);
        }

        var invoices = await data.ToListAsync(query.OrderByDescending(i => i.Id).Take(5000), cancellationToken);

        // El texto se filtra en memoria (sin distinguir mayúsculas): el volumen de un bar lo permite.
        var text = filter.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            invoices =
            [
                .. invoices.Where(i =>
                    i.FullNumber.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    (i.RecipientName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (i.RecipientNif?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)),
            ];
        }

        invoices = [.. invoices.Take(500)];
        var ids = invoices.Select(i => i.Id).ToList();
        var records = await data.ToListAsync(data.NoTracking(data.BillingRecords).Where(r => ids.Contains(r.InvoiceId)), cancellationToken);
        var statuses = (await data.ToListAsync(data.NoTracking(data.BillingSubmissions), cancellationToken)).ToDictionary(s => s.RecordSequence, s => s.Status);

        return
        [
            .. invoices.Select(i =>
            {
                var alta = records.Where(r => r.InvoiceId == i.Id && r.Type == BillingRecordType.Alta).MaxBy(r => r.Sequence);
                return new InvoiceSummary(
                    i.Id,
                    i.FullNumber,
                    i.Kind,
                    i.TypeCode,
                    i.IssueDate,
                    i.RecipientName,
                    i.Total,
                    i.OrderId,
                    records.Exists(r => r.InvoiceId == i.Id && r.Type == BillingRecordType.Anulacion),
                    alta is not null && statuses.TryGetValue(alta.Sequence, out var s) ? s : SubmissionStatus.Pending);
            }),
        ];
    }

    public async Task<IReadOnlyList<InvoiceSummary>> ListForOrderAsync(long orderId, CancellationToken cancellationToken)
    {
        user.Require(Permission.IssueInvoices);
        var all = await ListAsync(new InvoiceFilter(null, null, null, null), cancellationToken);
        return [.. all.Where(i => i.OrderId == orderId)];
    }

    public async Task<InvoiceView> GetAsync(long invoiceId, CancellationToken cancellationToken)
    {
        user.Require(Permission.IssueInvoices);

        var invoice = await data.FirstOrDefaultAsync(data.NoTracking(data.Invoices).Where(i => i.Id == invoiceId), cancellationToken)
            ?? throw new NotFoundException("La factura no existe.");
        var records = await data.ToListAsync(data.NoTracking(data.BillingRecords).Where(r => r.InvoiceId == invoiceId), cancellationToken);
        var alta = records.Where(r => r.Type == BillingRecordType.Alta).MaxBy(r => r.Sequence) ?? throw new DomainException("La factura no tiene registro de facturación.");
        var cancellation = records.Find(r => r.Type == BillingRecordType.Anulacion);
        var submission = await data.FirstOrDefaultAsync(data.NoTracking(data.BillingSubmissions).Where(s => s.RecordSequence == alta.Sequence), cancellationToken);
        var settings = await LoadSettingsAsync(cancellationToken);

        var rectifications = await data.ToListAsync(
            data.NoTracking(data.Invoices).Where(i => i.RectifiedInvoiceId == invoiceId).OrderBy(i => i.Id), cancellationToken);
        var done = invoice.Kind == InvoiceKind.Rectificative ? new Dictionary<long, RectifiedAmounts>() : SumRectified(rectifications);

        string? rectifiedNumber = null;
        if (invoice.RectifiedInvoiceId is { } originalId)
        {
            rectifiedNumber = (await data.FirstOrDefaultAsync(data.NoTracking(data.Invoices).Where(i => i.Id == originalId), cancellationToken))?.FullNumber;
        }

        var lines = invoice.Lines.OrderBy(l => l.Id).Select(l => new InvoiceLineView(
            l.Id,
            l.Description,
            l.Quantity,
            l.UnitPrice,
            l.TaxRate,
            l.Base,
            l.Tax,
            l.Total,
            invoice.Kind == InvoiceKind.Rectificative ? 0 : l.Quantity - (done.TryGetValue(l.Id, out var d) ? d.Quantity : 0))).ToList();

        var qr = BillingHash.QrUrl(settings.QrBaseUrl, invoice.IssuerNif, invoice.FullNumber, BillingHash.FormatDate(invoice.IssueDate), invoice.Total);
        return new InvoiceView(
            invoice.Id,
            invoice.FullNumber,
            invoice.Kind,
            invoice.TypeCode,
            invoice.IssueDate,
            invoice.IssuedAt,
            invoice.OrderId,
            invoice.IssuerName,
            invoice.IssuerNif,
            invoice.IssuerAddress,
            invoice.RecipientName,
            invoice.RecipientNif,
            invoice.RecipientAddress,
            invoice.RectifiedInvoiceId,
            rectifiedNumber,
            invoice.RectificationReason,
            invoice.Base,
            invoice.Tax,
            invoice.Total,
            lines,
            invoice.TaxBreakdown(),
            cancellation is not null,
            cancellation?.Reason,
            [.. rectifications.Select(r => new RelatedInvoice(r.Id, r.FullNumber, r.Total))],
            qr,
            alta.Hash,
            alta.Sequence,
            submission?.Status ?? SubmissionStatus.Pending,
            gateway is not null && gateway.Mode != AeatMode.Disabled,
            submission?.Message,
            submission?.Csv);
    }

    // ───────────── Integridad de la cadena ─────────────

    /// <summary>
    /// Recorre toda la cadena recalculando cada huella: detecta registros alterados, eliminados o reordenados. Solo lee.
    /// </summary>
    public async Task<ChainVerification> VerifyChainAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageConfiguration);

        var records = await data.ToListAsync(data.NoTracking(data.BillingRecords).OrderBy(r => r.Sequence), cancellationToken);
        var head = await data.FirstOrDefaultAsync(data.NoTracking(data.BillingChainHeads), cancellationToken);
        var result = Verify(records, head);
        audit.Write(AuditActions.BillingChainVerified, "billing", "chain", new { result.Ok, result.Records, result.BrokenAtSequence });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }

    public static ChainVerification Verify(IReadOnlyList<BillingRecord> records, BillingChainHead? head)
    {
        ArgumentNullException.ThrowIfNull(records);
        var previous = string.Empty;
        long expected = 1;
        foreach (var r in records)
        {
            if (r.Sequence != expected)
            {
                return new ChainVerification(false, records.Count, expected, $"Falta el registro {expected} o la numeración está alterada.");
            }

            if (r.PreviousHash != previous)
            {
                return new ChainVerification(false, records.Count, r.Sequence, "La huella anterior no coincide con la del registro precedente.");
            }

            if (!string.Equals(r.RecomputeHash(), r.Hash, StringComparison.Ordinal))
            {
                return new ChainVerification(false, records.Count, r.Sequence, "Los datos del registro no coinciden con su huella (alterado).");
            }

            previous = r.Hash;
            expected++;
        }

        if (head is not null && (head.LastSequence != records.Count || head.LastHash != previous))
        {
            return new ChainVerification(false, records.Count, head.LastSequence, "La cabeza de la cadena no coincide con el último registro (faltan registros al final).");
        }

        return new ChainVerification(true, records.Count, null, null);
    }

    public async Task<BillingOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.IssueInvoices);
        var head = await data.FirstOrDefaultAsync(data.NoTracking(data.BillingChainHeads), cancellationToken);
        var pending = await data.ToListAsync(data.NoTracking(data.BillingSubmissions).Where(s => s.Status == SubmissionStatus.Pending).Select(s => s.RecordSequence), cancellationToken);
        var settings = await LoadSettingsAsync(cancellationToken);
        return new BillingOverview(head?.LastSequence ?? 0, pending.Count, head?.LastSequence ?? 0, settings.IssuerComplete);
    }

    // ───────────── Apoyo ─────────────

    private async Task<BillingRecord> AppendAltaAsync(Invoice invoice, DateTimeOffset local, CancellationToken cancellationToken)
    {
        var head = await RequireHeadAsync(cancellationToken);
        var record = BillingRecord.CreateAlta(invoice, head.LastSequence + 1, head.LastHash, local, user.UserId);
        head.Advance(record);
        data.Add(invoice);
        data.Add(record);
        data.Add(BillingSubmission.Pending(record, time.GetUtcNow()));
        return record;
    }

    private async Task<BillingChainHead> RequireHeadAsync(CancellationToken cancellationToken) =>
        await data.FirstOrDefaultAsync(data.BillingChainHeads.Where(h => h.Id == BillingChainHead.SingleId), cancellationToken)
            ?? throw new DomainException("Falta la cabeza de la cadena de facturación: aplique las migraciones de la base de datos.");

    private async Task<int> NextNumberAsync(string series, CancellationToken cancellationToken)
    {
        var last = await data.ToListAsync(
            data.NoTracking(data.Invoices).Where(i => i.Series == series).OrderByDescending(i => i.Number).Select(i => i.Number).Take(1),
            cancellationToken);
        return (last.Count == 0 ? 0 : last[0]) + 1;
    }

    private async Task<bool> IsCancelledAsync(long invoiceId, CancellationToken cancellationToken) =>
        await data.AnyAsync(data.BillingRecords.Where(r => r.InvoiceId == invoiceId && r.Type == BillingRecordType.Anulacion), cancellationToken);

    /// <summary>Factura vigente de la comanda: original no anulada y no rectificada por completo.</summary>
    private async Task<Invoice?> FindLiveInvoiceAsync(long orderId, CancellationToken cancellationToken)
    {
        var invoices = await data.ToListAsync(data.NoTracking(data.Invoices).Where(i => i.OrderId == orderId), cancellationToken);
        var ids = invoices.Select(i => i.Id).ToList();
        var cancelled = (await data.ToListAsync(
            data.NoTracking(data.BillingRecords).Where(r => ids.Contains(r.InvoiceId) && r.Type == BillingRecordType.Anulacion).Select(r => r.InvoiceId),
            cancellationToken)).ToHashSet();

        foreach (var original in invoices.Where(i => i.Kind != InvoiceKind.Rectificative && !cancelled.Contains(i.Id)))
        {
            var rectified = invoices.Where(i => i.RectifiedInvoiceId == original.Id).Sum(i => i.Total);
            if (original.Total + rectified != 0)
            {
                return original;
            }
        }

        return null;
    }

    private async Task<Dictionary<long, RectifiedAmounts>> RectifiedSoFarAsync(long originalId, CancellationToken cancellationToken)
    {
        var rectifications = await data.ToListAsync(data.NoTracking(data.Invoices).Where(i => i.RectifiedInvoiceId == originalId), cancellationToken);
        return SumRectified(rectifications);
    }

    private static Dictionary<long, RectifiedAmounts> SumRectified(IEnumerable<Invoice> rectifications) =>
        rectifications
            .SelectMany(r => r.Lines)
            .Where(l => l.RectifiesLineId is not null)
            .GroupBy(l => l.RectifiesLineId!.Value)
            .ToDictionary(g => g.Key, g => new RectifiedAmounts(-g.Sum(l => l.Quantity), -g.Sum(l => l.Total), -g.Sum(l => l.Base), -g.Sum(l => l.Tax)));

    private async Task<InvoiceSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var all = (await data.ToListAsync(data.NoTracking(data.AppSettings).Where(s => s.Key.StartsWith("invoice.")), cancellationToken)).ToDictionary(s => s.Key, s => s.Value);
        var limit = all.TryGetValue(AppSettingKeys.InvoiceSimplifiedLimit, out var raw) && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : InvoiceSettings.DefaultSimplifiedLimit;
        return new InvoiceSettings(
            all.GetValueOrDefault(AppSettingKeys.InvoiceIssuerName, string.Empty),
            all.GetValueOrDefault(AppSettingKeys.InvoiceIssuerNif, string.Empty),
            all.GetValueOrDefault(AppSettingKeys.InvoiceIssuerAddress, string.Empty),
            limit,
            all.GetValueOrDefault(AppSettingKeys.InvoiceQrBaseUrl, gateway?.Mode == AeatMode.Production ? BillingHash.ProductionQrUrl : BillingHash.PreProductionQrUrl));
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

    private async Task<bool> WasProcessedAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var key = requestId.ToString("N");
        return await data.AnyAsync(data.IdempotencyRecords.Where(r => r.Key == key), cancellationToken);
    }

    private async Task CompleteAsync(Guid requestId, string operation, CancellationToken cancellationToken)
    {
        data.Add(IdempotencyRecord.Create(requestId, operation, time.GetUtcNow()));
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateEntryException ex)
        {
            // Otra emisión simultánea tomó el mismo número, o la misma petición ya se estaba procesando: se trata como un
            // conflicto de concurrencia para que el ejecutor repita el caso de uso con el estado ya confirmado.
            throw new ConcurrencyConflictException("Otra operación de facturación se confirmó a la vez. Se reintenta.", ex);
        }
    }
}
