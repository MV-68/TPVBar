namespace TPVBar.PrintAgent;

/// <summary>
/// Configuración del agente (sección <c>Agent</c>). La clave de API es un SECRETO: va en User Secrets o en
/// variables de entorno (<c>Agent__ApiKey</c>), nunca en el repositorio ni en appsettings.json.
/// </summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    public string ServerUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Nombre del agente (aparece en auditoría y en el estado). Por defecto, el nombre del equipo.</summary>
    public string Name { get; set; } = string.Empty;

    public int PollIntervalSeconds { get; set; } = 2;

    public int BatchSize { get; set; } = 5;

    public string EffectiveName => string.IsNullOrWhiteSpace(Name) ? Environment.MachineName : Name;

    /// <summary>Devuelve los problemas de configuración (vacío si es válida).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            problems.Add("Agent:ServerUrl debe ser una dirección http(s) válida.");
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            problems.Add("Falta la clave de API (Agent:ApiKey). Defínala en User Secrets o en la variable de entorno Agent__ApiKey.");
        }

        if (PollIntervalSeconds is < 1 or > 60)
        {
            problems.Add("Agent:PollIntervalSeconds debe estar entre 1 y 60.");
        }

        if (BatchSize is < 1 or > 20)
        {
            problems.Add("Agent:BatchSize debe estar entre 1 y 20.");
        }

        return problems;
    }
}
