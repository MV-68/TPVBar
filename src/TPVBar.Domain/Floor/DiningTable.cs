using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Floor;

public sealed class DiningTable
{
    private DiningTable()
    {
        Name = string.Empty;
    }

    public int Id { get; private set; }

    public int ZoneId { get; private set; }

    public string Name { get; private set; }

    public int Capacity { get; private set; }

    public int DisplayOrder { get; private set; }

    public bool IsActive { get; private set; }

    public static DiningTable Create(int zoneId, string name, int capacity, int displayOrder)
    {
        var table = new DiningTable { ZoneId = zoneId, IsActive = true };
        table.Update(zoneId, name, capacity, displayOrder);
        return table;
    }

    public void Update(int zoneId, string name, int capacity, int displayOrder)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("El nombre de la mesa es obligatorio.");
        }

        if (name.Trim().Length > 40)
        {
            throw new DomainException("El nombre de la mesa no puede superar 40 caracteres.");
        }

        if (capacity is < 1 or > 99)
        {
            throw new DomainException("La capacidad debe estar entre 1 y 99.");
        }

        ZoneId = zoneId;
        Name = name.Trim();
        Capacity = capacity;
        DisplayOrder = displayOrder;
    }

    public void SetActive(bool active) => IsActive = active;
}
