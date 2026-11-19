using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using TPVBar.PrintContracts;

namespace TPVBar.PrintAgent;

/// <summary>Cliente de la API del servidor. Autenticado con la clave del agente en cada petición.</summary>
internal sealed class ServerClient
{
    private readonly HttpClient _http;
    private readonly AgentOptions _options;

    public ServerClient(HttpClient http, IOptions<AgentOptions> options)
    {
        _options = options.Value;
        _http = http;
        _http.DefaultRequestHeaders.Remove(AgentApiRoutes.KeyHeader);
        _http.DefaultRequestHeaders.Add(AgentApiRoutes.KeyHeader, _options.ApiKey);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(CancellationToken cancellationToken)
    {
        var request = new ClaimRequest(_options.EffectiveName, typeof(ServerClient).Assembly.GetName().Version?.ToString(3), _options.BatchSize);
        using var response = await _http.PostAsJsonAsync(AgentApiRoutes.Claim, request, PrintJson.Options, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<ClaimResponse>(PrintJson.Options, cancellationToken);
        return body?.Jobs ?? [];
    }

    public async Task<IReadOnlyList<PrinterConfig>> GetPrintersAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(AgentApiRoutes.Printers, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<PrintersResponse>(PrintJson.Options, cancellationToken);
        return body?.Printers ?? [];
    }

    public async Task ReportAsync(long jobId, bool success, DeliveryLevel level, string? error, CancellationToken cancellationToken)
    {
        var request = new ResultRequest(_options.EffectiveName, success, level, error);
        using var response = await _http.PostAsJsonAsync(AgentApiRoutes.ResultFor(jobId), request, PrintJson.Options, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new ServerRejectedException(response.StatusCode, detail);
    }
}

/// <summary>El servidor respondió con un error (clave incorrecta, API sin configurar, conflicto...).</summary>
internal sealed class ServerRejectedException(System.Net.HttpStatusCode status, string detail)
    : Exception($"El servidor respondió {(int)status} {status}: {Shorten(detail)}")
{
    public System.Net.HttpStatusCode Status { get; } = status;

    private static string Shorten(string text) => text.Length > 300 ? text[..300] : text;
}
