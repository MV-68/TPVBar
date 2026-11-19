using TPVBar.Domain.Security;

namespace TPVBar.Domain.Entities;

public sealed class RolePermission
{
    // Constructor para EF Core.
    private RolePermission()
    {
    }

    public RolePermission(Permission permission)
    {
        Permission = permission;
    }

    public int RoleId { get; private set; }

    public Permission Permission { get; private set; }
}
