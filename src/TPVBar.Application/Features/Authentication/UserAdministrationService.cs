using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Entities;
using TPVBar.Domain.Exceptions;

namespace TPVBar.Application.Features.Authentication;

/// <summary>Alta de usuarios. Aplica la política de contraseñas y deja rastro de auditoría.</summary>
public sealed class UserAdministrationService(
    IUserStore users,
    IPasswordHasher hasher,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    public async Task<Guid> CreateUserAsync(
        string userName,
        string displayName,
        string password,
        IReadOnlyCollection<string> roleNames,
        Guid? createdBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        var errors = PasswordPolicy.Validate(userName, password);
        if (errors.Count > 0)
        {
            throw new DomainException(string.Join(" ", errors));
        }

        if (roleNames.Count == 0)
        {
            throw new DomainException("El usuario debe tener al menos un rol.");
        }

        if (await users.UserNameExistsAsync(userName, cancellationToken))
        {
            throw new DomainException("Ya existe un usuario con ese nombre.");
        }

        var user = AppUser.Create(userName, displayName, hasher.Hash(password), time.GetUtcNow());
        await users.AddAsync(user, roleNames, cancellationToken);

        audit.Add(AuditEvent.Create(
            AuditActions.UserCreated,
            "user",
            user.Id.ToString(),
            time.GetUtcNow(),
            createdBy,
            detailsJson: System.Text.Json.JsonSerializer.Serialize(new { user.UserName, roles = roleNames })));

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateEntryException ex)
        {
            // Carrera entre dos altas simultáneas: el índice único de la base de datos es la última barrera.
            throw new DomainException("Ya existe un usuario con ese nombre.", ex);
        }

        return user.Id;
    }
}
