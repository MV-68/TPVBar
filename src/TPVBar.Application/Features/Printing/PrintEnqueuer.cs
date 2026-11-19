using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Cash;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Printing;
using TPVBar.Domain.Sales;
using TPVBar.PrintContracts;

namespace TPVBar.Application.Features.Printing;

/// <summary>
/// Crea trabajos en la cola persistente de impresión. Los trabajos se añaden al contexto y se confirman
/// junto con la operación que los origina (misma transacción): no puede haber venta enviada sin su
/// ticket en cola, ni ticket en cola de una venta que no se guardó.
/// </summary>
public sealed class PrintEnqueuer(IAppData data, IOperationEvents events, TimeProvider time)
{
    /// <summary>Encola el ticket de un destino. Devuelve <see langword="false"/> si no hay impresora configurada.</summary>
    public async Task<bool> EnqueueKitchenTicketAsync(
        string tableLabel,
        PreparationStation station,
        IEnumerable<OrderLine> lines,
        Guid requestId,
        long orderId,
        CancellationToken cancellationToken)
    {
        var printer = await FindStationPrinterAsync(station, cancellationToken);
        if (printer is null)
        {
            return false;
        }

        var document = new PrintDocument(
            PrintDocumentKind.KitchenTicket,
            station == PreparationStation.Bar ? "BARRA" : "COCINA",
            null,
            tableLabel,
            time.GetUtcNow(),
            [.. lines.Select(l => new PrintLine(l.ProductNameSnapshot, l.Quantity, l.Notes))],
            "Ticket de preparación. No es un documento fiscal.");

        Add(printer, PrintJobType.KitchenTicket, orderId, document, $"kitchen:{requestId:N}:{station}");
        return true;
    }

    public async Task<bool> EnqueueKitchenVoidAsync(
        string tableLabel,
        PreparationStation station,
        OrderLine line,
        long orderId,
        CancellationToken cancellationToken)
    {
        var printer = await FindStationPrinterAsync(station, cancellationToken);
        if (printer is null)
        {
            return false;
        }

        var document = new PrintDocument(
            PrintDocumentKind.KitchenVoid,
            "** ANULADO **",
            station == PreparationStation.Bar ? "BARRA" : "COCINA",
            tableLabel,
            time.GetUtcNow(),
            [new PrintLine(line.ProductNameSnapshot, line.Quantity, line.VoidReason)],
            "NO preparar este producto.");

        Add(printer, PrintJobType.KitchenVoid, orderId, document, $"void:{line.Id}");
        return true;
    }

    /// <summary>Pre-cuenta para el cliente en la impresora de tickets. No es factura ni justificante fiscal.</summary>
    public async Task<bool> EnqueuePreBillAsync(string tableLabel, Order order, CancellationToken cancellationToken)
    {
        var printer = await FindReceiptPrinterAsync(cancellationToken);
        if (printer is null)
        {
            return false;
        }

        var document = new PrintDocument(
            PrintDocumentKind.PreBill,
            "CUENTA",
            "Pre-cuenta (no es una factura)",
            tableLabel,
            time.GetUtcNow(),
            [.. order.Lines.Where(l => l.IsActive).Select(l => new PrintLine(l.ProductNameSnapshot, l.Quantity, l.Notes, l.LineTotal))],
            "IVA incluido. Documento sin validez fiscal.",
            order.GrandTotal);

        Add(printer, PrintJobType.PreBill, order.Id, document, $"prebill:{order.Id}:{time.GetUtcNow().ToUnixTimeMilliseconds()}");
        return true;
    }

