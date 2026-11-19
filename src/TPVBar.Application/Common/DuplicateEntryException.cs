namespace TPVBar.Application.Common;

/// <summary>Se ha intentado guardar un registro que viola una restricción de unicidad.</summary>
public class DuplicateEntryException : Exception
{
    public DuplicateEntryException()
        : base("Ya existe un registro con esos datos.")
    {
    }

    public DuplicateEntryException(string message)
        : base(message)
    {
    }

    public DuplicateEntryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
