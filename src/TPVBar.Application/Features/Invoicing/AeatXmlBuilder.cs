using System.Globalization;
using System.Xml.Linq;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Invoicing;

namespace TPVBar.Application.Features.Invoicing;

/// <summary>
/// Genera el elemento <c>RegFactuSistemaFacturacion</c> (esquemas SuministroLR / SuministroInformacion de la AEAT) a partir
/// de los registros de facturación ya guardados. No calcula huellas: usa exactamente las del registro. El resultado se
/// valida contra los XSD oficiales en las pruebas y antes de cada envío.
/// </summary>
public static class AeatXmlBuilder
{
    public const string NamespaceLr = "https://www2.agenciatributaria.gob.es/static_files/common/internet/dep/aplicaciones/es/aeat/tike/cont/ws/SuministroLR.xsd";
    public const string NamespaceSf = "https://www2.agenciatributaria.gob.es/static_files/common/internet/dep/aplicaciones/es/aeat/tike/cont/ws/SuministroInformacion.xsd";
    public const string OperationDescription = "Servicios de hostelería: venta de comidas y bebidas";

    private static readonly XNamespace Lr = NamespaceLr;
    private static readonly XNamespace Sf = NamespaceSf;

    /// <summary>Un registro con lo necesario para construir su XML.</summary>
    /// <param name="Record">Registro de facturación.</param>
    /// <param name="Invoice">Factura a la que pertenece (con líneas).</param>
    /// <param name="Previous">Registro anterior de la cadena (<c>null</c> si es el primero).</param>
    /// <param name="Rectified">Factura original, si el registro es de una rectificativa.</param>
    public sealed record Item(BillingRecord Record, Invoice Invoice, BillingRecord? Previous, Invoice? Rectified);

    public static XElement Build(IReadOnlyList<Item> items, BillingSystemInfo system)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(system);
        if (items.Count is 0 or > 1000)
        {
            throw new DomainException("Un envío debe llevar entre 1 y 1000 registros.");
        }

        var first = items[0].Invoice;
        if (items.Any(i => i.Invoice.IssuerNif != first.IssuerNif))
        {
            throw new DomainException("Un envío no puede mezclar facturas de distintos emisores.");
        }