    /// <summary>
    /// Justificante de pago con desglose de IVA y pagos. NO es una factura ni un documento fiscal: el
    /// cumplimiento fiscal (numeración, facturas simplificadas, VERI*FACTU) está pendiente de análisis legal.
    /// </summary>
    public async Task<bool> EnqueueReceiptAsync(
        string tableLabel,
        Order order,
        IReadOnlyList<ReceiptPayment> payments,
        bool isReprint,
        CancellationToken cancellationToken)
    {
        var printer = await FindReceiptPrinterAsync(cancellationToken);
        if (printer is null)
        {
            return false;
        }

        var taxRows = order.Lines.Where(l => l.IsActive)
            .GroupBy(l => l.TaxRateSnapshot)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var split = g.Select(l => Domain.Common.Money.SplitGross(l.LineTotal, g.Key)).ToList();
                return new PrintLine($"IVA {g.Key:0.##} % (base {split.Sum(s => s.Base):N2} €)", Amount: split.Sum(s => s.Tax));
            })
            .ToList();

        var paymentRows = new List<PrintLine>();
        foreach (var payment in payments)
        {
            paymentRows.Add(new PrintLine(payment.Method, Amount: payment.Amount));
            if (payment.Tendered is { } tendered && payment.Change > 0)
            {
                paymentRows.Add(new PrintLine($"  Entregado {tendered:N2} €", Amount: null, Detail: null));
                paymentRows.Add(new PrintLine("  Cambio", Amount: payment.Change));
            }
        }

        var document = new PrintDocument(
            PrintDocumentKind.Receipt,
            "JUSTIFICANTE DE PAGO",
            $"Comanda nº {order.Id}" + (isReprint ? " · COPIA" : string.Empty),
            tableLabel,
            time.GetUtcNow(),
            [.. order.Lines.Where(l => l.IsActive).Select(l => new PrintLine(l.ProductNameSnapshot, l.Quantity, null, l.LineTotal))],
            "Justificante de pago. No es una factura ni un documento fiscal.",
            order.GrandTotal,
            [new PrintSection("IVA incluido", taxRows), new PrintSection("Pagos", paymentRows)]);

        var key = isReprint ? $"receipt:{order.Id}:{Guid.NewGuid():N}" : $"receipt:{order.Id}:{order.ClosedAt?.ToUnixTimeMilliseconds()}";
        Add(printer, PrintJobType.Receipt, order.Id, document, key);
        return true;
    }

    /// <summary>Resumen interno de cierre de caja en la impresora de tickets (documento interno, no fiscal).</summary>
    public async Task<bool> EnqueueCashCloseAsync(Features.Cash.CashSessionView report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var printer = await FindReceiptPrinterAsync(cancellationToken);
        if (printer is null)
        {
            return false;
        }

        var methods = report.Methods.Select(m => new PrintLine($"{m.Name}: cobros {m.Payments:N2} € · devol. {m.Refunds:N2} €", Amount: m.Net)).ToList();
        var cash = new List<PrintLine>
        {
            new("Fondo inicial", Amount: report.OpeningAmount),
            new("Entradas manuales", Amount: report.CashIn),
            new("Salidas manuales", Amount: -report.CashOut),
            new("Efectivo esperado", Amount: report.ExpectedCash),
            new("Efectivo contado", Amount: report.CountedCash),
            new("DIFERENCIA", Amount: report.Difference),
        };

        var document = new PrintDocument(
            PrintDocumentKind.CashClose,
            "CIERRE DE CAJA",
            $"Sesión {report.Id} · {report.PaymentsCount} cobros en {report.OrdersCount} comandas",
            null,
            time.GetUtcNow(),
            [new PrintLine($"Abierta {Common.BusinessTime.ToLocal(report.OpenedAt):dd/MM HH:mm} por {report.OpenedBy}")],
            "Documento interno de caja. No es un documento fiscal.",
            report.NetTotal,
            [new PrintSection("Cobros por método (neto)", methods), new PrintSection("Efectivo", cash)]);

        Add(printer, PrintJobType.CashClose, report.Id, document, $"cashclose:{report.Id}");
        return true;
    }

    private Task<Printer?> FindReceiptPrinterAsync(CancellationToken cancellationToken) =>
        data.FirstOrDefaultAsync(data.Printers.Where(p => p.IsActive && p.Type == PrinterType.Receipt).OrderBy(p => p.Id), cancellationToken);

    public PrintJob EnqueueTest(Printer printer)
    {
        ArgumentNullException.ThrowIfNull(printer);
        var document = new PrintDocument(
            PrintDocumentKind.Test,
            "PRUEBA DE IMPRESORA",
            printer.IsSimulated ? "IMPRESORA SIMULADA: no sale papel" : printer.Name,
            null,
            time.GetUtcNow(),
            [new PrintLine("ÁÉÍÓÚ áéíóú Ññ Üü ¿? ¡! € 0123456789"), new PrintLine("Si lee esto, la impresora funciona.")],
            "TPVBar");
        return Add(printer, PrintJobType.Test, printer.Id, document, $"test:{printer.Id}:{Guid.NewGuid():N}");
    }

    internal PrintJob Add(Printer printer, PrintJobType type, long referenceId, PrintDocument document, string dedupeKey)
    {
        var job = PrintJob.Create(
            printer.Id,
            type,
            type == PrintJobType.Test ? "printer" : "order",
            referenceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PrintJson.Serialize(document),
            dedupeKey,
            time.GetUtcNow());
        data.Add(job);
        events.Raise(new OperationEvent(OperationEventNames.PrintJobCreated));
        return job;
    }

    private async Task<Printer?> FindStationPrinterAsync(PreparationStation station, CancellationToken cancellationToken)
    {
        var route = await data.FirstOrDefaultAsync(data.PrinterRoutes.Where(r => r.Station == station), cancellationToken);
        if (route is null)
        {
            return null;
        }

        var printerId = route.PrinterId;
        return await data.FirstOrDefaultAsync(data.Printers.Where(p => p.Id == printerId && p.IsActive), cancellationToken);
    }
}
