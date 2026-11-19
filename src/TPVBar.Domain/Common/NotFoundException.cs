using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Common;

/// <summary>El registro solicitado no existe (o ya no está disponible).</summary>
public class NotFoundException : DomainException
{
    public NotFoundException()
    {
    }

    public NotFoundException(string message)
        : base(message)
    {
    }

    public NotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
