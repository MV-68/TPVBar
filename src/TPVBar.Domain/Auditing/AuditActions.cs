namespace TPVBar.Domain.Auditing;

/// <summary>Códigos estables de acciones auditadas (se guardan en base de datos).</summary>
public static class AuditActions
{
    public const string LoginSucceeded = "auth.login.succeeded";
    public const string LoginFailed = "auth.login.failed";
    public const string LoginBlocked = "auth.login.blocked";
    public const string AccountLocked = "auth.account.locked";
    public const string Logout = "auth.logout";
    public const string UserCreated = "user.created";
    public const string DatabaseMigrated = "system.database.migrated";

    public const string OrderOpened = "order.opened";
    public const string OrderLineAdded = "order.line.added";
    public const string OrderLineQuantityChanged = "order.line.quantity_changed";
    public const string OrderLineRemoved = "order.line.removed";
    public const string OrderLineVoided = "order.line.voided";
    public const string OrderSentToKitchen = "order.sent";
    public const string OrderBillRequested = "order.bill_requested";
    public const string OrderReopened = "order.reopened";
    public const string OrderCancelled = "order.cancelled";

    public const string StockMovementRegistered = "stock.movement";
    public const string StockSettingChanged = "stock.setting.changed";
    public const string ReportExported = "report.exported";

    public const string CashOpened = "cash.opened";
    public const string CashClosed = "cash.closed";
    public const string CashMovementRegistered = "cash.movement";
    public const string PaymentRegistered = "payment.registered";
    public const string PaymentRefunded = "payment.refunded";
    public const string ReceiptReprinted = "receipt.reprinted";
    public const string PaymentMethodChanged = "payment.method.changed";

    public const string InvoiceIssued = "invoice.issued";
    public const string InvoiceRectified = "invoice.rectified";
    public const string InvoiceCancelled = "invoice.cancelled";
    public const string InvoiceSettingsChanged = "invoice.settings.changed";
    public const string BillingSubmitted = "billing.submitted";
    public const string BillingSubmissionFailed = "billing.submission.failed";
    public const string BillingSubmissionRequeued = "billing.submission.requeued";
    public const string BillingSystemChanged = "billing.system.changed";
    public const string BillingRecordCorrected = "billing.record.corrected";
    public const string BillingChainVerified = "invoice.chain.verified";
    public const string InvoiceReportExported = "invoice.report.exported";

    public const string KitchenTicketAdvanced = "kitchen.ticket.advanced";
    public const string PrintJobFailed = "print.job.failed";
    public const string PrintJobRetried = "print.job.retried";
    public const string PrintJobCancelled = "print.job.cancelled";
    public const string PrintJobReprinted = "print.job.reprinted";
    public const string PrintTestRequested = "print.test.requested";
    public const string PrinterChanged = "print.printer.changed";

    public const string CatalogCreated = "catalog.created";
    public const string CatalogUpdated = "catalog.updated";
    public const string PriceChanged = "catalog.price_changed";
}
