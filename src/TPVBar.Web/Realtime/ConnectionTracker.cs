using Microsoft.AspNetCore.Components.Server.Circuits;

namespace TPVBar.Web.Realtime;

/// <summary>
/// Cuenta las conexiones abiertas (para el diagnóstico): clientes del hub y circuitos de Blazor
/// (cada pantalla abierta es un circuito). Solo números: sin identidades.
/// </summary>
public sealed class ConnectionTracker
{
    private int _hub;
    private int _circuits;

    public int Count => Volatile.Read(ref _hub);

    public int Circuits => Volatile.Read(ref _circuits);

    public void Opened() => Interlocked.Increment(ref _hub);

    public void Closed() => DecrementNotBelowZero(ref _hub);

    public void CircuitOpened() => Interlocked.Increment(ref _circuits);

    public void CircuitClosed() => DecrementNotBelowZero(ref _circuits);

    private static void DecrementNotBelowZero(ref int counter)
    {
        // Nunca por debajo de cero aunque se notifique un cierre sin apertura.
        int current;
        do
        {
            current = Volatile.Read(ref counter);
            if (current == 0)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref counter, current - 1, current) != current);
    }
}

/// <summary>Avisa al contador cuando se abre o se cierra un circuito de Blazor Server.</summary>
public sealed class CircuitCountHandler(ConnectionTracker tracker) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        tracker.CircuitOpened();
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        tracker.CircuitClosed();
        return Task.CompletedTask;
    }
}
