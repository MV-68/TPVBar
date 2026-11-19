using Microsoft.EntityFrameworkCore;
using TPVBar.Application.Abstractions;
using TPVBar.Domain.Entities;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Security;

namespace TPVBar.Infrastructure.Persistence;

internal sealed class EfUserStore(TpvBarDbContext db) : IUserStore
{
    public Task<AppUser?> FindByUserNameAsync(string userName, CancellationToken cancellationToken)
    {
        var normalized = AppUser.NormalizeUserName(userName);
        return db.Users.SingleOrDefaultAsync(u => u.NormalizedUserName == normalized, cancellationToken);
    }

    public Task<bool> UserNameExistsAsync(string userName, CancellationToken cancellationToken)
    {
        var normalized = AppUser.NormalizeUserName(userName);
        return db.Users.AnyAsync(u => u.NormalizedUserName == normalized, cancellationToken);
    }

    public async Task<UserAccess> GetAccessAsync(Guid userId, CancellationToken cancellationToken)
    {
        var roleIds = db.UserRoles.Where(r => r.UserId == userId).Select(r => r.RoleId);

        var roles = await db.Roles
            .Where(r => roleIds.Contains(r.Id))
            .Select(r => r.Name)
            .OrderBy(n => n)
            .ToListAsync(cancellationToken);

        var permissions = await db.RolePermissions
            .Where(p => roleIds.Contains(p.RoleId))
            .Select(p => p.Permission)
            .Distinct()
            .ToListAsync(cancellationToken);

        return new UserAccess(roles, permissions.ToHashSet());
    }

    public async Task AddAsync(AppUser user, IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken)
    {
        var names = roleNames.Select(n => n.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var roles = await db.Roles.Where(r => names.Contains(r.Name)).ToListAsync(cancellationToken);

        var missing = names.Except(roles.Select(r => r.Name), StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new DomainException($"No existen los roles: {string.Join(", ", missing)}.");
        }

        foreach (var role in roles)
        {
            user.AssignRole(role);
        }

        db.Users.Add(user);
    }

    public Task ReloadAsync(AppUser user, CancellationToken cancellationToken) =>
        db.Entry(user).ReloadAsync(cancellationToken);
}
