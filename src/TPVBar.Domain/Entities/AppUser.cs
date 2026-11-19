using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Security;

namespace TPVBar.Domain.Entities;

/// <summary>Trabajador con acceso al sistema. Nunca almacena la contraseña, solo su hash.</summary>
public sealed class AppUser
{
    private readonly List<UserRole> _userRoles = [];

    private AppUser()
    {
        UserName = string.Empty;
        NormalizedUserName = string.Empty;
        DisplayName = string.Empty;
        PasswordHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public string UserName { get; private set; }

    public string NormalizedUserName { get; private set; }

    public string DisplayName { get; private set; }

    public string PasswordHash { get; private set; }

    public bool IsActive { get; private set; }

    public int FailedAccessCount { get; private set; }

    public DateTimeOffset? LockoutEnd { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Token de concurrencia optimista (columna de sistema xmin de PostgreSQL).</summary>
    public uint RowVersion { get; private set; }

    public IReadOnlyCollection<UserRole> UserRoles => _userRoles;

    public static string NormalizeUserName(string userName) =>
        (userName ?? string.Empty).Trim().ToUpperInvariant();

    public static AppUser Create(string userName, string displayName, string passwordHash, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new DomainException("El nombre de usuario es obligatorio.");
        }

        if (userName.Trim().Length > 50)
        {
            throw new DomainException("El nombre de usuario no puede superar 50 caracteres.");
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new DomainException("El nombre para mostrar es obligatorio.");
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new DomainException("Falta el hash de la contraseña.");
        }

        return new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = userName.Trim(),
            NormalizedUserName = NormalizeUserName(userName),
            DisplayName = displayName.Trim(),
            PasswordHash = passwordHash,
            IsActive = true,
            CreatedAt = now,
        };
    }

    public bool IsLockedOut(DateTimeOffset now) => LockoutEnd is { } end && end > now;

    /// <summary>Registra un intento fallido y, si se alcanza el límite, bloquea la cuenta.</summary>
    /// <returns><see langword="true"/> si este intento ha provocado el bloqueo.</returns>
    public bool RegisterFailedAccess(DateTimeOffset now, LockoutPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Un bloqueo ya vencido reinicia el contador.
        if (LockoutEnd is { } end && end <= now)
        {
            LockoutEnd = null;
            FailedAccessCount = 0;
        }

        FailedAccessCount++;
        if (FailedAccessCount >= policy.MaxFailedAttempts)
        {
            LockoutEnd = now + policy.Duration;
            FailedAccessCount = 0;
            return true;
        }

        return false;
    }

    public void RegisterSuccessfulAccess(DateTimeOffset now)
    {
        FailedAccessCount = 0;
        LockoutEnd = null;
        LastLoginAt = now;
    }

    public void SetPasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new DomainException("Falta el hash de la contraseña.");
        }

        PasswordHash = passwordHash;
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    public void AssignRole(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (_userRoles.Exists(r => r.RoleId == role.Id))
        {
            return;
        }

        _userRoles.Add(new UserRole(Id, role.Id));
    }
}
