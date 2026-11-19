using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Catalog;

/// <summary>
/// Tipo impositivo configurable con vigencia temporal. Nunca se codifica como constante:
/// cada línea de venta guarda el porcentaje vigente en el momento de la venta.
/// </summary>
public sealed class TaxRate
{
    private TaxRate()
    {
        Name = string.Empty;
    }

    public int Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>Porcentaje (0–100).</summary>
    public decimal Rate { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public bool IsActive { get; private set; }

    public static TaxRate Create(string name, decimal ratePercent, DateOnly validFrom, DateOnly? validTo = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("El nombre del tipo impositivo es obligatorio.");
        }

        if (ratePercent is < 0 or > 100)
        {
            throw new DomainException("El porcentaje debe estar entre 0 y 100.");
        }

        if (validTo is { } end && end < validFrom)
        {
            throw new DomainException("La fecha de fin de vigencia no puede ser anterior a la de inicio.");
        }

        return new TaxRate { Name = name.Trim(), Rate = ratePercent, ValidFrom = validFrom, ValidTo = validTo, IsActive = true };
    }

    public bool IsValidOn(DateOnly date) => IsActive && date >= ValidFrom && (ValidTo is null || date <= ValidTo);

    public void EndValidity(DateOnly lastDay)
    {
        if (lastDay < ValidFrom)
        {
            throw new DomainException("La fecha de fin no puede ser anterior al inicio de la vigencia.");
        }

        ValidTo = lastDay;
    }
}
