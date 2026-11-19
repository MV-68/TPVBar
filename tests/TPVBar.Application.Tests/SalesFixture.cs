using TPVBar.Application.Common;
using TPVBar.Application.Features.Cash;
using TPVBar.Application.Features.Catalog;
using TPVBar.Application.Features.Inventory;
using TPVBar.Application.Features.Invoicing;
using TPVBar.Application.Features.Kitchen;
using TPVBar.Application.Features.Orders;
using TPVBar.Application.Features.Printing;
using TPVBar.Application.Features.Reports;
using TPVBar.Application.Tests.Fakes;
using TPVBar.Domain.Cash;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Floor;
using TPVBar.Domain.Printing;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Tests;

/// <summary>Escenario común: un bar con una zona, dos mesas, una caña (barra) y una tortilla (cocina).</summary>
internal sealed class SalesFixture
{
    public static readonly DateTimeOffset Start = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    public FakeAppData Data { get; } = new();

    public FakeStore Store { get; } = new();

    public FakeTimeProvider Time { get; } = new(Start);

    public CurrentUserAccessor User { get; } = new();

    public Guid UserId { get; } = Guid.NewGuid();

    public Zone Salon { get; }

    public DiningTable Mesa1 { get; }

    public DiningTable Mesa2 { get; }

    public Product Cana { get; }

    public Product Tortilla { get; }

    public TaxRate Reduced { get; }

    public TaxRate General { get; }

    public SalesFixture(params Permission[] permissions)
    {
        User.Set(UserId, permissions.Length == 0 ? [Permission.TakeOrders] : permissions, "test");

        Reduced = TaxRate.Create("Reducido", 10m, new DateOnly(2020, 1, 1));
        General = TaxRate.Create("General", 21m, new DateOnly(2020, 1, 1));
        Data.Add(Reduced);
        Data.Add(General);

        var bebidas = Category.Create("Bebidas", 1);
        var tapas = Category.Create("Tapas", 2);
        Data.Add(bebidas);
        Data.Add(tapas);

        Salon = Zone.Create("Salón", 1, Start);
        Data.Add(Salon);
        Mesa1 = DiningTable.Create(Salon.Id, "Mesa 1", 4, 1);
        Mesa2 = DiningTable.Create(Salon.Id, "Mesa 2", 4, 2);
        Data.Add(Mesa1);
        Data.Add(Mesa2);

        Cana = Product.Create(bebidas.Id, General.Id, "Caña", 1.80m, PreparationStation.Bar, null, null, 1, Start);
        Tortilla = Product.Create(tapas.Id, Reduced.Id, "Tortilla", 3.50m, PreparationStation.Kitchen, null, null, 2, Start);
        Data.Add(Cana);
        Data.Add(Tortilla);
    }

    public PendingOperationEvents Events { get; } = new();

    public AuditWriter Audit => new(Store, User, Time);

    public PrintEnqueuer Enqueuer => new(Data, Events, Time);

    public Printer KitchenPrinter { get; private set; } = null!;

    public Printer ReceiptPrinter { get; private set; } = null!;

    /// <summary>Configura una impresora de cocina (con rutas para cocina y barra) y otra de tickets.</summary>
    public SalesFixture WithPrinters()
    {
        KitchenPrinter = Printer.Create("Cocina", PrinterType.Kitchen, PrinterConnection.Simulated, "carpeta-cocina", 80);
        ReceiptPrinter = Printer.Create("Tickets", PrinterType.Receipt, PrinterConnection.Simulated, "carpeta-tickets", 80);
        Data.Add(KitchenPrinter);
        Data.Add(ReceiptPrinter);
        Data.Add(new PrinterRoute(PreparationStation.Kitchen, KitchenPrinter.Id));
        Data.Add(new PrinterRoute(PreparationStation.Bar, KitchenPrinter.Id));
        return this;
    }

    public PaymentMethod CashMethod { get; private set; } = null!;

    public PaymentMethod CardMethod { get; private set; } = null!;

    public PaymentMethod RefMethod { get; private set; } = null!;

