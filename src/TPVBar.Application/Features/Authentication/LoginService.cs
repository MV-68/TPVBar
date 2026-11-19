using System.Text.Json;
using Microsoft.Extensions.Logging;
using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Entities;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Authentication;

/// <summary>
/// Caso de uso de inicio de sesión: verifica credenciales, aplica el bloqueo por intentos fallidos
/// y audita cada resultado. No revela si un usuario existe.
/// </summary>
public sealed partial class LoginService(
    IUserStore users,
    IPasswordHasher hasher,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider time,
    LockoutPolicy lockoutPolicy,
    ILogger<LoginService> logger)
{
    // Con N intentos simultáneos, en cada ronda al menos uno guarda: 10 cubre de sobra un puesto de bar.
    private const int MaxConcurrencyRetries = 10;

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userName = request.UserName?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;

        if (userName.Length == 0 || password.Length == 0)
        {
            hasher.VerifyAgainstDummy(password);
            return LoginResult.Fail(LoginFailure.InvalidCredentials);
        }

        var user = await users.FindByUserNameAsync(userName, cancellationToken);
        if (user is null)
        {
            hasher.VerifyAgainstDummy(password);
            await AuditAsync(AuditActions.LoginFailed, "user", userName, null, request.Workstation, "unknown_user", cancellationToken);
            return LoginResult.Fail(LoginFailure.InvalidCredentials);
        }

        if (user.IsLockedOut(time.GetUtcNow()))
        {
            // No se comprueba la contraseña: un atacante no puede seguir probando durante el bloqueo.
            await AuditAsync(AuditActions.LoginBlocked, "user", user.Id.ToString(), user.Id, request.Workstation, "locked_out", cancellationToken);
            return LoginResult.Fail(LoginFailure.LockedOut);
        }

        var verification = hasher.Verify(user.PasswordHash, password);
        if (verification == PasswordVerification.Failed || !user.IsActive)
        {
            if (!user.IsActive)
            {
                hasher.VerifyAgainstDummy(password);
            }

            return await RegisterFailureAsync(user, request.Workstation, cancellationToken);
        }

        await RegisterSuccessAsync(user, verification == PasswordVerification.SuccessRehashNeeded ? password : null, cancellationToken);

        var access = await users.GetAccessAsync(user.Id, cancellationToken);
        await AuditAsync(AuditActions.LoginSucceeded, "user", user.Id.ToString(), user.Id, request.Workstation, null, cancellationToken);

        return new LoginResult(true, LoginFailure.None, user.Id, user.UserName, user.DisplayName, access.Roles, access.Permissions);
    }

    public async Task LogoutAsync(Guid userId, string? workstation, CancellationToken cancellationToken) =>
        await AuditAsync(AuditActions.Logout, "user", userId.ToString(), userId, workstation, null, cancellationToken);

    private async Task<LoginResult> RegisterFailureAsync(AppUser user, string? workstation, CancellationToken cancellationToken)
    {
        var lockedNow = false;
        await SaveWithRetryAsync(
            user,
            () => lockedNow = user.RegisterFailedAccess(time.GetUtcNow(), lockoutPolicy),
            cancellationToken);

        await AuditAsync(AuditActions.LoginFailed, "user", user.Id.ToString(), user.Id, workstation, "bad_credentials", cancellationToken);
        if (lockedNow)
        {
            await AuditAsync(AuditActions.AccountLocked, "user", user.Id.ToString(), user.Id, workstation, null, cancellationToken);
            LogAccountLocked(logger, user.Id);
        }

        return LoginResult.Fail(lockedNow ? LoginFailure.LockedOut : LoginFailure.InvalidCredentials);
    }

    private Task RegisterSuccessAsync(AppUser user, string? passwordToRehash, CancellationToken cancellationToken) =>
        SaveWithRetryAsync(
            user,
            () =>
            {
                user.RegisterSuccessfulAccess(time.GetUtcNow());
                if (passwordToRehash is not null)
                {
                    user.SetPasswordHash(hasher.Hash(passwordToRehash));
                }
            },
            cancellationToken);

    /// <summary>
    /// Aplica el cambio y guarda. Si otro proceso modificó al usuario a la vez (p. ej. dos intentos
    /// fallidos simultáneos), relee y reaplica para no perder incrementos del contador de bloqueo.
    /// </summary>
    private async Task SaveWithRetryAsync(AppUser user, Action mutate, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            mutate();
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (ConcurrencyConflictException) when (attempt < MaxConcurrencyRetries)
            {
                await users.ReloadAsync(user, cancellationToken);
            }
        }
    }

    private async Task AuditAsync(
        string action,
        string entityType,
        string entityId,
        Guid? userId,
        string? workstation,
        string? reason,
        CancellationToken cancellationToken)
    {
        // Nunca se incluye la contraseña ni su hash. El nombre de usuario intentado sí se guarda
        // en entityId cuando no existe, para poder detectar ataques.
        var details = reason is null ? null : JsonSerializer.Serialize(new { reason });
        audit.Add(AuditEvent.Create(action, entityType, entityId, time.GetUtcNow(), userId, workstation, details));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Warning, Message = "Cuenta {UserId} bloqueada temporalmente por intentos fallidos.")]
    private static partial void LogAccountLocked(ILogger logger, Guid userId);
}
