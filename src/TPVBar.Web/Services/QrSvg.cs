using QRCoder;

namespace TPVBar.Web.Services;

/// <summary>Genera el QR (ISO/IEC 18004, corrección de errores M) como SVG, para mostrarlo en la factura.</summary>
public static class QrSvg
{
    public static string Render(string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(4, "#000000", "#ffffff", drawQuietZones: true);
    }
}
