using TPVBar.Domain.Cash;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Entities;
using TPVBar.Domain.Floor;
using TPVBar.Domain.Inventory;
using TPVBar.Domain.Invoicing;
using TPVBar.Domain.Printing;
using TPVBar.Domain.Sales;

namespace TPVBar.Application.Abstractions;

/// <summary>
/// Acceso a los datos del negocio para los casos de uso, sin depender de EF Core. Las consultas se
/// escriben con LINQ y se ejecutan mediante los métodos asíncronos de esta interfaz. Los cambios se
/// confirman con <see cref="IUnitOfWork"/> en una única transacción.
/// </summary>
public interface IAppData
{
    IQueryable<Zone> Zones { get; }

    IQueryable<DiningTable> Tables { get; }

    IQueryable<Category> Categories { get; }

    IQueryable<TaxRate> TaxRates { get; }

    IQueryable<Product> Products { get; }

    /// <summary>Comandas con sus líneas, con seguimiento de cambios (para modificarlas).</summary>
    IQueryable<Order> Orders { get; }

    IQueryable<IdempotencyRecord> IdempotencyRecords { get; }

    /// <summary>Tickets de preparación con sus líneas, con seguimiento de cambios.</summary>
    IQueryable<KitchenTicket> KitchenTickets { get; }

    IQueryable<Printer> Printers { get; }

    IQueryable<PrinterRoute> PrinterRoutes { get; }

    IQueryable<PrintJob> PrintJobs { get; }

    IQueryable<PrintAgentHeartbeat> PrintAgentHeartbeats { get; }

    IQueryable<PaymentMethod> PaymentMethods { get; }

    /// <summary>Sesiones de caja, con seguimiento de cambios (para cerrarlas y registrar actividad).</summary>
    IQueryable<CashSession> CashSessions { get; }

    IQueryable<CashMovement> CashMovements { get; }

    IQueryable<Payment> Payments { get; }

    IQueryable<PaymentRefund> PaymentRefunds { get; }

    IQueryable<StockMovement> StockMovements { get; }

    /// <summary>Facturas con sus líneas (inmutables: solo se consultan y se añaden).</summary>
    IQueryable<Invoice> Invoices { get; }

    IQueryable<BillingRecord> BillingRecords { get; }

    /// <summary>Cabeza de la cadena de facturación, con seguimiento de cambios.</summary>
    IQueryable<BillingChainHead> BillingChainHeads { get; }

    IQueryable<BillingSubmission> BillingSubmissions { get; }

    /// <summary>Ajustes generales (clave/valor), con seguimiento de cambios.</summary>
    IQueryable<AppSetting> AppSettings { get; }

    /// <summary>Usuarios (solo para mostrar nombres en informes).</summary>
    IQueryable<AppUser> Users { get; }

    void Add<T>(T entity)
        where T : class;

    /// <summary>Consulta de solo lectura (sin seguimiento de cambios).</summary>
    IQueryable<T> NoTracking<T>(IQueryable<T> query)
        where T : class;

    Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken);

    Task<T?> FirstOrDefaultAsync<T>(IQueryable<T> query, CancellationToken cancellationToken);

    Task<bool> AnyAsync<T>(IQueryable<T> query, CancellationToken cancellationToken);
}
