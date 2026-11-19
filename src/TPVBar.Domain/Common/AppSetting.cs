using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Common;

/// <summary>Claves de configuración general conocidas.</summary>
public static class AppSettingKeys
{
    /// <summary>
    /// Permite que el stock de un producto controlado quede por debajo de cero. Desactivado por defecto: si el
    /// stock no alcanza, el envío se rechaza. Solo lo cambia quien tiene permiso de configuración.
    /// </summary>
    public const string AllowNegativeStock = "stock.allow_negative";

    public const string InvoiceIssuerName = "invoice.issuer.name";
    public const string InvoiceIssuerNif = "invoice.issuer.nif";
    public const string InvoiceIssuerAddress = "invoice.issuer.address";

    /// <summary>Importe máximo (IVA incluido) de una factura simplificada. Por defecto 400 €; verificar con el asesor.</summary>
    public const string InvoiceSimplifiedLimit = "invoice.simplified_limit";

    /// <summary>URL base del QR de cotejo. Por defecto la de preproducción de la AEAT mientras no haya envío real.</summary>
    public const string InvoiceQrBaseUrl = "invoice.qr_base_url";

    /// <summary>Productor del sistema informático (el que firma la declaración responsable): nombre, NIF e identificador de 2 caracteres.</summary>
    public const string BillingProducerName = "billing.producer.name";
    public const string BillingProducerNif = "billing.producer.nif";
    public const string BillingSystemId = "billing.system.id";

    /// <summary>Número de instalación (único por instalación). Se genera la primera vez que hace falta.</summary>
    public const string BillingInstallationNumber = "billing.installation_number";
}

/// <summary>Ajuste de configuración general (clave/valor).</summary>
public sealed class AppSetting
{
    private AppSetting()
    {
        Key = string.Empty;
        Value = string.Empty;
    }

    public AppSetting(string key, string value, Guid? updatedBy, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 80)
        {
            throw new DomainException("La clave del ajuste no es válida.");
        }

        Key = key;
        Value = value ?? string.Empty;
        UpdatedBy = updatedBy;
        UpdatedAt = now;
    }

    public string Key { get; private set; }

    public string Value { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Change(string value, Guid? updatedBy, DateTimeOffset now)
    {
        Value = value ?? string.Empty;
        UpdatedBy = updatedBy;
        UpdatedAt = now;
    }
}
