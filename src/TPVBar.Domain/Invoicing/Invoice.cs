using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Sales;

namespace TPVBar.Domain.Invoicing;

public enum InvoiceKind
{
    /// <summary>Factura simplificada (tipo F2): sustituye al ticket; no lleva datos del cliente.</summary>
    Simplified = 1,

    /// <summary>Factura completa (tipo F1): con datos del cliente.</summary>
    Complete = 2,

    /// <summary>Factura rectificativa por diferencias (R1 de completa, R5 de simplificada). Importes en negativo.</summary>
    Rectificative = 3,
}

/// <summary>Datos del emisor, tal como estaban en el momento de emitir (no cambian si luego se edita la configuración).</summary>
public sealed record InvoiceIssuer(string Name, string Nif, string Address)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 120)
        {
            throw new DomainException("Falta el nombre o razón social del emisor (máximo 120 caracteres). Configúrelo en Facturación.");
        }

        if (!SpanishTaxId.IsValid(Nif))
        {
            throw new DomainException("El NIF del emisor no es válido. Configúrelo en Facturación.");
        }

        if (string.IsNullOrWhiteSpace(Address) || Address.Length > 250)
        {
            throw new DomainException("Falta el domicilio del emisor (máximo 250 caracteres). Configúrelo en Facturación.");
        }
    }
}

public sealed record InvoiceRecipient(string Name, string Nif, string Address)
{
    public InvoiceRecipient Validated()
    {
        var name = Name?.Trim() ?? string.Empty;
        var address = Address?.Trim() ?? string.Empty;
        var nif = SpanishTaxId.Normalize(Nif);
        if (name.Length is 0 or > 120)
        {
            throw new DomainException("El nombre o razón social del cliente es obligatorio (máximo 120 caracteres).");
        }

        if (!SpanishTaxId.IsValid(nif))
        {
            throw new DomainException("El NIF del cliente no es válido (solo se admiten NIF, NIE y CIF españoles).");
        }

        if (address.Length is 0 or > 250)
        {
            throw new DomainException("El domicilio del cliente es obligatorio (máximo 250 caracteres).");
        }

        return new InvoiceRecipient(name, nif, address);
    }
}

/// <summary>Lo ya rectificado de una línea original (para no rectificar más de lo facturado).</summary>
public sealed record RectifiedAmounts(decimal Quantity, decimal Total, decimal Base, decimal Tax);

public sealed record TaxBreakdownItem(decimal TaxRate, decimal Base, decimal Tax, decimal Total);

/// <summary>
/// Factura emitida. Inmutable una vez creada (un trigger lo impide en la base de datos): los errores se corrigen con una
/// factura rectificativa o con un registro de anulación, nunca editando. Guarda una fotografía de emisor, cliente y líneas.
/// </summary>
public sealed class Invoice
{
    private readonly List<InvoiceLine> _lines = [];

    private Invoice()
    {
        Series = string.Empty;
        FullNumber = string.Empty;
        TypeCode = string.Empty;
        IssuerName = string.Empty;
        IssuerNif = string.Empty;
        IssuerAddress = string.Empty;
    }

    public long Id { get; private set; }

    public string Series { get; private set; }

    public int Number { get; private set; }

    /// <summary>Número completo (serie + número), el que aparece en el documento y en el registro de facturación.</summary>
    public string FullNumber { get; private set; }

    public InvoiceKind Kind { get; private set; }

    /// <summary>Código de tipo de factura (F1, F2, R1, R5).</summary>
    public string TypeCode { get; private set; }

    public long OrderId { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    /// <summary>Fecha de expedición (día en Europe/Madrid).</summary>
    public DateOnly IssueDate { get; private set; }

    public string IssuerName { get; private set; }

    public string IssuerNif { get; private set; }

    public string IssuerAddress { get; private set; }

    public string? RecipientName { get; private set; }

    public string? RecipientNif { get; private set; }

    public string? RecipientAddress { get; private set; }

    public long? RectifiedInvoiceId { get; private set; }

    public string? RectificationReason { get; private set; }

    /// <summary>Base imponible total (negativa en rectificativas).</summary>
    public decimal Base { get; private set; }

    public decimal Tax { get; private set; }

    public decimal Total { get; private set; }

    public Guid CreatedBy { get; private set; }

    public IReadOnlyCollection<InvoiceLine> Lines => _lines;

    public static string SeriesFor(InvoiceKind kind, int year) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(kind switch { InvoiceKind.Simplified => "S", InvoiceKind.Complete => "F", _ => "R" })}{year}");

