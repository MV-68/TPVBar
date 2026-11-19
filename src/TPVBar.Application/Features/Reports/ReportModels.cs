using System.Globalization;
using TPVBar.Application.Features.Inventory;

namespace TPVBar.Application.Features.Reports;

public enum Granularity
{
    Day = 1,
    Week = 2,
    Month = 3,
}

/// <summary>Rango de fechas LOCALES (Europe/Madrid), ambos extremos incluidos.</summary>
public sealed record ReportRange(DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber + 1;
}

/// <summary>
/// Totales de ventas. Definiciones (no se mezclan magnitudes distintas):
/// <list type="bullet">
/// <item><b>Ticket</b>: comanda cobrada cuyo cierre cae en el rango.</item>
/// <item><b>Ventas brutas</b>: suma de totales de tickets antes de descuentos. <b>Ventas</b>: después de descuentos, con IVA incluido.</item>
/// <item><b>Base</b> e <b>IVA</b>: desglose de las ventas (no incluye devoluciones: no se desglosan por tipo).</item>
/// <item><b>Devoluciones</b>: dinero devuelto en el rango (por fecha de la devolución). <b>Neto</b> = ventas − devoluciones.</item>
/// </list>
/// Los <i>cobros</i> (dinero recibido por método) son otra magnitud y van en su propia sección.
/// </summary>
public sealed record SalesTotals(
    int Tickets,
    decimal GrossSales,
    decimal Discounts,
    decimal Sales,
    decimal BaseAmount,
    decimal Tax,
    decimal Refunds,
    decimal NetAfterRefunds)
{
    public decimal AverageTicket => Tickets == 0 ? 0 : Math.Round(Sales / Tickets, 2, MidpointRounding.AwayFromZero);
}

public sealed record DayRow(DateOnly Day, int Tickets, decimal Sales);

public sealed record PeriodRow(string Label, DateOnly Start, int Tickets, decimal Sales)
{
    public decimal AverageTicket => Tickets == 0 ? 0 : Math.Round(Sales / Tickets, 2, MidpointRounding.AwayFromZero);
}

public sealed record HourRow(int Hour, int Tickets, decimal Sales);

/// <summary>Dinero recibido y devuelto por método de pago (cobros, no ventas).</summary>
public sealed record MethodRow(string Name, bool IsCash, decimal Payments, decimal Refunds)
{
    public decimal Net => Payments - Refunds;
}

public sealed record ProductSalesRow(int? ProductId, string Name, string Category, decimal Quantity, decimal Amount);

public sealed record CategorySalesRow(string Category, decimal Quantity, decimal Amount);

public sealed record VoidRow(DateTimeOffset At, string Product, decimal Quantity, decimal Amount, string Reason, string By);

public sealed record CashDifferenceRow(long SessionId, DateTimeOffset ClosedAt, decimal Expected, decimal Counted, decimal Difference);

public sealed record SalesReport(
    ReportRange Range,
    SalesTotals Totals,
    IReadOnlyList<DayRow> Days,
    IReadOnlyList<HourRow> Hours,
    IReadOnlyList<MethodRow> Methods,
    IReadOnlyList<ProductSalesRow> Products,
    IReadOnlyList<CategorySalesRow> Categories,
    int VoidCount,
    decimal VoidAmount,
    IReadOnlyList<VoidRow> Voids,
    IReadOnlyList<CashDifferenceRow> CashDifferences,
    IReadOnlyList<StockRow> LowStock)
{
    public decimal CashDifferenceTotal => CashDifferences.Sum(c => c.Difference);

    public decimal TotalPayments => Methods.Sum(m => m.Payments);

    /// <summary>Agrupa la serie diaria por semana (lunes a domingo) o por mes natural.</summary>
    public IReadOnlyList<PeriodRow> Series(Granularity granularity)
    {
        var culture = new CultureInfo("es-ES");
        return granularity switch
        {
            Granularity.Day => [.. Days.Select(d => new PeriodRow(d.Day.ToString("dd/MM/yyyy", culture), d.Day, d.Tickets, d.Sales))],
            Granularity.Week => [.. Days.GroupBy(d => d.Day.AddDays(-(((int)d.Day.DayOfWeek + 6) % 7)))
                .OrderBy(g => g.Key)
                .Select(g => new PeriodRow($"Semana del {g.Key.ToString("dd/MM/yyyy", culture)}", g.Key, g.Sum(d => d.Tickets), g.Sum(d => d.Sales)))],
            _ => [.. Days.GroupBy(d => new DateOnly(d.Day.Year, d.Day.Month, 1))
                .OrderBy(g => g.Key)
                .Select(g => new PeriodRow(g.Key.ToString("MMMM yyyy", culture), g.Key, g.Sum(d => d.Tickets), g.Sum(d => d.Sales)))],
        };
    }
}
