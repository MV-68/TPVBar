using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;

namespace TPVBar.Domain.Cash;

public enum CashSessionStatus
{
    Open = 1,
    Closed = 2,
}

/// <summary>
/// Sesión de caja: desde la apertura con fondo inicial hasta el cierre con recuento. Solo puede haber una
/// abierta a la vez (lo garantiza un índice único). El efectivo esperado lo calcula el servidor al cerrar.
/// </summary>
public sealed class CashSession
{
    private CashSession()
    {
    }

    public long Id { get; private set; }

    public Guid OpenedBy { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public decimal OpeningAmount { get; private set; }

    public Guid? ClosedBy { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public decimal? ExpectedCash { get; private set; }

    public decimal? CountedCash { get; private set; }

    public decimal? Difference { get; private set; }

    public CashSessionStatus Status { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>Última escritura contable (cobro, devolución o movimiento).</summary>
    public DateTimeOffset LastActivityAt { get; private set; }

    /// <summary>Token de concurrencia optimista (columna de sistema xmin de PostgreSQL).</summary>
    public uint RowVersion { get; private set; }

    public static CashSession Open(Guid openedBy, decimal openingAmount, DateTimeOffset now)
    {
        ValidateAmount(openingAmount, allowZero: true, "El fondo inicial");
        return new CashSession { OpenedBy = openedBy, OpenedAt = now, OpeningAmount = openingAmount, Status = CashSessionStatus.Open, LastActivityAt = now };
    }

    /// <summary>
    /// Toda escritura contable (cobro, devolución, movimiento) "toca" la sesión. Así un cierre simultáneo y un
    /// cobro no pueden cruzarse: el segundo falla por concurrencia, se repite y ve la caja ya cerrada (o el
    /// cobro ya contado). Evita que un cobro quede fuera del efectivo esperado.
    /// </summary>
    public void RegisterActivity(DateTimeOffset now)
    {
        if (Status != CashSessionStatus.Open)
        {
            throw new DomainException("La caja está cerrada.");
        }

        LastActivityAt = now;
    }

    /// <summary>Cierra la sesión guardando el esperado (calculado en servidor), el contado y la diferencia.</summary>
    public void Close(Guid closedBy, decimal countedCash, decimal expectedCash, string? notes, DateTimeOffset now)
    {
        if (Status != CashSessionStatus.Open)
        {
            throw new DomainException("La caja ya está cerrada.");
        }

        ValidateAmount(countedCash, allowZero: true, "El efectivo contado");
        Status = CashSessionStatus.Closed;
        ClosedBy = closedBy;
        ClosedAt = now;
        ExpectedCash = expectedCash;
        CountedCash = countedCash;
        Difference = countedCash - expectedCash;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : (notes.Trim().Length > 500 ? throw new DomainException("Las notas no pueden superar 500 caracteres.") : notes.Trim());
    }

    internal static void ValidateAmount(decimal amount, bool allowZero, string what)
    {
        if (amount < 0 || (!allowZero && amount == 0) || Money.Round(amount) != amount || amount > 9_999_999.99m)
        {
            throw new DomainException($"{what} debe ser {(allowZero ? "cero o positivo" : "mayor que cero")}, con un máximo de 2 decimales.");
        }
    }
}
