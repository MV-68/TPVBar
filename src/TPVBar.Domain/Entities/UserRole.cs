namespace TPVBar.Domain.Entities;

public sealed class UserRole
{
    // Constructor para EF Core.
    private UserRole()
    {
    }

    public UserRole(Guid userId, int roleId)
    {
        UserId = userId;
        RoleId = roleId;
    }

    public Guid UserId { get; private set; }

    public int RoleId { get; private set; }
}
