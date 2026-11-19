using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Invoicing;
using TPVBar.Application.Features.Reports;
using TPVBar.Domain.Exceptions;
using TPVBar.Web.Security;

namespace TPVBar.Web.Endpoints;

/// <summary>
/// Exportación de informes a CSV (español: «;», coma decimal, UTF-8 con BOM). Exige sesión y permiso
/// <c>ViewReports</c>; cada descarga queda auditada. XLSX no está implementado (pendiente de aprobación).
/// </summary>
public static class ReportExportEndpoints
{
    public static IEndpointRouteBuilder MapReportExportEndpoints(this IEndpointRouteBuilder app)
    {
        // Las rutas literales de facturas tienen prioridad sobre «/export/{informe}.csv».
        app.MapGet("/export/facturas-libro.csv", (string? desde, string? hasta, HttpContext http, IServiceScopeFactory scopes, CancellationToken ct) =>
                ExportInvoicesAsync(book: true, desde, hasta, http, scopes, ct))
            .RequireAuthorization(Policies.ViewReports);
        app.MapGet("/export/facturas-iva.csv", (string? desde, string? hasta, HttpContext http, IServiceScopeFactory scopes, CancellationToken ct) =>
                ExportInvoicesAsync(book: false, desde, hasta, http, scopes, ct))
            .RequireAuthorization(Policies.ViewReports);
        app.MapGet("/export/{informe}.csv", ExportAsync)
            .RequireAuthorization(Policies.ViewReports);
        return app;
    }

    private static async Task<IResult> ExportInvoicesAsync(
        bool book,
        string? desde,
        string? hasta,
        HttpContext http,
        IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(desde, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
            !DateOnly.TryParseExact(hasta, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
        {
            return Results.BadRequest(new { error = "Fechas no válidas. Use desde=yyyy-MM-dd&hasta=yyyy-MM-dd." });
        }

        if (!PrincipalReader.TryRead(http.User, out var userId, out var permissions))
        {
            return Results.Unauthorized();
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CurrentUserAccessor>().Set(userId, permissions, http.Connection.RemoteIpAddress?.ToString());
        var reports = scope.ServiceProvider.GetRequiredService<InvoiceReportService>();

        try
        {
            string csv;
            string name;
            if (book)
            {
                csv = InvoiceCsv.Book(await reports.GetBookAsync(from, to, audited: true, cancellationToken));
                name = "libro-facturas";
            }
            else
            {
                csv = InvoiceCsv.Vat(await reports.GetVatAsync(from, to, cancellationToken));
                name = "iva-por-tipo";
            }

            return Results.File(ReportCsv.ToBytes(csv), "text/csv; charset=utf-8", $"tpvbar-{name}-{from:yyyyMMdd}-{to:yyyyMMdd}.csv");
        }
        catch (DomainException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ExportAsync(
        string informe,
        string? desde,
        string? hasta,
        string? agrupar,
        HttpContext http,
        IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        if (!ReportCsv.Kinds.Contains(informe, StringComparer.Ordinal) ||
            !DateOnly.TryParseExact(desde, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
            !DateOnly.TryParseExact(hasta, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
        {
            return Results.BadRequest(new { error = "Informe o fechas no válidos. Use desde=yyyy-MM-dd&hasta=yyyy-MM-dd." });
        }

        if (!PrincipalReader.TryRead(http.User, out var userId, out var permissions))
        {
            return Results.Unauthorized();
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CurrentUserAccessor>().Set(userId, permissions, http.Connection.RemoteIpAddress?.ToString());
        var reports = scope.ServiceProvider.GetRequiredService<ReportService>();

        try
        {
            var report = await reports.GetSalesReportAsync(from, to, cancellationToken);
            var granularity = agrupar switch { "semana" => Granularity.Week, "mes" => Granularity.Month, _ => Granularity.Day };
            var csv = informe switch
            {
                "ventas-por-periodo" => ReportCsv.SalesByPeriod(report, granularity),
                "ventas-por-producto" => ReportCsv.SalesByProduct(report),
                _ => ReportCsv.PaymentsByMethod(report),
            };

            await reports.RecordExportAsync(informe, from, to, cancellationToken);
            return Results.File(ReportCsv.ToBytes(csv), "text/csv; charset=utf-8", $"tpvbar-{informe}-{from:yyyyMMdd}-{to:yyyyMMdd}.csv");
        }
        catch (DomainException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
