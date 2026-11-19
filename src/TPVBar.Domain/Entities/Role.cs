using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Security;

namespace TPVBar.Domain.Entities;

/// <summary>Rol con un conjunto configurable de permisos.</summary>
public sealed class Role
{
    private readonly List<RolePermission> _permissions = [];

    private Role()
    {
        Name = string.Empty;
    }

    public int Id { get; private set; }

    public string Name { get; private set; }

    public IReadOnlyCollection<RolePermission> Permissions => _permissions;

    public static Role Create(string name, IEnumerable<Permission> permissions)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("El nombre del rol es obligatorio.");
        }

        var role = new Role { Name = name.Trim() };
        foreach (var permission in permissions.Distinct())
        {
            role._permissions.Add(new RolePermission(permission));
        }

        return role;
    }
}
