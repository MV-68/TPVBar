using System.Globalization;
using System.Text;
using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Reports;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Invoicing;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Invoicing;

public sealed record BookRow(
    string Number,
    DateOnly Date,
    string TypeCode,
    string? RecipientNif,
    string? RecipientName,
    decimal TaxRate,
    decimal Base,
    decimal Tax,
    decimal Total,
    string? Rectifies,
    bool Cancelled);

/// <summary>Informes calculados siempre sobre las facturas emitidas (no sobre las comandas): IVA por tipo y libro de facturas.</summary>
public sealed class InvoiceReportService(IAppData data, ICurrentUser user, AuditWriter audit, IUnitOfWork unitOfWork)
{
    public async Task<VatReport> GetVatAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        user.Require(Permission.ViewReports);
        var rows = await GetBookAsync(from, to, audited: false, cancellationToken);
        var vat = rows.Where(r => !r.Cancelled)
            .GroupBy(r => r.TaxRate)
            .OrderBy(g => g.Key)
            .Select(g => new VatRow(g.Key, g.Sum(r => r.Base), g.Sum(r => r.Tax), g.Sum(r => r.Total)))
            .ToList();
        return new VatReport(from, to, vat, rows.Where(r => !r.Cancelled).Select(r => r.Number).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Una fila por factura y tipo de IVA. Las facturas anuladas aparecen con importes a cero.</summary>
    public async Task<IReadOnlyList<BookRow>> GetBookAsync(DateOnly from, DateOnly to, bool audited, CancellationToken cancellationToken)
    {
        user.Require(Permission.ViewReports);
        if (to < from)
        {
            throw new DomainException("La fecha final no puede ser anterior a la inicial.");
        }

        var invoices = await data.ToListAsync(
            data.NoTracking(data.Invoices).Where(i => i.IssueDate >= from && i.IssueDate <= to).OrderBy(i => i.IssueDate).ThenBy(i => i.Id),
            cancellationToken);
        var ids = invoices.Select(i => i.Id).ToList();
        var cancelled = (await data.ToListAsync(
            data.NoTracking(data.BillingRecords).Where(r => ids.Contains(r.InvoiceId) && r.Type == BillingRecordType.Anulacion).Select(r => r.InvoiceId),
            cancellationToken)).ToHashSet();
        var originals = invoices.Where(i => i.RectifiedInvoiceId is not null).Select(i => i.RectifiedInvoiceId!.Value).Distinct().ToList();
        var originalNumbers = (await data.ToListAsync(data.NoTracking(data.Invoices).Where(i => originals.Contains(i.Id)), cancellationToken))
            .ToDictionary(i => i.Id, i => i.FullNumber);

        var rows = new List<BookRow>();
        foreach (var invoice in invoices)
        {
            var isCancelled = cancelled.Contains(invoice.Id);
            var rectifies = invoice.RectifiedInvoiceId is { } id && originalNumbers.TryGetValue(id, out var n) ? n : null;
            foreach (var b in invoice.TaxBreakdown())
            {
                rows.Add(new BookRow(
                    invoice.FullNumber,
                    invoice.IssueDate,
                    invoice.TypeCode,
                    invoice.RecipientNif,
                    invoice.RecipientName,
                    b.TaxRate,
                    isCancelled ? 0 : b.Base,
                    isCancelled ? 0 : b.Tax,
                    isCancelled ? 0 : b.Total,
                    rectifies,
                    isCancelled));
            }
        }

        if (audited)
        {
            audit.Write(AuditActions.InvoiceReportExported, "report", "invoice-book", new { from, to, rows = rows.Count });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return rows;
    }
}

/// <summary>CSV para el asesor: separador «;», decimales con coma, UTF-8 con BOM (mismo formato que los demás informes).</summary>
public static class InvoiceCsv
{
    private static readonly CultureInfo Spanish = new("es-ES");

    public static string Book(IReadOnlyList<BookRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        Row(sb, "Número", "Fecha", "Tipo", "NIF cliente", "Cliente", "Tipo IVA %", "Base imponible", "Cuota IVA", "Total", "Rectifica a", "Estado");
        foreach (var r in rows)
        {
            Row(
                sb,
                ReportCsv.Text(r.Number),
                r.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                r.TypeCode,
                ReportCsv.Text(r.RecipientNif ?? string.Empty),
                ReportCsv.Text(r.RecipientName ?? string.Empty),
                r.TaxRate.ToString("0.##", Spanish),
                Money(r.Base),
                Money(r.Tax),
                Money(r.Total),
                ReportCsv.Text(r.Rectifies ?? string.Empty),
                r.Cancelled ? "Anulada" : "Emitida");
        }

        Row(sb, "TOTAL", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, Money(rows.Sum(r => r.Base)), Money(rows.Sum(r => r.Tax)), Money(rows.Sum(r => r.Total)), string.Empty, string.Empty);
        return sb.ToString();
    }

    public static string Vat(VatReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        Row(sb, "Tipo IVA %", "Base imponible", "Cuota IVA", "Total");
        foreach (var r in report.Rows)
        {
            Row(sb, r.TaxRate.ToString("0.##", Spanish), Money(r.Base), Money(r.Tax), Money(r.Total));
        }

        Row(sb, "TOTAL", Money(report.Base), Money(report.Tax), Money(report.Total));
        return sb.ToString();
    }

    private static string Money(decimal value) => value.ToString("0.00", Spanish);

    private static void Row(StringBuilder sb, params string[] cells) => sb.Append(string.Join(';', cells)).Append("\r\n");
}
