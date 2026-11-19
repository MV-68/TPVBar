using Microsoft.EntityFrameworkCore;
using Npgsql;
using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Auditing;

namespace TPVBar.Infrastructure.Persistence;

/// <summary>Unidad de trabajo y registro de auditoría sobre el mismo <see cref="TpvBarDbContext"/> de la petición.</summary>
internal sealed class EfUnitOfWork(TpvBarDbContext db) : IUnitOfWork, IAuditLog
{
    public void Add(AuditEvent auditEvent) => db.AuditEvents.Add(auditEvent);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("Los datos han sido modificados por otra operación.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DuplicateEntryException("Ya existe un registro con esos datos.", ex);
        }
    }
}
