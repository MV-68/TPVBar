using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Auditing;

/// <summary>
/// Evento de auditoría. Es inmutable: no existe forma de modificarlo desde la aplicación
/// y la base de datos rechaza UPDATE, DELETE y TRUNCATE sobre su tabla.
/// </summary>
public sealed class AuditEvent
{
    private AuditEvent()
    {
        Action = string.Empty;
        EntityType = string.Empty;
        EntityId = string.Empty;
        DetailsJson = "{}";
    }

    public long Id { get; private set; }

    public Guid? UserId { get; private set; }

    public string Action { get; private set; }

    public string EntityType { get; private set; }

    public string EntityId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string? Workstation { get; private set; }

    /// <summary>Detalles en JSON. No debe contener contraseñas, hashes ni otros secretos.</summary>
    public string DetailsJson { get; private set; }

    public static AuditEvent Create(
        string action,
        string entityType,
        string entityId,
        DateTimeOffset occurredAt,
        Guid? userId = null,
        string? workstation = null,
        string? detailsJson = null)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new DomainException("La acción de auditoría es obligatoria.");
        }

        return new AuditEvent
        {
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OccurredAt = occurredAt,
            UserId = userId,
            Workstation = workstation,
            DetailsJson = string.IsNullOrWhiteSpace(detailsJson) ? "{}" : detailsJson,
        };
    }
}
