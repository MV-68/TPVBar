using Microsoft.EntityFrameworkCore;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Cash;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Entities;
using TPVBar.Domain.Floor;
using TPVBar.Domain.Inventory;
using TPVBar.Domain.Invoicing;
using TPVBar.Domain.Printing;
using TPVBar.Domain.Sales;

namespace TPVBar.Infrastructure.Persistence;

public sealed class TpvBarDbContext(DbContextOptions<TpvBarDbContext> options) : DbContext(options)
{
    public const string Schema = "tpv";

    public DbSet<AppUser> Users => Set<AppUser>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<Zone> Zones => Set<Zone>();

    public DbSet<DiningTable> Tables => Set<DiningTable>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<TaxRate> TaxRates => Set<TaxRate>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<KitchenTicket> KitchenTickets => Set<KitchenTicket>();

    public DbSet<KitchenTicketLine> KitchenTicketLines => Set<KitchenTicketLine>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<PaymentMethod> PaymentMethods => Set<PaymentMethod>();

    public DbSet<CashSession> CashSessions => Set<CashSession>();

    public DbSet<CashMovement> CashMovements => Set<CashMovement>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<PaymentRefund> PaymentRefunds => Set<PaymentRefund>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();

    public DbSet<BillingRecord> BillingRecords => Set<BillingRecord>();

    public DbSet<BillingChainHead> BillingChainHeads => Set<BillingChainHead>();

    public DbSet<BillingSubmission> BillingSubmissions => Set<BillingSubmission>();

    public DbSet<Printer> Printers => Set<Printer>();

    public DbSet<PrinterRoute> PrinterRoutes => Set<PrinterRoute>();

    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();

    public DbSet<PrintJobAttempt> PrintJobAttempts => Set<PrintJobAttempt>();

    public DbSet<PrintAgentHeartbeat> PrintAgentHeartbeats => Set<PrintAgentHeartbeat>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TpvBarDbContext).Assembly);
    }
}