    public static string FormatNumber(string series, int number) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{series}-{number:000000}");

    /// <summary>Emite la factura (simplificada o completa) de una comanda totalmente cobrada.</summary>
    public static Invoice IssueFromOrder(
        Order order,
        InvoiceKind kind,
        InvoiceIssuer issuer,
        InvoiceRecipient? recipient,
        int number,
        decimal simplifiedLimit,
        Guid createdBy,
        DateTimeOffset issuedAt,
        DateOnly issueDate)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(issuer);

        if (kind is not (InvoiceKind.Simplified or InvoiceKind.Complete))
        {
            throw new DomainException("Solo se pueden emitir facturas simplificadas o completas desde una comanda.");
        }

        if (order.Status != OrderStatus.Paid || order.Balance != 0)
        {
            throw new DomainException("Solo se factura una comanda totalmente cobrada.");
        }

        issuer.Validate();
        var activeLines = order.Lines.Where(l => l.IsActive).OrderBy(l => l.Id).ToList();
        if (activeLines.Count == 0 || order.GrandTotal <= 0)
        {
            throw new DomainException("La comanda no tiene importe que facturar.");
        }

        InvoiceRecipient? validated = null;
        if (kind == InvoiceKind.Complete)
        {
            validated = (recipient ?? throw new DomainException("La factura completa necesita los datos del cliente.")).Validated();
        }
        else if (order.GrandTotal > simplifiedLimit)
        {
            throw new DomainException($"El total ({order.GrandTotal:0.00} €) supera el límite de la factura simplificada ({simplifiedLimit:0.00} €): emita una factura completa con los datos del cliente.");
        }

        var series = SeriesFor(kind, issueDate.Year);
        var invoice = new Invoice
        {
            Series = series,
            Number = number,
            FullNumber = FormatNumber(series, number),
            Kind = kind,
            TypeCode = kind == InvoiceKind.Complete ? "F1" : "F2",
            OrderId = order.Id,
            IssuedAt = issuedAt,
            IssueDate = issueDate,
            IssuerName = issuer.Name.Trim(),
            IssuerNif = SpanishTaxId.Normalize(issuer.Nif),
            IssuerAddress = issuer.Address.Trim(),
            RecipientName = validated?.Name,
            RecipientNif = validated?.Nif,
            RecipientAddress = validated?.Address,
            CreatedBy = createdBy,
        };

        foreach (var line in activeLines)
        {
            var (net, tax) = Money.SplitGross(line.LineTotal, line.TaxRateSnapshot);
            invoice._lines.Add(InvoiceLine.Create(line.ProductNameSnapshot, line.Quantity, line.UnitPrice, line.TaxRateSnapshot, net, tax, line.LineTotal, line.Id, null));
        }

        invoice.SumTotals();
        if (invoice.Total != order.GrandTotal || invoice.Tax != order.TaxTotal || invoice.Base != order.Subtotal)
        {
            throw new DomainException("Los totales de la factura no coinciden con los de la comanda. No se emite.");
        }

