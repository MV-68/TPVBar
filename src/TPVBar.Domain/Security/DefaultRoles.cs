using System.Collections.Frozen;

namespace TPVBar.Domain.Security;

/// <summary>
/// Roles iniciales y sus permisos por defecto. Los permisos se asignan SOLO al crear el rol:
/// después son configuración editable y un reinicio nunca debe revertirla.
/// </summary>
public static class DefaultRoles
{
    public const string Administrator = "Administrador";
    public const string Manager = "Encargado";
    public const string Waiter = "Camarero";
    public const string Kitchen = "Cocina";
    public const string Cashier = "Caja";

    public static IReadOnlyList<string> Names { get; } = [Administrator, Manager, Waiter, Kitchen, Cashier];

    public static FrozenDictionary<string, IReadOnlyList<Permission>> Permissions { get; } =
        new Dictionary<string, IReadOnlyList<Permission>>
        {
            [Administrator] = Enum.GetValues<Permission>(),
            [Manager] =
            [
                Permission.OpenCloseCash, Permission.ApplyDiscounts, Permission.VoidLines, Permission.ReopenTables,
                Permission.ViewReports, Permission.ModifyPrices, Permission.PerformRefunds, Permission.AdjustStock,
                Permission.ViewAudit, Permission.ManageCatalog, Permission.TakeOrders, Permission.UseKitchen,
                Permission.ManagePrinting, Permission.TakePayments, Permission.IssueInvoices, Permission.RectifyInvoices,
            ],
            [Waiter] = [Permission.TakeOrders, Permission.TakePayments],
            [Kitchen] = [Permission.UseKitchen],
            [Cashier] = [Permission.OpenCloseCash, Permission.PerformRefunds, Permission.TakeOrders, Permission.TakePayments, Permission.IssueInvoices],
        }.ToFrozenDictionary();
}
