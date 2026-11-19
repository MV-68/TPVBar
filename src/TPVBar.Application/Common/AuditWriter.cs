using System.Text.Json;
using TPVBar.Application.Abstractions;
using TPVBar.Domain.Auditing;

namespace TPVBar.Application.Common;

/// <summary>Registra eventos de auditoría con el usuario y el puesto actuales, en la misma transacción del caso de uso.</summary>
public sealed class AuditWriter(IAuditLog audit, ICurrentUser user, TimeProvider time)
{
    public void Write(string action, string entityType, object entityId, object? details = null) =>
        audit.Add(AuditEvent.Create(
            action,
            entityType,
            Convert.ToString(entityId, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            time.GetUtcNow(),
            user.UserId,
            user.Workstation,
            details is null ? null : JsonSerializer.Serialize(details)));
}