        return invoice;
    }

    /// <summary>
    /// Rectificativa por diferencias: devuelve (en negativo) las cantidades indicadas de las líneas de la factura original.
    /// </summary>
    /// <param name="original">Factura simplificada o completa a rectificar.</param>
    /// <param name="quantities">Cantidad a rectificar por línea original (id de línea de factura).</param>
    /// <param name="alreadyRectified">Lo ya rectificado por línea original (rectificativas anteriores).</param>
    public static Invoice Rectify(
        Invoice original,
        IReadOnlyDictionary<long, decimal> quantities,
        IReadOnlyDictionary<long, RectifiedAmounts> alreadyRectified,
        string reason,
        int number,
        Guid createdBy,
        DateTimeOffset issuedAt,
        DateOnly issueDate)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(quantities);
        ArgumentNullException.ThrowIfNull(alreadyRectified);

        if (original.Kind == InvoiceKind.Rectificative)
        {
            throw new DomainException("No se rectifica una factura rectificativa: rectifique la factura original.");
        }

        var trimmedReason = reason?.Trim() ?? string.Empty;
        if (trimmedReason.Length is 0 or > 250)
        {
            throw new DomainException("Indique el motivo de la rectificación (máximo 250 caracteres).");
        }

        var selected = quantities.Where(q => q.Value != 0).ToList();
        if (selected.Count == 0)
        {
            throw new DomainException("Indique al menos una línea y cantidad a rectificar.");
        }

        var series = SeriesFor(InvoiceKind.Rectificative, issueDate.Year);
        var invoice = new Invoice
        {
            Series = series,
            Number = number,
            FullNumber = FormatNumber(series, number),
            Kind = InvoiceKind.Rectificative,
            TypeCode = original.Kind == InvoiceKind.Simplified ? "R5" : "R1",
            OrderId = original.OrderId,
            IssuedAt = issuedAt,
            IssueDate = issueDate,
            IssuerName = original.IssuerName,
            IssuerNif = original.IssuerNif,
            IssuerAddress = original.IssuerAddress,
            RecipientName = original.RecipientName,
            RecipientNif = original.RecipientNif,
            RecipientAddress = original.RecipientAddress,
            RectifiedInvoiceId = original.Id,
            RectificationReason = trimmedReason,
            CreatedBy = createdBy,
        };

        foreach (var (lineId, quantity) in selected.OrderBy(s => s.Key))
        {
            var line = original._lines.Find(l => l.Id == lineId)
                ?? throw new DomainException("Una de las líneas no pertenece a la factura original.");
            alreadyRectified.TryGetValue(lineId, out var done);
            done ??= new RectifiedAmounts(0, 0, 0, 0);

            var remaining = line.Quantity - done.Quantity;
            if (quantity < 0 || decimal.Round(quantity, 3) != quantity || quantity > remaining)
            {
                throw new DomainException($"«{line.Description}»: solo se pueden rectificar {remaining:0.###} unidades (cantidad pedida: {quantity:0.###}).");
            }

            decimal total;
            decimal net;
            decimal tax;
            if (quantity == remaining)
            {
                // Lo que queda exacto: así la suma de rectificaciones nunca difiere de la factura por céntimos.
                total = line.Total - done.Total;
                net = line.Base - done.Base;
                tax = line.Tax - done.Tax;
            }
            else
            {
                total = Money.Round(quantity * line.UnitPrice);
                (net, tax) = Money.SplitGross(total, line.TaxRate);
            }

            invoice._lines.Add(InvoiceLine.Create(line.Description, -quantity, line.UnitPrice, line.TaxRate, -net, -tax, -total, null, line.Id));
        }

        invoice.SumTotals();
        return invoice;
    }

    public IReadOnlyList<TaxBreakdownItem> TaxBreakdown() =>
    [
        .. _lines.GroupBy(l => l.TaxRate).OrderBy(g => g.Key)
            .Select(g => new TaxBreakdownItem(g.Key, g.Sum(l => l.Base), g.Sum(l => l.Tax), g.Sum(l => l.Total))),
    ];

    private void SumTotals()
    {
        Base = _lines.Sum(l => l.Base);
        Tax = _lines.Sum(l => l.Tax);
        Total = _lines.Sum(l => l.Total);
        if (Base + Tax != Total)
        {
            throw new DomainException("La factura no cuadra (base + IVA ≠ total).");
        }
    }
}

public sealed class InvoiceLine
{
    private InvoiceLine()
    {
        Description = string.Empty;
    }

    public long Id { get; private set; }

    public long InvoiceId { get; private set; }

    public string Description { get; private set; }

    /// <summary>Cantidad (negativa en rectificativas).</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Precio unitario con IVA incluido.</summary>
    public decimal UnitPrice { get; private set; }

    public decimal TaxRate { get; private set; }

    public decimal Base { get; private set; }

    public decimal Tax { get; private set; }

    public decimal Total { get; private set; }

    public long? OrderLineId { get; private set; }

    /// <summary>En una rectificativa, la línea de la factura original que se rectifica.</summary>
    public long? RectifiesLineId { get; private set; }

    internal static InvoiceLine Create(string description, decimal quantity, decimal unitPrice, decimal taxRate, decimal net, decimal tax, decimal total, long? orderLineId, long? rectifiesLineId) =>
        new()
        {
            Description = description,
            Quantity = quantity,
            UnitPrice = unitPrice,
            TaxRate = taxRate,
            Base = net,
            Tax = tax,
            Total = total,
            OrderLineId = orderLineId,
            RectifiesLineId = rectifiesLineId,
        };
}
