using System.Text;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Cash;
using TPVBar.Application.Features.Reports;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Tests;

public class ReportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Permission[] All =
    [
        Permission.TakeOrders, Permission.TakePayments, Permission.OpenCloseCash, Permission.PerformRefunds, Permission.ViewReports, Permission.VoidLines,
    ];

    // 2026-11-16 es lunes. Madrid está en horario de invierno (UTC+1).
    private static readonly DateTimeOffset Noon = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly D16 = new(2026, 11, 16);
    private static readonly DateOnly D17 = new(2026, 11, 17);

    private static async Task<SalesFixture> OpenAsync()
    {
        var f = new SalesFixture(All).WithPrinters().WithPaymentMethods();
        await f.Cash().OpenAsync(0m, Ct);
        return f;
    }

    /// <summary>Crea, envía y cobra en efectivo una comanda en el instante indicado.</summary>
    private static async Task<(long OrderId, long PaymentId, decimal Total)> PaidOrderAsync(SalesFixture f, DateTimeOffset at, params (Product Product, decimal Quantity)[] items)
    {
        f.Time.Set(at);
        var orderId = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        foreach (var (product, quantity) in items)
        {
            await f.Orders().AddLineAsync(orderId, product.Id, quantity, null, Guid.NewGuid(), Ct);
        }

        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        var view = await f.Orders().GetAsync(orderId, Ct);
        var paid = await f.Payments().PayAsync(new PayRequest(orderId, f.CashMethod.Id, view.GrandTotal, null, null, Guid.NewGuid()), Ct);
        return (orderId, paid.PaymentId, view.GrandTotal);
    }

    // ---------- Totales ----------

    [Fact]
    public async Task Totales_SumanTicketsVentasBaseEIvaYSeparanLasDevoluciones()
    {
        var f = await OpenAsync();
        var a = await PaidOrderAsync(f, Noon, (f.Cana, 2), (f.Tortilla, 1)); // 7,10: base 6,16 + IVA 0,94
        await PaidOrderAsync(f, Noon.AddHours(1), (f.Tortilla, 2));          // 7,00: base 6,36 + IVA 0,64
        await f.Payments().RefundAsync(a.PaymentId, 1.00m, "Reclamación", Guid.NewGuid(), Ct);

        var report = await f.Reports().GetSalesReportAsync(D16, D16, Ct);

        var t = report.Totals;
        Assert.Equal(2, t.Tickets);
        Assert.Equal(14.10m, t.Sales);
        Assert.Equal(14.10m, t.GrossSales);
        Assert.Equal(0m, t.Discounts);
        Assert.Equal(12.52m, t.BaseAmount);
        Assert.Equal(1.58m, t.Tax);
        Assert.Equal(t.Sales, t.BaseAmount + t.Tax); // base + IVA = ventas
        Assert.Equal(7.05m, t.AverageTicket);
        Assert.Equal(1.00m, t.Refunds);
        Assert.Equal(13.10m, t.NetAfterRefunds);
    }

    [Fact]
    public async Task Totales_SoloCuentanLasComandasCobradas()
    {
        var f = await OpenAsync();
        await PaidOrderAsync(f, Noon, (f.Cana, 1));
        // Una comanda abierta y otra enviada sin cobrar no son tickets.
        var open = await f.Floor().OpenTableAsync(f.Mesa2.Id, 2, Ct);
        await f.Orders().AddLineAsync(open, f.Tortilla.Id, 3, null, Guid.NewGuid(), Ct);
        await f.Orders().SendToKitchenAsync(open, Guid.NewGuid(), Ct);

        var report = await f.Reports().GetSalesReportAsync(D16, D16, Ct);

        Assert.Equal(1, report.Totals.Tickets);
        Assert.Equal(1.80m, report.Totals.Sales);
    }

    [Fact]
    public async Task SinVentas_TodoACero_SinFallar()
    {
        var f = await OpenAsync();

        var report = await f.Reports().GetSalesReportAsync(D16, D16, Ct);

        Assert.Equal(0, report.Totals.Tickets);
        Assert.Equal(0m, report.Totals.AverageTicket);
        Assert.Empty(report.Hours);
        Assert.Empty(report.Products);
        Assert.Single(report.Days);
    }

    // ---------- Zona horaria ----------

    [Fact]
    public async Task Dias_SonLocalesDeMadridYNoUtc_ConDiasSinVentasIncluidos()
    {
        var f = await OpenAsync();
        await PaidOrderAsync(f, Noon, (f.Cana, 1));                                     // 16/11 13:00 local
        await PaidOrderAsync(f, new DateTimeOffset(2026, 11, 16, 23, 30, 0, TimeSpan.Zero), (f.Cana, 2)); // 17/11 00:30 local

        var report = await f.Reports().GetSalesReportAsync(D16, new DateOnly(2026, 11, 18), Ct);

        Assert.Equal(3, report.Days.Count);
        Assert.Equal((1, 1.80m), (report.Days[0].Tickets, report.Days[0].Sales));
        Assert.Equal((1, 3.60m), (report.Days[1].Tickets, report.Days[1].Sales)); // cae en el día 17 local
        Assert.Equal((0, 0m), (report.Days[2].Tickets, report.Days[2].Sales));
    }

    [Fact]
    public async Task Franjas_SeAgrupanPorHoraLocal()
    {
        var f = await OpenAsync();
        await PaidOrderAsync(f, Noon, (f.Cana, 1));                                     // 13:xx local
        await PaidOrderAsync(f, Noon.AddMinutes(20), (f.Cana, 1));                      // 13:xx local
        await PaidOrderAsync(f, new DateTimeOffset(2026, 11, 16, 23, 30, 0, TimeSpan.Zero), (f.Cana, 1)); // 00:xx del 17

        var report = await f.Reports().GetSalesReportAsync(D16, D17, Ct);

        Assert.Equal([0, 13], report.Hours.Select(h => h.Hour));
        Assert.Equal(2, report.Hours.Single(h => h.Hour == 13).Tickets);
        Assert.Equal(1, report.Hours.Single(h => h.Hour == 0).Tickets);
    }

    [Fact]
    public async Task FrontereDeDia_ElInstanteExactoDeMedianocheLocalPerteneceAlDiaNuevo()
    {
        var f = await OpenAsync();
        await PaidOrderAsync(f, new DateTimeOffset(2026, 11, 16, 22, 59, 59, TimeSpan.Zero), (f.Cana, 1)); // 23:59:59 local del 16
        await PaidOrderAsync(f, new DateTimeOffset(2026, 11, 16, 23, 0, 0, TimeSpan.Zero), (f.Cana, 1));   // 00:00:00 local del 17

        var day16 = await f.Reports().GetSalesReportAsync(D16, D16, Ct);
        var day17 = await f.Reports().GetSalesReportAsync(D17, D17, Ct);

        Assert.Equal(1, day16.Totals.Tickets);
        Assert.Equal(1, day17.Totals.Tickets);
    }

    [Fact]
    public async Task Series_PorSemanaYMes_AgrupanDesdeElLunesYPorMesNatural()
    {
        var f = await OpenAsync();
        await PaidOrderAsync(f, new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero), (f.Cana, 1)); // domingo
        await PaidOrderAsync(f, new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero), (f.Cana, 1)); // lunes siguiente
        await PaidOrderAsync(f, new DateTimeOffset(2026, 12, 2, 12, 0, 0, TimeSpan.Zero), (f.Cana, 1));

        var report = await f.Reports().GetSalesReportAsync(new DateOnly(2026, 11, 16), new DateOnly(2026, 12, 6), Ct);

        var weeks = report.Series(Granularity.Week).Where(w => w.Tickets > 0).ToList();
        Assert.Equal([new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 23), new DateOnly(2026, 11, 30)], weeks.Select(w => w.Start));
        Assert.Equal([1, 1, 1], weeks.Select(w => w.Tickets));
        var months = report.Series(Granularity.Month);
        Assert.Equal([2, 1], months.Select(m => m.Tickets));
        Assert.Equal(report.Totals.Sales, months.Sum(m => m.Sales));
        Assert.Equal(report.Days.Count, report.Series(Granularity.Day).Count);
    }

    // ---------- Cobros ≠ ventas ----------

    [Fact]
    public async Task Cobros_NoSonVentas_UnaCuentaCobradaEnDosDiasRepartePagosYTicketPorSeparado()
    {
        var f = await OpenAsync();
        f.Time.Set(Noon);
        var orderId = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 2, null, Guid.NewGuid(), Ct); // 3,60
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        await f.Payments().PayAsync(new PayRequest(orderId, f.CardMethod.Id, 1.00m, null, null, Guid.NewGuid()), Ct);
        f.Time.Set(Noon.AddDays(1));
        await f.Payments().PayAsync(new PayRequest(orderId, f.CashMethod.Id, 2.60m, null, null, Guid.NewGuid()), Ct);

        var day16 = await f.Reports().GetSalesReportAsync(D16, D16, Ct);
        var day17 = await f.Reports().GetSalesReportAsync(D17, D17, Ct);

        Assert.Equal(0, day16.Totals.Tickets);
        Assert.Equal(1.00m, day16.TotalPayments);
        Assert.Equal(1, day17.Totals.Tickets);
        Assert.Equal(3.60m, day17.Totals.Sales);
        Assert.Equal(2.60m, day17.TotalPayments);
    }

    [Fact]
    public async Task Cobros_PorMetodo_RestanLasDevolucionesDeCadaMetodo()
    {
        var f = await OpenAsync();
        var cash = await PaidOrderAsync(f, Noon, (f.Cana, 2)); // efectivo 3,60
        f.Time.Set(Noon.AddMinutes(30));
        var orderId = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        await f.Orders().AddLineAsync(orderId, f.Tortilla.Id, 1, null, Guid.NewGuid(), Ct);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        await f.Payments().PayAsync(new PayRequest(orderId, f.CardMethod.Id, 3.50m, null, null, Guid.NewGuid()), Ct); // tarjeta 3,50
        await f.Payments().RefundAsync(cash.PaymentId, 0.60m, "Error", Guid.NewGuid(), Ct);

        var report = await f.Reports().GetSalesReportAsync(D16, D16, Ct);

        var efectivo = report.Methods.Single(m => m.Name == "Efectivo");
        var tarjeta = report.Methods.Single(m => m.Name == "Tarjeta");
        Assert.Equal((3.60m, 0.60m, 3.00m, true), (efectivo.Payments, efectivo.Refunds, efectivo.Net, efectivo.IsCash));
        Assert.Equal((3.50m, 0m, 3.50m), (tarjeta.Payments, tarjeta.Refunds, tarjeta.Net));
    }

    // ---------- Productos, anulaciones y caja ----------

    [Fact]
    public async Task Productos_AgreganUnidadesEImporteYExcluyenLasLineasAnuladas()
    {
        var f = await OpenAsync();
        f.Time.Set(Noon);
        var orderId = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        await f.Orders().AddLineAsync(orderId, f.Cana.Id, 2, null, Guid.NewGuid(), Ct);
        var tortilla = await f.Orders().AddLineAsync(orderId, f.Tortilla.Id, 1, null, Guid.NewGuid(), Ct);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        await f.Orders().VoidLineAsync(orderId, tortilla.Lines.Single(l => l.Name == "Tortilla").Id, "Se quemó", Ct);
        var view = await f.Orders().GetAsync(orderId, Ct);
        await f.Payments().PayAsync(new PayRequest(orderId, f.CashMethod.Id, view.GrandTotal, null, null, Guid.NewGuid()), Ct);
        await PaidOrderAsync(f, Noon.AddHours(1), (f.Cana, 1));

        var report = await f.Reports().GetSalesReportAsync(D16, D16, Ct);

        var cana = report.Products.Single(p => p.Name == "Caña");
        Assert.Equal((3m, 5.40m, "Bebidas"), (cana.Quantity, cana.Amount, cana.Category));
        Assert.DoesNotContain(report.Products, p => p.Name == "Tortilla"); // la línea anulada no se vende
        Assert.Equal(5.40m, report.Categories.Single(c => c.Category == "Bebidas").Amount);
        Assert.Equal(5.40m, report.Totals.Sales);
        Assert.Equal(1, report.VoidCount);
        Assert.Equal(3.50m, report.VoidAmount);
        var voided = Assert.Single(report.Voids);
        Assert.Equal(("Tortilla", "Se quemó", "Cajero de prueba"), (voided.Product, voided.Reason, voided.By));
    }

    [Fact]
    public async Task Anulaciones_SeCuentanPorLaFechaDeLaAnulacionAunqueLaComandaNoSeCobre()
    {
        var f = await OpenAsync();
        f.Time.Set(Noon);
        var orderId = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        var view = await f.Orders().AddLineAsync(orderId, f.Cana.Id, 2, null, Guid.NewGuid(), Ct);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        f.Time.Set(Noon.AddDays(1));
        await f.Orders().VoidLineAsync(orderId, view.Lines.Single().Id, "Cliente se fue", Ct);

        Assert.Equal(0, (await f.Reports().GetSalesReportAsync(D16, D16, Ct)).VoidCount);
        var day17 = await f.Reports().GetSalesReportAsync(D17, D17, Ct);
        Assert.Equal((1, 3.60m), (day17.VoidCount, day17.VoidAmount));
        Assert.Equal(0, day17.Totals.Tickets);
    }

    [Fact]
    public async Task DiferenciasDeCaja_ListanLosCierresDelPeriodoConSuTotal()
    {
        var f = await OpenAsync();
        await PaidOrderAsync(f, Noon, (f.Cana, 2)); // efectivo 3,60
        f.Time.Set(Noon.AddHours(8));
        await f.Cash().CloseAsync(3.10m, null, Ct); // faltan 0,50
        await f.Cash().OpenAsync(0m, Ct);
        f.Time.Set(Noon.AddHours(9));
        await f.Cash().CloseAsync(0.20m, null, Ct); // sobran 0,20

        var report = await f.Reports().GetSalesReportAsync(D16, D16, Ct);

        Assert.Equal([-0.50m, 0.20m], report.CashDifferences.Select(d => d.Difference));
        Assert.Equal(-0.30m, report.CashDifferenceTotal);
        Assert.Equal(3.60m, report.CashDifferences[0].Expected);
    }

    [Fact]
    public async Task StockBajo_ApareceEnElInforme()
    {
        var f = await OpenAsync();
        f.WithStock(f.Cana, 2m, 5m);

        var report = await f.Reports().GetSalesReportAsync(D16, D16, Ct);

        var low = Assert.Single(report.LowStock);
        Assert.Equal(("Caña", 2m, 5m), (low.Name, low.Quantity, low.Minimum));
    }

    // ---------- Permisos y validación ----------

    [Fact]
    public async Task Informes_ExigenElPermisoViewReports()
    {
        var f = await OpenAsync();
        f.User.Set(f.UserId, [Permission.TakeOrders, Permission.TakePayments], "test");

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Reports().GetSalesReportAsync(D16, D16, Ct));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Reports().RecordExportAsync("ventas-por-periodo", D16, D16, Ct));
    }

    [Fact]
    public async Task Informes_RangoInvalidoOExcesivo_SeRechaza()
    {
        var f = await OpenAsync();

        await Assert.ThrowsAsync<DomainException>(() => f.Reports().GetSalesReportAsync(D17, D16, Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Reports().GetSalesReportAsync(D16, D16.AddDays(366), Ct));
        var year = await f.Reports().GetSalesReportAsync(D16, D16.AddDays(365), Ct);
        Assert.Equal(366, year.Days.Count);
    }

    [Fact]
    public async Task Exportar_QuedaAuditado()
    {
        var f = await OpenAsync();

        await f.Reports().RecordExportAsync("ventas-por-producto", D16, D17, Ct);

        var audit = Assert.Single(f.Store.Audit, e => e.Action == Domain.Auditing.AuditActions.ReportExported);
        Assert.Equal(f.UserId, audit.UserId);
        Assert.Equal("ventas-por-producto", audit.EntityId);
    }

    // ---------- CSV ----------

    [Fact]
    public async Task Csv_UsaPuntoYComaComaDecimalCabecerasEnEspanolYFilaDeTotal()
    {
        var f = await OpenAsync();
        await PaidOrderAsync(f, Noon, (f.Cana, 2), (f.Tortilla, 1));
        await PaidOrderAsync(f, Noon.AddHours(1), (f.Tortilla, 2));
        var report = await f.Reports().GetSalesReportAsync(D16, D16, Ct);

        var csv = ReportCsv.SalesByPeriod(report, Granularity.Day);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Periodo;Tickets;Ventas (IVA incl.);Ticket medio", lines[0]);
        Assert.Equal("16/11/2026;2;14,10;7,05", lines[1]);
        Assert.Equal("TOTAL;2;14,10;7,05", lines[2]);
        Assert.DoesNotContain("14.10", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Csv_ProductosYMetodos_TienenLasColumnasEsperadas()
    {
        var f = await OpenAsync();
        await PaidOrderAsync(f, Noon, (f.Cana, 2), (f.Tortilla, 1));
        var report = await f.Reports().GetSalesReportAsync(D16, D16, Ct);

        var products = ReportCsv.SalesByProduct(report).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var methods = ReportCsv.PaymentsByMethod(report).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Producto;Categoría;Unidades;Importe (IVA incl.)", products[0]);
        Assert.Contains("Caña;Bebidas;2;3,60", products);
        Assert.Equal("Método de pago;Cobros;Devoluciones;Neto", methods[0]);
        Assert.Contains("Efectivo;7,10;0,00;7,10", methods);
        Assert.Equal("TOTAL;7,10;0,00;7,10", methods[^1]);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+34600000000", "'+34600000000")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("Caña normal", "Caña normal")]
    [InlineData("con;punto y coma", "\"con;punto y coma\"")]
    [InlineData("dice \"hola\"", "\"dice \"\"hola\"\"\"")]
    public void Csv_LasCeldasDeTextoSeProtegenFrenteAFormulasYSeEscapan(string input, string expected) =>
        Assert.Equal(expected, ReportCsv.Text(input));

    [Fact]
    public async Task Csv_UnProductoLlamadoComoUnaFormula_NoSeEjecutaAlAbrirloEnExcel()
    {
        var f = await OpenAsync();
        f.Tortilla.Update(f.Tortilla.CategoryId, f.Tortilla.TaxRateId, "=HYPERLINK(\"http://x\")", 3.50m, PreparationStation.Kitchen, null, null, 2, Noon);
        await PaidOrderAsync(f, Noon, (f.Tortilla, 1));
        var report = await f.Reports().GetSalesReportAsync(D16, D16, Ct);

        var csv = ReportCsv.SalesByProduct(report);

        Assert.DoesNotContain("\n=HYPERLINK", csv, StringComparison.Ordinal);
        Assert.Contains("'=HYPERLINK", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_EnBytesLlevaBomUtf8ParaQueExcelRespeteLasTildes()
    {
        var bytes = ReportCsv.ToBytes("Categoría;Ñandú\r\n");

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("Categoría;Ñandú\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }
}
