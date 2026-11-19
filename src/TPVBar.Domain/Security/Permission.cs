namespace TPVBar.Domain.Security;

/// <summary>
/// Permisos configurables por operación. Se persisten por nombre (texto), por lo que
/// los nombres existentes no deben renombrarse sin una migración de datos.
/// </summary>
public enum Permission
{
    OpenCloseCash = 1,
    ApplyDiscounts = 2,
    VoidLines = 3,
    ReopenTables = 4,
    ViewReports = 5,
    ModifyPrices = 6,
    PerformRefunds = 7,
    AdjustStock = 8,
    ManageUsers = 9,
    ManageConfiguration = 10,
    ViewAudit = 11,

    /// <summary>Crear y modificar zonas, mesas, categorías, productos y tipos impositivos.</summary>
    ManageCatalog = 12,

    /// <summary>Abrir mesas, tomar comandas y enviarlas a cocina o barra.</summary>
    TakeOrders = 13,

    /// <summary>Ver la pantalla de cocina y cambiar el estado de preparación.</summary>
    UseKitchen = 14,

    /// <summary>Configurar impresoras, ver la cola de impresión, reintentar y reimprimir.</summary>
    ManagePrinting = 15,

    /// <summary>Cobrar comandas, consultar el estado de la caja y reimprimir justificantes.</summary>
    TakePayments = 16,

    /// <summary>Emitir facturas simplificadas y completas de comandas cobradas y consultar las facturas.</summary>
    IssueInvoices = 17,

    /// <summary>Emitir facturas rectificativas y anular facturas emitidas por error.</summary>
    RectifyInvoices = 18,
}
