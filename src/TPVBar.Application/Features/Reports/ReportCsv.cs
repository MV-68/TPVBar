using System.Globalization;
using System.Text;

namespace TPVBar.Application.Features.Reports;

/// <summary>
/// CSV para hoja de cálculo en español: separador «;», decimales con coma, fechas dd/MM/yyyy y UTF-8 con BOM
/// (para que Excel respete las tildes). Las celdas de texto se protegen frente a la inyección de fórmulas:
/// si empiezan por = + - @ (o tabulador/retorno) se les antepone una comilla simple.
/// </summary>
public static class ReportCsv
{
    private static readonly CultureInfo Spanish = new("es-ES");

    public static readonly string[] Kinds = ["ventas-por-periodo", "ventas-por-producto", "cobros-por-metodo"];

    public static string SalesByPeriod(SalesReport report, Granularity granularity)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        Row(sb, "Periodo", "Tickets", "Ventas (IVA incl.)", "Ticket medio");
        var series = report.Series(granularity);
        foreach (var row in series)
        {
            Row(sb, Text(row.Label), Int(row.Tickets), Money(row.Sales), Money(row.AverageTicket));
        }

        Row(sb, "TOTAL", Int(report.Totals.Tickets), Money(report.Totals.Sales), Money(report.Totals.AverageTicket));
        return sb.ToString();
    }

    public static string SalesByProduct(SalesReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        Row(sb, "Producto", "Categoría", "Unidades", "Importe (IVA incl.)");
        foreach (var p in report.Products)
        {
            Row(sb, Text(p.Name), Text(p.Category), Quantity(p.Quantity), Money(p.Amount));
        }

        Row(sb, "TOTAL", string.Empty, Quantity(report.Products.Sum(p => p.Quantity)), Money(report.Products.Sum(p => p.Amount)));
        return sb.ToString();
    }

    public static string PaymentsByMethod(SalesReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        Row(sb, "Método de pago", "Cobros", "Devoluciones", "Neto");
        foreach (var m in report.Methods)
        {
            Row(sb, Text(m.Name), Money(m.Payments), Money(m.Refunds), Money(m.Net));
        }

        Row(sb, "TOTAL", Money(report.Methods.Sum(m => m.Payments)), Money(report.Methods.Sum(m => m.Refunds)), Money(report.Methods.Sum(m => m.Net)));
        return sb.ToString();
    }

    /// <summary>Convierte el texto a bytes UTF-8 con BOM.</summary>
    public static byte[] ToBytes(string csv) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)];

    /// <summary>Celda de texto segura: escapada y protegida frente a la inyección de fórmulas.</summary>
    public static string Text(string value)
    {
        var safe = value ?? string.Empty;
        if (safe.Length > 0 && safe[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            safe = "'" + safe;
        }

        return Escape(safe);
    }

    private static string Escape(string value) =>
        value.Contains(';', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;

    private static string Money(decimal value) => value.ToString("0.00", Spanish);

    private static string Quantity(decimal value) => value.ToString("0.###", Spanish);

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Row(StringBuilder sb, params string[] cells) => sb.Append(string.Join(';', cells)).Append("\r\n");
}
