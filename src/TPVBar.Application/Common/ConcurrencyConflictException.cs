namespace TPVBar.Application.Common;

/// <summary>Conflicto de concurrencia optimista: los datos cambiaron desde que se leyeron.</summary>
public class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException()
        : base("Los datos han sido modificados por otra operación.")
    {
    }

    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
