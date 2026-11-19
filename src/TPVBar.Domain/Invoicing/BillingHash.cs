using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TPVBar.Domain.Invoicing;

/// <summary>
/// Huella (SHA-256) y datos del QR de los registros de facturación.
/// <para>
/// IMPORTANTE: el formato de la cadena de entrada sigue la descripción de las especificaciones técnicas de la AEAT
/// recogida en fuentes secundarias (orden y nombres de campo, fechas dd-MM-yyyy, importes sin ceros finales,
/// fecha-hora ISO 8601 con huso). NO se ha contrastado con los ejemplos oficiales de la AEAT: debe validarse con ellos
/// antes de enviar nada (DECISIONS.md, D-060).
/// </para>
/// </summary>
public static class BillingHash
{
    public const string PreProductionQrUrl = "https://prewww2.aeat.es/wlpl/TIKE-CONT/ValidarQR";
    public const string ProductionQrUrl = "https://www2.agenciatributaria.gob.es/wlpl/TIKE-CONT/ValidarQR";

    public static string FormatDate(DateOnly date) => date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

    public static string FormatTimestamp(DateTimeOffset instantLocal) => instantLocal.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>Importe con punto decimal y sin ceros finales (21,00 → «21»; 12,50 → «12.5»).</summary>
    public static string FormatAmount(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string AltaInput(string issuerNif, string invoiceNumber, string issueDate, string typeCode, decimal taxTotal, decimal grandTotal, string previousHash, string generatedAt) =>
        $"IDEmisorFactura={issuerNif.Trim()}&NumSerieFactura={invoiceNumber.Trim()}&FechaExpedicionFactura={issueDate.Trim()}&TipoFactura={typeCode.Trim()}" +
        $"&CuotaTotal={FormatAmount(taxTotal)}&ImporteTotal={FormatAmount(grandTotal)}&Huella={previousHash.Trim()}&FechaHoraHusoGenRegistro={generatedAt.Trim()}";

    public static string CancellationInput(string issuerNif, string invoiceNumber, string issueDate, string previousHash, string generatedAt) =>
        $"IDEmisorFacturaAnulada={issuerNif.Trim()}&NumSerieFacturaAnulada={invoiceNumber.Trim()}&FechaExpedicionFacturaAnulada={issueDate.Trim()}" +
        $"&Huella={previousHash.Trim()}&FechaHoraHusoGenRegistro={generatedAt.Trim()}";

    /// <summary>SHA-256 de la cadena en UTF-8, en 64 caracteres hexadecimales en mayúsculas.</summary>
    public static string Sha256Hex(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    /// <summary>URL del QR de cotejo. El importe es el total de la factura con 2 decimales y punto.</summary>
    public static string QrUrl(string baseUrl, string issuerNif, string invoiceNumber, string issueDate, decimal grandTotal) =>
        $"{baseUrl}?nif={Uri.EscapeDataString(issuerNif)}&numserie={Uri.EscapeDataString(invoiceNumber)}" +
        $"&fecha={Uri.EscapeDataString(issueDate)}&importe={grandTotal.ToString("0.00", CultureInfo.InvariantCulture)}";
}
