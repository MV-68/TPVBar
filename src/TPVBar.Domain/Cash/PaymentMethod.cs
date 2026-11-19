using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Cash;

/// <summary>
/// Método de pago configurable. Registrar un cobro con «Tarjeta» NO equivale a una integración bancaria:
/// solo anota que el cliente pagó así; el datáfono funciona por su cuenta.
/// </summary>
public sealed class PaymentMethod
{
    private PaymentMethod()
    {
        Code = string.Empty;
        Name = string.Empty;
    }

    public int Id { get; private set; }

    /// <summary>Código estable en mayúsculas (CASH, CARD, OTHER...). No se reutiliza.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    /// <summary>Exige anotar una referencia (p. ej. nº de operación del datáfono).</summary>
    public bool RequiresReference { get; private set; }

    /// <summary>El dinero entra y sale del cajón: cuenta para el efectivo esperado y admite «entregado/cambio».</summary>
    public bool AffectsCashDrawer { get; private set; }

    public bool IsActive { get; private set; }

    public int DisplayOrder { get; private set; }

    public static PaymentMethod Create(string code, string name, bool requiresReference, bool affectsCashDrawer, int displayOrder)
    {
        var method = new PaymentMethod { IsActive = true };
        method.SetCode(code);
        method.Update(name, requiresReference, affectsCashDrawer, displayOrder);
        return method;
    }

    public void Update(string name, bool requiresReference, bool affectsCashDrawer, int displayOrder)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 60)
        {
            throw new DomainException("El nombre del método de pago es obligatorio (máximo 60 caracteres).");
        }

        Name = name.Trim();
        RequiresReference = requiresReference;
        AffectsCashDrawer = affectsCashDrawer;
        DisplayOrder = displayOrder;
    }

    public void SetActive(bool active) => IsActive = active;

    private void SetCode(string code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length is 0 or > 30 || !normalized.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new DomainException("El código del método de pago solo admite letras, números y guion bajo (máximo 30).");
        }

        Code = normalized;
    }
}
