using System.Globalization;
using System.Text;
using TPVBar.PrintContracts;

namespace TPVBar.PrintAgent.Printers;

/// <summary>Un ticket listo para enviar: bytes ESC/POS y su versión en texto plano (para simuladas y registros).</summary>
internal sealed record RenderedTicket(byte[] Bytes, string Text);

/// <summary>
/// Convierte un <see cref="PrintDocument"/> a comandos ESC/POS para impresoras térmicas de 80 o 58 mm.
/// Usa la página de códigos CP858 (acentos españoles y €). Los caracteres que la impresora no pueda
/// representar se sustituyen por «?». No depende de ningún dispositivo: se puede probar sin impresora.
/// </summary>
internal static class TicketRenderer
{
    private static readonly CultureInfo Spanish = new("es-ES");
    private static readonly Encoding Cp858 = CreateEncoding();
    private static readonly TimeZoneInfo Madrid = ResolveZone();

    public static int Columns(int paperWidthMm) => paperWidthMm <= 58 ? 32 : 48;

    public static RenderedTicket Render(PrintDocument document, int paperWidthMm, bool isRetry)
    {
        ArgumentNullException.ThrowIfNull(document);

        var width = Columns(paperWidthMm);
        var bytes = new List<byte>(512);
        var text = new StringBuilder();

        void Raw(params byte[] data) => bytes.AddRange(data);

        void Line(string value, bool center = false, bool big = false, bool bold = false)
        {
            var effectiveWidth = big ? width / 2 : width;
            // Una línea que ya cabe se respeta tal cual (conserva columnas alineadas); solo se ajusta la que no cabe.
            var parts = value.Length <= effectiveWidth ? [value] : Wrap(value, effectiveWidth);
            foreach (var wrapped in parts)
            {
                Raw(0x1B, 0x61, center ? (byte)1 : (byte)0);
                Raw(0x1D, 0x21, big ? (byte)0x11 : (byte)0x00);
                Raw(0x1B, 0x45, bold || big ? (byte)1 : (byte)0);
                bytes.AddRange(Cp858.GetBytes(wrapped));
                Raw(0x0A);

                var shown = center ? wrapped.PadLeft((effectiveWidth + wrapped.Length) / 2) : wrapped;
                text.AppendLine(shown);
            }
        }

        void Separator() => Line(new string('-', width));

        Raw(0x1B, 0x40);        // inicializar
        Raw(0x1B, 0x74, 0x13);  // página de códigos CP858

        if (isRetry)
        {
            Line("*** REIMPRESION / REINTENTO ***", center: true, bold: true);
            Line("Comprobar que no es un duplicado", center: true);
            Separator();
        }

        Line(document.Title, center: true, big: true);
        if (!string.IsNullOrWhiteSpace(document.Subtitle))
        {
            Line(document.Subtitle!, center: true);
        }

        if (!string.IsNullOrWhiteSpace(document.TableLabel))
        {
            Line(document.TableLabel!, center: true, big: true);
        }

        Line(TimeZoneInfo.ConvertTime(document.CreatedAt, Madrid).ToString("dd/MM/yyyy HH:mm", Spanish), center: true);
        Separator();

        foreach (var item in document.Lines)
        {
            RenderLine(document.Kind, item, width, Line);
        }

        if (document.Total is { } total)
        {
            Separator();
            Line(TwoColumns(document.Kind == PrintDocumentKind.CashClose ? "TOTAL NETO" : "TOTAL", Money(total), width), bold: true);
        }

        foreach (var section in document.Sections ?? [])
        {
            Separator();
            Line(section.Title, bold: true);
            foreach (var item in section.Lines)
            {
                Line(item.Amount is { } amount ? TwoColumns(item.Text, Money(amount), width) : item.Text);
            }
        }

        if (!string.IsNullOrWhiteSpace(document.Footer))
        {
            Separator();
            Line(document.Footer!, center: true);
        }

        Raw(0x0A, 0x0A, 0x0A);
        Raw(0x1D, 0x56, 0x42, 0x00); // avanzar y corte parcial

        return new RenderedTicket([.. bytes], text.ToString());
    }

    internal static IEnumerable<string> Wrap(string value, int width)
    {
        if (string.IsNullOrEmpty(value))
        {
            yield return string.Empty;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var remaining = word;
            while (remaining.Length > width)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                yield return remaining[..width];
                remaining = remaining[width..];
            }

            if (current.Length > 0 && current.Length + 1 + remaining.Length > width)
            {
                yield return current.ToString();
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(remaining);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static void RenderLine(PrintDocumentKind kind, PrintLine item, int width, Action<string, bool, bool, bool> line)
    {
        var quantity = item.Quantity is { } q ? q.ToString("0.###", CultureInfo.InvariantCulture) + "x " : string.Empty;

        switch (kind)
        {
            case PrintDocumentKind.KitchenTicket:
            case PrintDocumentKind.KitchenVoid:
                line(quantity + item.Text, false, true, true);
                if (!string.IsNullOrWhiteSpace(item.Detail))
                {
                    line((kind == PrintDocumentKind.KitchenVoid ? "Motivo: " : ">> ") + item.Detail, false, false, true);
                }

                break;

            case PrintDocumentKind.PreBill or PrintDocumentKind.Receipt when item.Amount is { } amount:
                line(TwoColumns(quantity + item.Text, Money(amount), width), false, false, false);
                if (!string.IsNullOrWhiteSpace(item.Detail))
                {
                    line("   " + item.Detail, false, false, false);
                }

                break;

            default:
                line(quantity + item.Text, false, false, false);
                if (!string.IsNullOrWhiteSpace(item.Detail))
                {
                    line("   " + item.Detail, false, false, false);
                }

                break;
        }
    }

    private static string TwoColumns(string left, string right, int width)
    {
        var space = width - right.Length - 1;
        if (space < 4)
        {
            return left + " " + right;
        }

        var trimmed = left.Length > space ? left[..space] : left;
        return trimmed.PadRight(width - right.Length) + right;
    }

    private static string Money(decimal value) => value.ToString("N2", Spanish) + " €";

    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(858, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
    }

    private static TimeZoneInfo ResolveZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        }
    }
}