        return new XElement(
            Lr + "RegFactuSistemaFacturacion",
            new XElement(
                Lr + "Cabecera", // elemento local de SuministroLR: va en su espacio de nombres; sus hijos, en el de SuministroInformacion
                new XElement(Sf + "ObligadoEmision", new XElement(Sf + "NombreRazon", first.IssuerName), new XElement(Sf + "NIF", first.IssuerNif))),
            items.Select(item => new XElement(Lr + "RegistroFactura", item.Record.Type == BillingRecordType.Alta ? Alta(item, system) : Anulacion(item, system))));
    }

    private static XElement Alta(Item item, BillingSystemInfo system)
    {
        var (record, invoice) = (item.Record, item.Invoice);
        var element = new XElement(
            Sf + "RegistroAlta",
            new XElement(Sf + "IDVersion", "1.0"),
            IdFactura("IDFactura", "IDEmisorFactura", "NumSerieFactura", "FechaExpedicionFactura", record.IssuerNif, record.InvoiceNumber, record.IssueDate),
            new XElement(Sf + "NombreRazonEmisor", invoice.IssuerName),
            record.CorrectsSequence is null ? null : new XElement(Sf + "Subsanacion", "S"),
            record.CorrectsSequence is null ? null : new XElement(Sf + "RechazoPrevio", record.RejectedBefore ? "S" : "N"),
            new XElement(Sf + "TipoFactura", record.TypeCode));

        if (invoice.Kind == InvoiceKind.Rectificative)
        {
            var original = item.Rectified ?? throw new DomainException("Falta la factura original de la rectificativa.");
            element.Add(
                new XElement(Sf + "TipoRectificativa", "I"),
                new XElement(
                    Sf + "FacturasRectificadas",
                    IdFactura("IDFacturaRectificada", "IDEmisorFactura", "NumSerieFactura", "FechaExpedicionFactura", original.IssuerNif, original.FullNumber, BillingHash.FormatDate(original.IssueDate))));
        }

        element.Add(new XElement(Sf + "DescripcionOperacion", OperationDescription));

        if (invoice.RecipientNif is not null)
        {
            element.Add(new XElement(
                Sf + "Destinatarios",
                new XElement(Sf + "IDDestinatario", new XElement(Sf + "NombreRazon", invoice.RecipientName), new XElement(Sf + "NIF", invoice.RecipientNif))));
        }

        element.Add(new XElement(Sf + "Desglose", invoice.TaxBreakdown().Select(Detail)));

        // Mismo formato que en la huella: así el texto enviado es exactamente el que se resumió.
        element.Add(
            new XElement(Sf + "CuotaTotal", BillingHash.FormatAmount(record.TaxTotal)),
            new XElement(Sf + "ImporteTotal", BillingHash.FormatAmount(record.GrandTotal)),
            Chain(item.Previous),
            SystemElement(system),
            new XElement(Sf + "FechaHoraHusoGenRegistro", record.GeneratedAtText),
            new XElement(Sf + "TipoHuella", "01"),
            new XElement(Sf + "Huella", record.Hash));
        return element;
    }

    private static XElement Anulacion(Item item, BillingSystemInfo system)
    {
        var record = item.Record;
        return new XElement(
            Sf + "RegistroAnulacion",
            new XElement(Sf + "IDVersion", "1.0"),
            IdFactura("IDFactura", "IDEmisorFacturaAnulada", "NumSerieFacturaAnulada", "FechaExpedicionFacturaAnulada", record.IssuerNif, record.InvoiceNumber, record.IssueDate),
            record.CorrectsSequence is not null && record.RejectedBefore ? new XElement(Sf + "RechazoPrevio", "S") : null,
            Chain(item.Previous),
            SystemElement(system),
            new XElement(Sf + "FechaHoraHusoGenRegistro", record.GeneratedAtText),
            new XElement(Sf + "TipoHuella", "01"),
            new XElement(Sf + "Huella", record.Hash));
    }

    private static XElement Detail(TaxBreakdownItem b) =>
        new(
            Sf + "DetalleDesglose",
            new XElement(Sf + "Impuesto", "01"), // IVA
            new XElement(Sf + "ClaveRegimen", "01"), // régimen general
            new XElement(Sf + "CalificacionOperacion", "S1"), // sujeta y no exenta, sin inversión del sujeto pasivo
            new XElement(Sf + "TipoImpositivo", b.TaxRate.ToString("0.##", CultureInfo.InvariantCulture)),
            new XElement(Sf + "BaseImponibleOimporteNoSujeto", Amount(b.Base)),
            new XElement(Sf + "CuotaRepercutida", Amount(b.Tax)));

    private static XElement Chain(BillingRecord? previous) =>
        new(
            Sf + "Encadenamiento",
            previous is null
                ? new XElement(Sf + "PrimerRegistro", "S")
                : new XElement(
                    Sf + "RegistroAnterior",
                    new XElement(Sf + "IDEmisorFactura", previous.IssuerNif),
                    new XElement(Sf + "NumSerieFactura", previous.InvoiceNumber),
                    new XElement(Sf + "FechaExpedicionFactura", previous.IssueDate),
                    new XElement(Sf + "Huella", previous.Hash)));

    private static XElement SystemElement(BillingSystemInfo s) =>
        new(
            Sf + "SistemaInformatico",
            new XElement(Sf + "NombreRazon", s.ProducerName),
            new XElement(Sf + "NIF", s.ProducerNif),
            new XElement(Sf + "NombreSistemaInformatico", s.SystemName),
            new XElement(Sf + "IdSistemaInformatico", s.SystemId),
            new XElement(Sf + "Version", s.Version),
            new XElement(Sf + "NumeroInstalacion", s.InstallationNumber),
            new XElement(Sf + "TipoUsoPosibleSoloVerifactu", "S"),
            new XElement(Sf + "TipoUsoPosibleMultiOT", "N"),
            new XElement(Sf + "IndicadorMultiplesOT", "N"));

    private static XElement IdFactura(string wrapper, string issuer, string number, string date, string nif, string numSerie, string issueDate) =>
        new(Sf + wrapper, new XElement(Sf + issuer, nif), new XElement(Sf + number, numSerie), new XElement(Sf + date, issueDate));

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
