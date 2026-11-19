using System.Globalization;

namespace TPVBar.Domain.Invoicing;

/// <summary>
/// Validación de NIF/NIE/CIF españoles (formato y dígito de control). Solo comprueba que el identificador es
/// matemáticamente válido: NO consulta si existe ni si está censado en la AEAT. Los identificadores extranjeros
/// (VAT UE, pasaporte…) no se admiten todavía.
/// </summary>
public static class SpanishTaxId
{
    private const string PersonLetters = "TRWAGMYFPDXBNJZSQVHLCKE";
    private const string CifControlLetters = "JABCDEFGHI";
    private const string CifFirstLetters = "ABCDEFGHJNPQRSUVW";

    /// <summary>Mayúsculas y sin espacios ni guiones.</summary>
    public static string Normalize(string? value) =>
        (value ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    public static bool IsValid(string? value)
    {
        var id = Normalize(value);
        if (id.Length != 9)
        {
            return false;
        }

        if (char.IsAsciiDigit(id[0]))
        {
            return IsValidDni(id);
        }

        if (id[0] is 'X' or 'Y' or 'Z')
        {
            return IsValidNie(id);
        }

        if (id[0] is 'K' or 'L' or 'M')
        {
            return id[1..8].All(char.IsAsciiDigit) && id[8] == PersonLetters[int.Parse(id[1..8], CultureInfo.InvariantCulture) % 23];
        }

        return CifFirstLetters.Contains(id[0], StringComparison.Ordinal) && IsValidCif(id);
    }

    private static bool IsValidDni(string id) =>
        id[..8].All(char.IsAsciiDigit) && id[8] == PersonLetters[int.Parse(id[..8], CultureInfo.InvariantCulture) % 23];

    private static bool IsValidNie(string id)
    {
        if (!id[1..8].All(char.IsAsciiDigit))
        {
            return false;
        }

        var prefix = id[0] switch { 'X' => '0', 'Y' => '1', _ => '2' };
        var number = int.Parse(prefix + id[1..8], CultureInfo.InvariantCulture);
        return id[8] == PersonLetters[number % 23];
    }

    private static bool IsValidCif(string id)
    {
        var digits = id[1..8];
        if (!digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 7; i++)
        {
            var d = digits[i] - '0';
            if (i % 2 == 0)
            {
                d *= 2;
                d = (d / 10) + (d % 10);
            }

            sum += d;
        }

        var control = (10 - (sum % 10)) % 10;
        var last = id[8];
        var letterOnly = id[0] is 'P' or 'Q' or 'R' or 'S' or 'N' or 'W';
        var digitOnly = id[0] is 'A' or 'B' or 'E' or 'H';
        var matchesDigit = last == (char)('0' + control);
        var matchesLetter = last == CifControlLetters[control];

        if (letterOnly)
        {
            return matchesLetter;
        }

        return digitOnly ? matchesDigit : matchesDigit || matchesLetter;
    }
}
