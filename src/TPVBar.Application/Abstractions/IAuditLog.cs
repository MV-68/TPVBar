using TPVBar.Domain.Auditing;

namespace TPVBar.Application.Abstractions;

/// <summary>Registro de auditoría. Los eventos se persisten en la siguiente llamada a <see cref="IUnitOfWork"/>.</summary>
public interface IAuditLog
{
    void Add(AuditEvent auditEvent);
}
