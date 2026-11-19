using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Authentication;

public sealed record LoginRequest(string UserName, string Password, string? Workstation);

public enum LoginFailure
{
    None = 0,

    /// <summary>Usuario inexistente, contraseña incorrecta o cuenta inactiva (no se distingue a propósito).</summary>
    InvalidCredentials = 1,

    LockedOut = 2,
}

public sealed record LoginResult(
    bool Succeeded,
    LoginFailure Failure,
    Guid? UserId,
    string? UserName,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlySet<Permission> Permissions)
{
    public static LoginResult Fail(LoginFailure failure) =>
        new(false, failure, null, null, null, [], new HashSet<Permission>());
}
