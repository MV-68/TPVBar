using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Inventory;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Cash;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Sales;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Reports;

/// <summary>
/// Informes de ventas y actividad. Los días y las horas son los LOCALES del establecimiento (Europe/Madrid),
/// aunque los instantes se guarden en UTC. Se agrega en memoria sobre los datos del rango: suficiente para un
/// bar (miles de tickets al mes); si crece mucho habría que pasar la agregación a SQL.
/// </summary>
public sealed class ReportService(IAppData data, IUnitOfWork unitOfWork, ICurrentUser user, AuditWriter audit, TimeProvider time)
{
    public const int MaxDays = 366;

    public async Task<SalesReport> GetSalesReportAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        user.Require(Permission.ViewReports);
        var range = Validate(from, to);
        var start = BusinessTime.StartOfDayUtc(range.From);
        var end = BusinessTime.StartOfDayUtc(range.To.AddDays(1));

        var orders = await data.ToListAsync(
            data.NoTracking(data.Orders).Where(o => o.Status == OrderStatus.Paid && o.ClosedAt != null && o.ClosedAt >= start && o.ClosedAt < end),
            cancellationToken);
        var refunds = await data.ToListAsync(data.NoTracking(data.PaymentRefunds).Where(r => r.CreatedAt >= start && r.CreatedAt < end), cancellationToken);
        var payments = await data.ToListAsync(data.NoTracking(data.Payments).Where(p => p.PaidAt >= start && p.PaidAt < end), cancellationToken);
        var methods = (await data.ToListAsync(data.NoTracking(data.PaymentMethods), cancellationToken)).ToDictionary(m => m.Id);

        var totals = new SalesTotals(
            orders.Count,
            orders.Sum(o => o.GrandTotal + o.DiscountTotal),
            orders.Sum(o => o.DiscountTotal),
            orders.Sum(o => o.GrandTotal),
            orders.Sum(o => o.Subtotal),
            orders.Sum(o => o.TaxTotal),
            refunds.Sum(r => r.Amount),
            orders.Sum(o => o.GrandTotal) - refunds.Sum(r => r.Amount));

        var byDay = orders.GroupBy(o => BusinessTime.ToLocalDate(o.ClosedAt!.Value)).ToDictionary(g => g.Key, g => (Tickets: g.Count(), Sales: g.Sum(o => o.GrandTotal)));
        var days = Enumerable.Range(0, range.Days)
            .Select(i => range.From.AddDays(i))
            .Select(d => byDay.TryGetValue(d, out var v) ? new DayRow(d, v.Tickets, v.Sales) : new DayRow(d, 0, 0))
            .ToList();

        var hours = orders.GroupBy(o => BusinessTime.ToLocal(o.ClosedAt!.Value).Hour)
            .OrderBy(g => g.Key)
            .Select(g => new HourRow(g.Key, g.Count(), g.Sum(o => o.GrandTotal)))
            .ToList();

        var methodIds = payments.Select(p => p.PaymentMethodId).Concat(refunds.Select(r => r.PaymentMethodId)).Distinct();
        var methodRows = methodIds
            .Where(methods.ContainsKey)
            .Select(id => methods[id])
            .OrderBy(m => m.DisplayOrder)
            .Select(m => new MethodRow(
                m.Name,
                m.AffectsCashDrawer,
                payments.Where(p => p.PaymentMethodId == m.Id).Sum(p => p.Amount),
                refunds.Where(r => r.PaymentMethodId == m.Id).Sum(r => r.Amount)))
            .ToList();

        var productCategories = (await data.ToListAsync(data.NoTracking(data.Products).Select(p => new { p.Id, p.CategoryId }), cancellationToken)).ToDictionary(p => p.Id, p => p.CategoryId);
        var categoryNames = (await data.ToListAsync(data.NoTracking(data.Categories), cancellationToken)).ToDictionary(c => c.Id, c => c.Name);
        string CategoryOf(int? productId) =>
            productId is { } id && productCategories.TryGetValue(id, out var cat) && categoryNames.TryGetValue(cat, out var name) ? name : "(sin categoría)";

