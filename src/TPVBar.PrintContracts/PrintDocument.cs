namespace TPVBar.PrintContracts;

public enum PrintDocumentKind
{
    /// <summary>Ticket de preparación para cocina o barra. No es un documento fiscal.</summary>
    KitchenTicket = 1,

    /// <summary>Aviso de anulación de un producto ya enviado.</summary>
    KitchenVoid = 2,

    /// <summary>Pre-cuenta para el cliente. No es una factura ni un justificante fiscal.</summary>
    PreBill = 3,

    /// <summary>Página de prueba de impresora.</summary>
    Test = 4,

    /// <summary>Justificante de pago. NO es una factura ni un documento fiscal.</summary>
    Receipt = 5,

    /// <summary>Resumen interno de cierre de caja.</summary>
    CashClose = 6,
}

public sealed record PrintLine(string Text, decimal? Quantity = null, string? Detail = null, decimal? Amount = null);

/// <summary>Bloque con título (desglose de IVA, pagos, totales de caja...). Cada línea lleva texto y, si procede, importe.</summary>
public sealed record PrintSection(string Title, IReadOnlyList<PrintLine> Lines);

/// <summary>
/// Documento a imprimir, independiente del dispositivo. Se guarda como JSON en la cola; el agente lo
/// convierte a los comandos de la impresora. Nunca contiene secretos.
/// </summary>
public sealed record PrintDocument(
    PrintDocumentKind Kind,
    string Title,
    string? Subtitle,
    string? TableLabel,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PrintLine> Lines,
    string? Footer = null,
    decimal? Total = null,
    IReadOnlyList<PrintSection>? Sections = null);
