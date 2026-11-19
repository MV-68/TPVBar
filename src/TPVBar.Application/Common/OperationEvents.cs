using TPVBar.Domain.Catalog;

namespace TPVBar.Application.Common;

/// <summary>Nombres estables de los eventos de tiempo real.</summary>
public static class OperationEventNames
{
    public const string TableStatusChanged = "TableStatusChanged";
    public const string OrderUpdated = "OrderUpdated";
    public const string OrderLineAdded = "OrderLineAdded";
    public const string OrderSentToKitchen = "OrderSentToKitchen";
    public const string KitchenTicketCreated = "KitchenTicketCreated";
    public const string KitchenTicketStatusChanged = "KitchenTicketStatusChanged";
    public const string PrinterStatusChanged = "PrinterStatusChanged";
    public const string PrintJobCreated = "PrintJobCreated";
    public const string PaymentRegistered = "PaymentRegistered";
    public const string CashSessionChanged = "CashSessionChanged";
    public const string StockAlertRaised = "StockAlertRaised";
    public const string StockChanged = "StockChanged";
}

/// <summary>
/// Aviso de que algo ha cambiado. Lleva solo identificadores, nunca datos: quien lo recibe vuelve a leer
/// el estado de la base de datos. Por eso repetirlo, perderlo o recibirlo tras una reconexión no puede
/// duplicar ni corromper nada.
/// </summary>
public sealed record OperationEvent(
    string Name,
    long? OrderId = null,
    int? TableId = null,
    long? TicketId = null,
    PreparationStation? Station = null,
    int? ProductId = null);

public interface IOperationEvents
{
    void Raise(OperationEvent operationEvent);
}

/// <summary>
/// Acumula los eventos de un caso de uso. El host los publica SOLO si la operación termina bien
/// (después de confirmar la transacción); si falla, se descartan.
/// </summary>
public sealed class PendingOperationEvents : IOperationEvents
{
    private readonly List<OperationEvent> _events = [];

    public void Raise(OperationEvent operationEvent)
    {
        ArgumentNullException.ThrowIfNull(operationEvent);
        _events.Add(operationEvent);
    }

    public IReadOnlyList<OperationEvent> Drain()
    {
        var copy = _events.ToList();
        _events.Clear();
        return copy;
    }
}
