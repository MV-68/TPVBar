namespace TPVBar.Domain.Common;

/// <summary>
/// Marca de una operación ya ejecutada con una clave de idempotencia. Se guarda en la misma transacción
/// que la operación: repetir la clave (doble clic, reintento) no la vuelve a ejecutar.
/// </summary>
public sealed class IdempotencyRecord
{
    private IdempotencyRecord()
    {
        Key = string.Empty;
        Operation = string.Empty;
    }

    public long Id { get; private set; }

    public string Key { get; private set; }

    public string Operation { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static IdempotencyRecord Create(Guid key, string operation, DateTimeOffset now) =>
        new() { Key = key.ToString("N"), Operation = operation, CreatedAt = now };
}
