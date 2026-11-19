using Microsoft.EntityFrameworkCore;
using TPVBar.Application.Abstractions;
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

internal sealed class EfAppData(TpvBarDbContext db) : IAppData
{
    public IQueryable<Zone> Zones => db.Zones;

    public IQueryable<DiningTable> Tables => db.Tables;

    public IQueryable<Category> Categories => db.Categories;

    public IQueryable<TaxRate> TaxRates => db.TaxRates;

    public IQueryable<Product> Products => db.Products;

    public IQueryable<Order> Orders => db.Orders.Include(o => o.Lines);

    public IQueryable<IdempotencyRecord> IdempotencyRecords => db.IdempotencyRecords;

    public IQueryable<KitchenTicket> KitchenTickets => db.KitchenTickets.Include(t => t.Lines);

    public IQueryable<PaymentMethod> PaymentMethods => db.PaymentMethods;

    public IQueryable<CashSession> CashSessions => db.CashSessions;

    public IQueryable<CashMovement> CashMovements => db.CashMovements;

    public IQueryable<Payment> Payments => db.Payments;

    public IQueryable<PaymentRefund> PaymentRefunds => db.PaymentRefunds;

    public IQueryable<StockMovement> StockMovements => db.StockMovements;

    public IQueryable<Invoice> Invoices => db.Invoices.Include(i => i.Lines);

    public IQueryable<BillingRecord> BillingRecords => db.BillingRecords;

    public IQueryable<BillingChainHead> BillingChainHeads => db.BillingChainHeads;

    public IQueryable<BillingSubmission> BillingSubmissions => db.BillingSubmissions;

    public IQueryable<AppSetting> AppSettings => db.AppSettings;

    public IQueryable<AppUser> Users => db.Users;

    public IQueryable<Printer> Printers => db.Printers;

    public IQueryable<PrinterRoute> PrinterRoutes => db.PrinterRoutes;

    public IQueryable<PrintJob> PrintJobs => db.PrintJobs;

    public IQueryable<PrintAgentHeartbeat> PrintAgentHeartbeats => db.PrintAgentHeartbeats;

    public void Add<T>(T entity)
        where T : class => db.Add(entity);

    public IQueryable<T> NoTracking<T>(IQueryable<T> query)
        where T : class => query.AsNoTracking();

    public Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken) =>
        query.ToListAsync(cancellationToken);

    public Task<T?> FirstOrDefaultAsync<T>(IQueryable<T> query, CancellationToken cancellationToken) =>
        query.FirstOrDefaultAsync(cancellationToken);

    public Task<bool> AnyAsync<T>(IQueryable<T> query, CancellationToken cancellationToken) =>
        query.AnyAsync(cancellationToken);
}
