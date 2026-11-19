using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Common;

/// <summary>Falta un permiso para la operación solicitada.</summary>
public class ForbiddenException : DomainException
{
    public ForbiddenException()
        : base("No tiene permiso para realizar esta operación.")
    {
    }

    public ForbiddenException(string message)
        : base(message)
    {
    }

    public ForbiddenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Usuario que ejecuta el caso de uso, con los permisos de su sesión.</summary>
public interface ICurrentUser
{
    Guid UserId { get; }

    string? Workstation { get; }

    bool Has(Permission permission);
}

public static class CurrentUserExtensions
{
    public static void Require(this ICurrentUser user, Permission permission)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.Has(permission))
        {
            throw new ForbiddenException();
        }
    }
}

/// <summary>
/// Implementación por ámbito (scope). El host la rellena una vez por operación con la identidad de la
/// sesión; mientras no se rellene, cualquier uso falla (denegado por defecto).
/// </summary>
public sealed class CurrentUserAccessor : ICurrentUser
{
    private HashSet<Permission> _permissions = [];
    private Guid? _userId;

    public Guid UserId => _userId ?? throw new ForbiddenException("No hay una sesión iniciada.");

    public string? Workstation { get; private set; }

    public void Set(Guid userId, IEnumerable<Permission> permissions, string? workstation)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        _userId = userId;
        _permissions = [.. permissions];
        Workstation = workstation;
    }

    public bool Has(Permission permission) => _userId is not null && _permissions.Contains(permission);
}
