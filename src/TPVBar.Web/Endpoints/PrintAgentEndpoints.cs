using System.Security.Cryptography;
using System.Text;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Printing;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.PrintContracts;
using TPVBar.Web.Realtime;

namespace TPVBar.Web.Endpoints;

/// <summary>
/// API que consume el agente de impresión. No usa cookies: se autentica con una clave en la cabecera
/// <c>X-Agent-Key</c> (secreto de configuración, nunca en el repositorio). Sin clave configurada, la API
/// queda desactivada (503): no existe ninguna clave por defecto. Debe quedar restringida a la LAN por el cortafuegos.
/// </summary>
public static class PrintAgentEndpoints
{
    public const string ApiKeySetting = "PrintAgent:ApiKey";
    private const int MaxClaimAttempts = 6;

    public static IEndpointRouteBuilder MapPrintAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/print")
            .AllowAnonymous()
            .DisableAntiforgery();

        group.MapPost("/claim", ClaimAsync);
        group.MapPost("/jobs/{id:long}/result", ReportAsync);
        group.MapGet("/printers", PrintersAsync);

        return app;
    }

    private static async Task<IResult> ClaimAsync(ClaimRequest request, IServiceScopeFactory scopes, OperationEventPublisher publisher, CancellationToken cancellationToken)
    {
        // Cada intento usa un ámbito nuevo: si dos agentes reclaman a la vez, el perdedor repite con datos frescos.
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                var jobs = await scope.ServiceProvider.GetRequiredService<PrintQueueService>()
                    .ClaimAsync(request.Agent, request.Version, request.Max, cancellationToken);
                return Results.Ok(new ClaimResponse(jobs));
            }
            catch (ConcurrencyConflictException) when (attempt < MaxClaimAttempts)
            {
                await Task.Delay(20 * attempt, cancellationToken);
            }
            catch (DomainException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }
    }

    private static async Task<IResult> ReportAsync(long id, ResultRequest request, IServiceScopeFactory scopes, OperationEventPublisher publisher, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<PrintQueueService>()
                .ReportResultAsync(id, request.Agent, request.Success, request.Level, request.Error, cancellationToken);

            await publisher.PublishAsync(scope.ServiceProvider.GetRequiredService<PendingOperationEvents>().Drain());
            return Results.Ok(new { outcome = outcome.ToString() });
        }
        catch (NotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is DomainException or ConcurrencyConflictException)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> PrintersAsync(IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var printers = await scope.ServiceProvider.GetRequiredService<PrintQueueService>().GetPrintersAsync(cancellationToken);
        return Results.Ok(new PrintersResponse(printers));
    }
}

/// <summary>
/// Autentica la API del agente ANTES de leer cualquier cuerpo: una petición sin clave válida recibe siempre
/// 401, nunca un error de formato que revele que la ruta existe.
/// </summary>
public static class PrintAgentKeyMiddleware
{
    public static IApplicationBuilder UsePrintAgentKey(this IApplicationBuilder app) =>
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/api/print", StringComparison.OrdinalIgnoreCase),
            branch => branch.Use(async (context, next) =>
            {
                var expected = context.RequestServices.GetRequiredService<IConfiguration>()[PrintAgentEndpoints.ApiKeySetting];
                if (string.IsNullOrWhiteSpace(expected))
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { error = "La API del agente de impresión no está configurada." });
                    return;
                }

                var provided = context.Request.Headers[AgentApiRoutes.KeyHeader].ToString();
                if (!KeysMatch(expected, provided))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await next(context);
            }));

    /// <summary>Comparación en tiempo constante (sobre hashes, para que la longitud tampoco se filtre).</summary>
    private static bool KeysMatch(string expected, string provided) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)));
}
