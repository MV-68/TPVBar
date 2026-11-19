using Microsoft.AspNetCore.SignalR;
using TPVBar.Application.Common;

namespace TPVBar.Web.Realtime;

/// <summary>Publica los eventos de un caso de uso ya confirmado: al bus del proceso y a los grupos del hub.</summary>
public sealed partial class OperationEventPublisher(
    OperationEventBus bus,
    IHubContext<OperationsHub> hub,
    ILogger<OperationEventPublisher> logger)
{
    public async Task PublishAsync(IEnumerable<OperationEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        // Un mismo aviso repetido en una operación no aporta nada: se envía una sola vez.
        foreach (var operationEvent in events.Distinct())
        {
            await bus.PublishAsync(operationEvent);

            try
            {
                await hub.Clients.Groups(GroupsFor(operationEvent.Name)).SendAsync(operationEvent.Name, operationEvent);
            }
            catch (Exception ex)
            {
                // El estado ya está guardado: perder un aviso solo retrasa la actualización (hay refresco de respaldo).
                LogHubFailed(logger, operationEvent.Name, ex);
            }
        }
    }

    private static string[] GroupsFor(string name) => name switch
    {
        OperationEventNames.KitchenTicketCreated or OperationEventNames.KitchenTicketStatusChanged =>
            [OperationsHub.KitchenGroup, OperationsHub.FloorGroup],
        OperationEventNames.PaymentRegistered or OperationEventNames.CashSessionChanged =>
            [OperationsHub.FloorGroup],
        OperationEventNames.PrintJobCreated or OperationEventNames.PrinterStatusChanged =>
            [OperationsHub.PrintingGroup, OperationsHub.FloorGroup],
        _ => [OperationsHub.FloorGroup, OperationsHub.KitchenGroup],
    };

    [LoggerMessage(EventId = 401, Level = LogLevel.Warning, Message = "No se pudo enviar el evento {EventName} por SignalR.")]
    private static partial void LogHubFailed(ILogger logger, string eventName, Exception exception);
}