        var lines = orders.SelectMany(o => o.Lines).Where(l => l.IsActive).ToList();
        var products = lines
            .GroupBy(l => (l.ProductId, l.ProductNameSnapshot))
            .Select(g => new ProductSalesRow(g.Key.ProductId, g.Key.ProductNameSnapshot, CategoryOf(g.Key.ProductId), g.Sum(l => l.Quantity), g.Sum(l => l.LineTotal)))
            .OrderByDescending(p => p.Amount)
            .ThenBy(p => p.Name)
            .ToList();
        var categories = products
            .GroupBy(p => p.Category)
            .Select(g => new CategorySalesRow(g.Key, g.Sum(p => p.Quantity), g.Sum(p => p.Amount)))
            .OrderByDescending(c => c.Amount)
            .ToList();

        var (voidRows, voidCount, voidAmount) = await LoadVoidsAsync(start, end, cancellationToken);

        var sessions = await data.ToListAsync(
            data.NoTracking(data.CashSessions).Where(s => s.Status == CashSessionStatus.Closed && s.ClosedAt != null && s.ClosedAt >= start && s.ClosedAt < end).OrderBy(s => s.ClosedAt),
            cancellationToken);
        var differences = sessions
            .Select(s => new CashDifferenceRow(s.Id, s.ClosedAt!.Value, s.ExpectedCash ?? 0, s.CountedCash ?? 0, s.Difference ?? 0))
            .ToList();

        var lowProducts = await data.ToListAsync(data.NoTracking(data.Products).Where(p => p.StockControlled && p.IsActive).OrderBy(p => p.Name), cancellationToken);
        var lowStock = lowProducts
            .Where(p => p.StockQuantity <= p.StockMinimum)
            .Select(p => new StockRow(p.Id, p.Name, categoryNames.GetValueOrDefault(p.CategoryId, "?"), p.StockQuantity, p.StockMinimum, p.StockUnit, p.IsActive))
            .ToList();

        return new SalesReport(range, totals, days, hours, methodRows, products, categories, voidCount, voidAmount, voidRows, differences, lowStock);
    }

    /// <summary>Registra en auditoría que se ha exportado un informe (quién, qué y de qué fechas).</summary>
    public async Task RecordExportAsync(string report, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        user.Require(Permission.ViewReports);
        audit.Write(AuditActions.ReportExported, "report", report, new { from, to });
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public DateOnly Today() => BusinessTime.Today(time);

    private async Task<(IReadOnlyList<VoidRow> Rows, int Count, decimal Amount)> LoadVoidsAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var orders = await data.ToListAsync(
            data.NoTracking(data.Orders).Where(o => o.Lines.Any(l => l.Status == OrderLineStatus.Cancelled && l.VoidedAt != null && l.VoidedAt >= start && l.VoidedAt < end)),
            cancellationToken);
        var voided = orders.SelectMany(o => o.Lines)
            .Where(l => l.Status == OrderLineStatus.Cancelled && l.VoidedAt is { } at && at >= start && at < end)
            .OrderByDescending(l => l.VoidedAt)
            .ToList();

        var userIds = voided.Where(l => l.VoidedBy != null).Select(l => l.VoidedBy!.Value).Distinct().ToList();
        var names = (await data.ToListAsync(data.NoTracking(data.Users).Where(u => userIds.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName }), cancellationToken))
            .ToDictionary(u => u.Id, u => u.DisplayName);

        var rows = voided.Take(200)
            .Select(l => new VoidRow(l.VoidedAt!.Value, l.ProductNameSnapshot, l.Quantity, l.LineTotal, l.VoidReason ?? string.Empty, l.VoidedBy is { } by ? names.GetValueOrDefault(by, "?") : "?"))
            .ToList();
        return (rows, voided.Count, voided.Sum(l => l.LineTotal));
    }

    private static ReportRange Validate(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            throw new DomainException("La fecha final no puede ser anterior a la inicial.");
        }

        if (to.DayNumber - from.DayNumber + 1 > MaxDays)
        {
            throw new DomainException($"El informe admite como máximo {MaxDays} días. Reduzca el rango.");
        }

        return new ReportRange(from, to);
    }
}
