namespace TPVBar.Application.Abstractions;

/// <summary>Confirma en una única transacción todos los cambios pendientes.</summary>
public interface IUnitOfWork
{
    /// <exception cref="Common.ConcurrencyConflictException">Otro proceso modificó los mismos datos.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
