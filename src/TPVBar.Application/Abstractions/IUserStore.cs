using TPVBar.Domain.Entities;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Abstractions;

/// <summary>Roles y permisos efectivos de un usuario.</summary>
public sealed record UserAccess(IReadOnlyList<string> Roles, IReadOnlySet<Permission> Permissions);

/// <summary>Acceso a usuarios. Lo implementa la infraestructura.</summary>
public interface IUserStore
{
    Task<AppUser?> FindByUserNameAsync(string userName, CancellationToken cancellationToken);

    Task<UserAccess> GetAccessAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> UserNameExistsAsync(string userName, CancellationToken cancellationToken);

    /// <summary>Añade el usuario con los roles indicados (por nombre). Falla si algún rol no existe.</summary>
    Task AddAsync(AppUser user, IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken);

    /// <summary>Vuelve a leer el usuario desde la base de datos (tras un conflicto de concurrencia).</summary>
    Task ReloadAsync(AppUser user, CancellationToken cancellationToken);
}