    /// <summary>Métodos de pago (efectivo, tarjeta y uno que exige referencia) y un usuario con nombre visible.</summary>
    public SalesFixture WithPaymentMethods()
    {
        CashMethod = PaymentMethod.Create("CASH", "Efectivo", false, true, 1);
        CardMethod = PaymentMethod.Create("CARD", "Tarjeta", false, false, 2);
        RefMethod = PaymentMethod.Create("BIZUM", "Bizum", true, false, 3);
        Data.Add(CashMethod);
        Data.Add(CardMethod);
        Data.Add(RefMethod);

        var person = TPVBar.Domain.Entities.AppUser.Create("cajero", "Cajero de prueba", "h", Start);
        typeof(TPVBar.Domain.Entities.AppUser).GetProperty("Id")!.SetValue(person, UserId);
        Data.UserList.Add(person);
        return this;
    }

    public CashService Cash() => new(Data, new LineIdAssigningUnitOfWork(Store, Data), User, Audit, Enqueuer, Events, Time);

    public PaymentService Payments() => new(Data, new LineIdAssigningUnitOfWork(Store, Data), User, Audit, Enqueuer, Events, Time);

    public InvoiceService Invoices() => new(Data, new LineIdAssigningUnitOfWork(Store, Data), User, Audit, Time);

    public FakeAeatGateway Gateway { get; } = new();

    public BillingSubmissionService Submissions(BillingSubmissionLock? submissionLock = null) => new(Data, new LineIdAssigningUnitOfWork(Store, Data), Store, User, Gateway, Time, submissionLock);

    public InvoiceReportService InvoiceReports() => new(Data, User, Audit, new LineIdAssigningUnitOfWork(Store, Data));

    /// <summary>Datos del emisor configurados (NIF válido de ejemplo).</summary>
    public SalesFixture WithIssuer(decimal? simplifiedLimit = null)
    {
        Data.Add(new Domain.Common.AppSetting(Domain.Common.AppSettingKeys.InvoiceIssuerName, "Bar de Prueba S.L.", UserId, Start));
        Data.Add(new Domain.Common.AppSetting(Domain.Common.AppSettingKeys.InvoiceIssuerNif, "B12345674", UserId, Start));
        Data.Add(new Domain.Common.AppSetting(Domain.Common.AppSettingKeys.InvoiceIssuerAddress, "Calle Mayor 1, 28001 Madrid", UserId, Start));
        if (simplifiedLimit is { } limit)
        {
            Data.Add(new Domain.Common.AppSetting(Domain.Common.AppSettingKeys.InvoiceSimplifiedLimit, limit.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), UserId, Start));
        }

        return this;
    }

    public PaymentMethodAdminService MethodAdmin() => new(Data, Store, User, Audit);

    public FloorService Floor() => new(Data, Store, User, Audit, Events, Time);

    public StockLedger Ledger => new(Data, User, Events, Time);

    /// <summary>Activa el control de stock de un producto con existencias iniciales (con su movimiento de entrada).</summary>
    public SalesFixture WithStock(Product product, decimal quantity, decimal minimum)
    {
        product.ConfigureStock(true, minimum, "unidad", Start);
        var after = product.ApplyStockDelta(quantity, false, Start);
        Data.Add(Domain.Inventory.StockMovement.Create(product.Id, quantity, after, Domain.Inventory.StockMovementReason.Entry, null, null, "inicial", UserId, Start));
        return this;
    }

    public StockService Stock() => new(Data, new LineIdAssigningUnitOfWork(Store, Data), User, Audit, Ledger, Time);

    public ReportService Reports() => new(Data, Store, User, Audit, Time);

    public OrderService Orders() => new(Data, new LineIdAssigningUnitOfWork(Store, Data), User, Audit, Enqueuer, Ledger, Events, Time);

    public KitchenService Kitchen() => new(Data, new LineIdAssigningUnitOfWork(Store, Data), User, Audit, Events, Time);

    public PrintAdminService PrintAdmin() => new(Data, Store, User, Audit, Enqueuer, Events, Time);

    public PrintQueueService Queue() => new(Data, Store, Store, Events, Time);

    public MenuService Menu() => new(Data, User, Time);

    public CatalogService Catalog() => new(Data, Store, User, Audit, Time);

    /// <summary>Unidad de trabajo que, al guardar, asigna ids a las líneas nuevas como lo haría la base de datos.</summary>
    private sealed class LineIdAssigningUnitOfWork(FakeStore store, FakeAppData data) : Abstractions.IUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            data.AssignLineIds();
            return store.SaveChangesAsync(cancellationToken);
        }
    }
}
