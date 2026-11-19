using System.Reflection;
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

namespace TPVBar.Application.Tests.Fakes;

/// <summary>Datos en memoria con LINQ to Objects. Asigna identificadores al añadir, como haría la base de datos.</summary>
internal sealed class FakeAppData : IAppData
{
    private readonly Dictionary<Type, long> _nextIds = [];

    public List<Zone> ZoneList { get; } = [];

    public List<DiningTable> TableList { get; } = [];

    public List<Category> CategoryList { get; } = [];

    public List<TaxRate> TaxRateList { get; } = [];

    public List<Product> ProductList { get; } = [];

    public List<Order> OrderList { get; } = [];

    public List<KitchenTicket> TicketList { get; } = [];

    public List<IdempotencyRecord> IdempotencyList { get; } = [];

    public List<Printer> PrinterList { get; } = [];

    public List<PrinterRoute> RouteList { get; } = [];

    public List<PrintJob> JobList { get; } = [];

    public List<PrintAgentHeartbeat> HeartbeatList { get; } = [];

    public List<PrintJobAttempt> AttemptList { get; } = [];

    public List<PaymentMethod> MethodList { get; } = [];

    public List<CashSession> SessionList { get; } = [];

    public List<CashMovement> MovementList { get; } = [];

    public List<Payment> PaymentList { get; } = [];

    public List<PaymentRefund> RefundList { get; } = [];

    public List<AppUser> UserList { get; } = [];

    public List<StockMovement> StockMovementList { get; } = [];

    public List<AppSetting> SettingList { get; } = [];

    public List<Invoice> InvoiceList { get; } = [];

    public List<BillingRecord> RecordList { get; } = [];

    public List<BillingChainHead> HeadList { get; } = [BillingChainHead.Initial()];

    public List<BillingSubmission> SubmissionList { get; } = [];

    public IQueryable<Invoice> Invoices => InvoiceList.AsQueryable();

    public IQueryable<BillingRecord> BillingRecords => RecordList.AsQueryable();

    public IQueryable<BillingChainHead> BillingChainHeads => HeadList.AsQueryable();

    public IQueryable<BillingSubmission> BillingSubmissions => SubmissionList.AsQueryable();

    public IQueryable<StockMovement> StockMovements => StockMovementList.AsQueryable();

    public IQueryable<AppSetting> AppSettings => SettingList.AsQueryable();

    public IQueryable<PaymentMethod> PaymentMethods => MethodList.AsQueryable();

    public IQueryable<CashSession> CashSessions => SessionList.AsQueryable();

    public IQueryable<CashMovement> CashMovements => MovementList.AsQueryable();

    public IQueryable<Payment> Payments => PaymentList.AsQueryable();

    public IQueryable<PaymentRefund> PaymentRefunds => RefundList.AsQueryable();

    public IQueryable<AppUser> Users => UserList.AsQueryable();

    public IQueryable<KitchenTicket> KitchenTickets => TicketList.AsQueryable();

    public IQueryable<Printer> Printers => PrinterList.AsQueryable();

    public IQueryable<PrinterRoute> PrinterRoutes => RouteList.AsQueryable();

    public IQueryable<PrintJob> PrintJobs => JobList.AsQueryable();

    public IQueryable<PrintAgentHeartbeat> PrintAgentHeartbeats => HeartbeatList.AsQueryable();

    public IQueryable<Zone> Zones => ZoneList.AsQueryable();

    public IQueryable<DiningTable> Tables => TableList.AsQueryable();

    public IQueryable<Category> Categories => CategoryList.AsQueryable();

    public IQueryable<TaxRate> TaxRates => TaxRateList.AsQueryable();

    public IQueryable<Product> Products => ProductList.AsQueryable();

    public IQueryable<Order> Orders => OrderList.AsQueryable();

    public IQueryable<IdempotencyRecord> IdempotencyRecords => IdempotencyList.AsQueryable();

    public void Add<T>(T entity)
        where T : class
    {
        AssignId(entity);
        switch (entity)
        {
            case Zone z: ZoneList.Add(z); break;
            case DiningTable t: TableList.Add(t); break;
            case Category c: CategoryList.Add(c); break;
            case TaxRate r: TaxRateList.Add(r); break;
            case Product p: ProductList.Add(p); break;
            case Order o: OrderList.Add(o); break;
            case KitchenTicket k: TicketList.Add(k); break;
            case IdempotencyRecord i: IdempotencyList.Add(i); break;
            case Printer p: PrinterList.Add(p); break;
            case PrinterRoute r: RouteList.Add(r); break;
            case PrintJob j: JobList.Add(j); break;
            case PrintAgentHeartbeat h: HeartbeatList.Add(h); break;
            case PrintJobAttempt a: AttemptList.Add(a); break;
            case PaymentMethod pm: MethodList.Add(pm); break;
            case CashSession cs: SessionList.Add(cs); break;
            case CashMovement cm: MovementList.Add(cm); break;
            case Payment pay: PaymentList.Add(pay); break;
            case PaymentRefund pr: RefundList.Add(pr); break;
            case StockMovement sm: StockMovementList.Add(sm); break;
            case AppSetting setting: SettingList.Add(setting); break;
            case Invoice inv: InvoiceList.Add(inv); break;
            case BillingRecord rec:
                RecordList.Add(rec);
                if (rec.Invoice is not null)
                {
                    typeof(BillingRecord).GetProperty(nameof(BillingRecord.InvoiceId))!.SetValue(rec, rec.Invoice.Id);
                }

                break;
            case BillingSubmission sub: SubmissionList.Add(sub); break;
            default: throw new NotSupportedException(typeof(T).Name);
        }
    }

    public IQueryable<T> NoTracking<T>(IQueryable<T> query)
        where T : class => query;

    public Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken) => Task.FromResult(query.ToList());

    public Task<T?> FirstOrDefaultAsync<T>(IQueryable<T> query, CancellationToken cancellationToken) => Task.FromResult(query.FirstOrDefault());

    public Task<bool> AnyAsync<T>(IQueryable<T> query, CancellationToken cancellationToken) => Task.FromResult(query.Any());

    /// <summary>Los hijos (líneas de comanda) también reciben identificador al "guardarse".</summary>
    public void AssignLineIds()
    {
        foreach (var line in OrderList.SelectMany(o => o.Lines).Where(l => l.Id == 0))
        {
            AssignId(line);
        }

        foreach (var line in InvoiceList.SelectMany(i => i.Lines).Where(l => l.Id == 0))
        {
            AssignId(line);
        }

        // Las líneas de ticket apuntan a líneas de comanda que ya tienen identificador.
        foreach (var line in TicketList.SelectMany(t => t.Lines).Where(l => l.Id == 0))
        {
            AssignId(line);
        }
    }

    private void AssignId(object entity)
    {
        var property = entity.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
        if (property is null || Convert.ToInt64(property.GetValue(entity), System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            return;
        }

        var type = entity.GetType();
        _nextIds[type] = _nextIds.GetValueOrDefault(type) + 1;
        property.SetValue(entity, Convert.ChangeType(_nextIds[type], property.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
    }
}
