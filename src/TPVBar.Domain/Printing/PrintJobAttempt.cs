namespace TPVBar.Domain.Printing;

/// <summary>Detalle de cada intento de impresión (quién, cuándo y con qué resultado).</summary>
public sealed class PrintJobAttempt
{
    private PrintJobAttempt()
    {
        Agent = string.Empty;
    }

    public PrintJobAttempt(long jobId, int attemptNumber, string agent, bool success, DeliveryLevel level, string? error, DateTimeOffset now)
    {
        JobId = jobId;
        AttemptNumber = attemptNumber;
        Agent = agent;
        Success = success;
        DeliveryLevel = level;
        Error = error is { Length: > 1000 } ? error[..1000] : error;
        FinishedAt = now;
    }

    public long Id { get; private set; }

    public long JobId { get; private set; }

    public int AttemptNumber { get; private set; }

    public string Agent { get; private set; }

    public bool Success { get; private set; }

    public DeliveryLevel DeliveryLevel { get; private set; }

    public string? Error { get; private set; }

    public DateTimeOffset FinishedAt { get; private set; }
}

/// <summary>Última vez que un agente de impresión se puso en contacto con el servidor.</summary>
public sealed class PrintAgentHeartbeat
{
    private PrintAgentHeartbeat()
    {
        AgentName = string.Empty;
    }

    public PrintAgentHeartbeat(string agentName, string? version, DateTimeOffset now)
    {
        AgentName = agentName;
        Version = version;
        LastSeenAt = now;
    }

    public string AgentName { get; private set; }

    public string? Version { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public void Touch(string? version, DateTimeOffset now)
    {
        Version = version;
        LastSeenAt = now;
    }
}
