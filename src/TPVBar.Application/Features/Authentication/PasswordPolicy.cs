namespace TPVBar.Application.Features.Authentication;

/// <summary>Reglas mínimas de contraseña. Se validan siempre en servidor.</summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 10;
    public const int MaximumLength = 128;

    public static IReadOnlyList<string> Validate(string userName, string? password)
    {
        var errors = new List<string>();

        if (string.IsNullOrEmpty(password))
        {
            errors.Add("La contraseña es obligatoria.");
            return errors;
        }

        if (password.Length < MinimumLength)
        {
            errors.Add($"La contraseña debe tener al menos {MinimumLength} caracteres.");
        }

        if (password.Length > MaximumLength)
        {
            errors.Add($"La contraseña no puede superar {MaximumLength} caracteres.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            errors.Add("La contraseña no puede estar formada solo por espacios.");
        }

        if (!string.IsNullOrWhiteSpace(userName) &&
            password.Contains(userName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("La contraseña no puede contener el nombre de usuario.");
        }

        if (password.Distinct().Count() < 4)
        {
            errors.Add("La contraseña es demasiado repetitiva.");
        }

        return errors;
    }
}
