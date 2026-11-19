using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Printing;

/// <summary>
/// Impresora configurable sin recompilar. No guarda contraseñas ni secretos: el destino es solo el nombre
/// de la impresora de Windows, "equipo:puerto" o la carpeta de una impresora simulada.
/// </summary>
public sealed class Printer
{
    private Printer()
    {
        Name = string.Empty;
        Target = string.Empty;
    }

    public int Id { get; private set; }

    public string Name { get; private set; }

    public PrinterType Type { get; private set; }

    public PrinterConnection Connection { get; private set; }

    public string Target { get; private set; }

    public int PaperWidthMm { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset? LastSuccessAt { get; private set; }

    public DateTimeOffset? LastErrorAt { get; private set; }

    public string? LastError { get; private set; }

    public bool IsSimulated => Connection == PrinterConnection.Simulated;

    public static Printer Create(string name, PrinterType type, PrinterConnection connection, string target, int paperWidthMm)
    {
        var printer = new Printer { IsActive = true };
        printer.Update(name, type, connection, target, paperWidthMm);
        return printer;
    }

    public void Update(string name, PrinterType type, PrinterConnection connection, string target, int paperWidthMm)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80)
        {
            throw new DomainException("El nombre de la impresora es obligatorio (máximo 80 caracteres).");
        }

        if (!Enum.IsDefined(type) || !Enum.IsDefined(connection))
        {
            throw new DomainException("El tipo o la conexión de la impresora no son válidos.");
        }

        if (string.IsNullOrWhiteSpace(target) || target.Trim().Length > 200)
        {
            throw new DomainException("El destino de la impresora es obligatorio (máximo 200 caracteres).");
        }

        if (connection == PrinterConnection.Network && !IsHostPort(target.Trim()))
        {
            throw new DomainException("Para una impresora de red indique equipo:puerto, por ejemplo 192.168.1.50:9100.");
        }

        if (paperWidthMm is not (58 or 80))
        {
            throw new DomainException("El ancho de papel debe ser 58 u 80 mm.");
        }

        Name = name.Trim();
        Type = type;
        Connection = connection;
        Target = target.Trim();
        PaperWidthMm = paperWidthMm;
    }

    public void SetActive(bool active) => IsActive = active;

    public void RecordSuccess(DateTimeOffset now)
    {
        LastSuccessAt = now;
        LastError = null;
        LastErrorAt = null;
    }

    public void RecordError(string error, DateTimeOffset now)
    {
        LastErrorAt = now;
        LastError = error.Length > 500 ? error[..500] : error;
    }

    internal static bool IsHostPort(string value)
    {
        var index = value.LastIndexOf(':');
        return index > 0 &&
               index < value.Length - 1 &&
               int.TryParse(value[(index + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) &&
               port is > 0 and <= 65535;
    }
}
