using System.Security.Claims;
using TPVBar.Domain.Security;

namespace TPVBar.Web.Security;

/// <summary>Lee de la sesión (cookie) el identificador de usuario y sus permisos.</summary>
public static class PrincipalReader
{
    public static bool TryRead(ClaimsPrincipal principal, out Guid userId, out List<Permission> permissions)
    {
        ArgumentNullException.ThrowIfNull(principal);
        permissions = [];
        userId = Guid.Empty;
        if (principal.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out userId))
        {
            return false;
        }

        foreach (var claim in principal.FindAll(TpvClaimTypes.Permission))
        {
            if (Enum.TryParse<Permission>(claim.Value, out var permission))
            {
                permissions.Add(permission);
            }
        }

        return true;
    }
}
