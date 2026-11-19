using TPVBar.Application.Common;

namespace TPVBar.Web.Realtime;

/// <summary>
/// Bus de eventos dentro del proceso para las pantallas Blazor Server. Un circuito de Blazor Server ya es
/// una conexión SignalR con el servidor; abrir otra desde cada página sería redundante. Los clientes que
/// no son Blazor (otras pantallas, el agente) usan <see cref="OperationsHub"/>.
/// </summary>
public sealed partial class OperationEventBus(ILogger<OperationEventBus> logger)
{
    private readonly object _gate = new();
    private readonly List<Func<OperationEvent, Task>> _handlers = [];

    public IDisposable Subscribe(Func<OperationEvent, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers.Add(handler);
        }

        return new Subscription(this, handler);
    }

    public async Task PublishAsync(OperationEvent operationEvent)
    {
        Func<OperationEvent, Task>[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _handlers];
        }

        foreach (var handler in snapshot)
        {
            try
            {
                await handler(operationEvent);
            }
            catch (Exception ex)
            {
                // Un suscriptor que falla (circuito cerrado, etc.) no debe afectar a los demás.
                LogHandlerFailed(logger, ex);
            }
        }
    }

    private void Unsubscribe(Func<OperationEvent, Task> handler)
    {
        lock (_gate)
        {
            _handlers.Remove(handler);
        }
    }

    [LoggerMessage(EventId = 400, Level = LogLevel.Debug, Message = "Un suscriptor de eventos ha fallado.")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);

    private sealed class Subscription(OperationEventBus bus, Func<OperationEvent, Task> handler) : IDisposable
    {
        public void Dispose() => bus.Unsubscribe(handler);
    }
}
