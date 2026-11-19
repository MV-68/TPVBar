using TPVBar.Domain.Invoicing;

namespace TPVBar.Application.Features.Invoicing;

public sealed record InvoiceSettings(string IssuerName, string IssuerNif, string IssuerAddress, decimal SimplifiedLimit, string QrBaseUrl)
{
    public const decimal DefaultSimplifiedLimit = 400m;

    public bool IssuerComplete => !string.IsNullOrWhiteSpace(IssuerName) && SpanishTaxId.IsValid(IssuerNif) && !string.IsNullOrWhiteSpace(IssuerAddress);
}

public sealed record IssueInvoiceRequest(long OrderId, InvoiceKind Kind, InvoiceRecipient? Recipient, Guid RequestId);

public sealed record IssueInvoiceResult(long InvoiceId, string FullNumber, bool AlreadyProcessed);

public sealed record RectifyLineRequest(long InvoiceLineId, decimal Quantity);

public sealed record RectifyInvoiceRequest(long InvoiceId, string Reason, IReadOnlyList<RectifyLineRequest> Lines, Guid RequestId);

public sealed record InvoiceSummary(
    long Id,
    string FullNumber,
    InvoiceKind Kind,
    string TypeCode,
    DateOnly IssueDate,
    string? RecipientName,
    decimal Total,
    long OrderId,
    bool Cancelled,
    SubmissionStatus Submission);

public sealed record InvoiceFilter(DateOnly? From, DateOnly? To, string? Text, InvoiceKind? Kind);

public sealed record InvoiceLineView(long Id, string Description, decimal Quantity, decimal UnitPrice, decimal TaxRate, decimal Base, decimal Tax, decimal Total, decimal RectifiableQuantity);

public sealed record InvoiceView(
    long Id,
    string FullNumber,
    InvoiceKind Kind,
    string TypeCode,
    DateOnly IssueDate,
    DateTimeOffset IssuedAt,
    long OrderId,
    string IssuerName,
    string IssuerNif,
    string IssuerAddress,
    string? RecipientName,
    string? RecipientNif,
    string? RecipientAddress,
    long? RectifiedInvoiceId,
    string? RectifiedInvoiceNumber,
    string? RectificationReason,
    decimal Base,
    decimal Tax,
    decimal Total,
    IReadOnlyList<InvoiceLineView> Lines,
    IReadOnlyList<TaxBreakdownItem> TaxBreakdown,
    bool Cancelled,
    string? CancellationReason,
    IReadOnlyList<RelatedInvoice> Rectifications,
    string QrUrl,
    string RecordHash,
    long RecordSequence,
    SubmissionStatus Submission,
    bool ShowVeriFactuLegend,
    string? SubmissionMessage,
    string? SubmissionCsv);

public sealed record RelatedInvoice(long Id, string FullNumber, decimal Total);

public sealed record ChainVerification(bool Ok, long Records, long? BrokenAtSequence, string? Problem);

public sealed record BillingOverview(long Records, long Pending, long LastSequence, bool IssuerComplete);

public sealed record VatRow(decimal TaxRate, decimal Base, decimal Tax, decimal Total);

public sealed record VatReport(DateOnly From, DateOnly To, IReadOnlyList<VatRow> Rows, int InvoiceCount)
{
    public decimal Base => Rows.Sum(r => r.Base);

    public decimal Tax => Rows.Sum(r => r.Tax);

    public decimal Total => Rows.Sum(r => r.Total);
}
