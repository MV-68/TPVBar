using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Catalog;

public sealed class Category
{
    private Category()
    {
        Name = string.Empty;
    }

    public int Id { get; private set; }

    public string Name { get; private set; }

    public int DisplayOrder { get; private set; }

    public bool IsActive { get; private set; }

    public static Category Create(string name, int displayOrder)
    {
        var category = new Category { IsActive = true };
        category.Update(name, displayOrder);
        return category;
    }

    public void Update(string name, int displayOrder)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("El nombre de la categoría es obligatorio.");
        }

        if (name.Trim().Length > 80)
        {
            throw new DomainException("El nombre de la categoría no puede superar 80 caracteres.");
        }

        Name = name.Trim();
        DisplayOrder = displayOrder;
    }

    public void SetActive(bool active) => IsActive = active;
}
