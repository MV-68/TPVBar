using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Floor;

public sealed class Zone
{
    private Zone()
    {
        Name = string.Empty;
    }

    public int Id { get; private set; }

    public string Name { get; private set; }

    public int DisplayOrder { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Zone Create(string name, int displayOrder, DateTimeOffset now)
    {
        var zone = new Zone { IsActive = true, CreatedAt = now };
        zone.Update(name, displayOrder);
        return zone;
    }

    public void Update(string name, int displayOrder)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("El nombre de la zona es obligatorio.");
        }

        if (name.Trim().Length > 80)
        {
            throw new DomainException("El nombre de la zona no puede superar 80 caracteres.");
        }

        Name = name.Trim();
        DisplayOrder = displayOrder;
    }

    public void SetActive(bool active) => IsActive = active;
}
