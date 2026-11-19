using TPVBar.Domain.Security;

namespace TPVBar.Web.Security;

public static class Policies
{
    private const string Prefix = "perm:";

    // Constantes para usar en atributos [Authorize(Policy = ...)]; deben coincidir con For(permiso).
    public const string TakeOrders = Prefix + nameof(Permission.TakeOrders);
    public const string ManageCatalog = Prefix + nameof(Permission.ManageCatalog);
    public const string TakePayments = Prefix + nameof(Permission.TakePayments);
    public const string AdjustStock = Prefix + nameof(Permission.AdjustStock);
    public const string ViewReports = Prefix + nameof(Permission.ViewReports);
    public const string ManageConfiguration = Prefix + nameof(Permission.ManageConfiguration);
    public const string IssueInvoices = Prefix + nameof(Permission.IssueInvoices);
    public const string RectifyInvoices = Prefix + nameof(Permission.RectifyInvoices);

    /// <summary>Quien cobra o quien abre/cierra caja puede ver la pantalla de caja.</summary>
    public const string CashAccess = "cash:access";
    public const string UseKitchen = Prefix + nameof(Permission.UseKitchen);
    public const string ManagePrinting = Prefix + nameof(Permission.ManagePrinting);
    /// <summary>Nombre de la política de autorización que exige el permiso indicado.</summary>
    public static string For(Permission permission) => Prefix + permission;
}
