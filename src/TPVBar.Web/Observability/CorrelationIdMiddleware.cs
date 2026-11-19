using System.Text.RegularExpressions;

namespace TPVBar.Web.Observability;

/// <summary>
/// Asigna un identificador de correlación a cada petición (cabecera X-Correlation-Id), lo devuelve
/// en la respuesta y lo añade al ámbito de log. Solo se acepta el recibido si tiene formato seguro.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var incoming = context.Request.Headers[HeaderName].ToString();
        var id = SafeId().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");

        context.TraceIdentifier = id;
        context.Response.Headers[HeaderName] = id;

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next(context);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex SafeId();
}
