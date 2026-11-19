using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using TPVBar.Application.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Security;
using TPVBar.Web.Realtime;
using TPVBar.Web.Security;

namespace TPVBar.Web.Services;

public sealed record UiResult<T>(bool Ok, T? Value, string? Error);

/// <summary>
/// Ejecuta un caso de uso en un ámbito de servicios NUEVO con la identidad de la sesión actual.
/// Un circuito de Blazor Server vive mucho tiempo: reutilizar su DbContext acumularía datos obsoletos.
/// Con un ámbito por acción cada operación parte de datos frescos, y los conflictos de concurrencia
/// (dos terminales sobre la misma comanda) se reintentan automáticamente con una operación nueva.
/// </summary>
public sealed partial class UseCaseRunner(
    IServiceScopeFactory scopes,
    AuthenticationStateProvider authentication,
    OperationEventPublisher publisher,
    ILogger<UseCaseRunner> logger)
{
    private const int MaxAttempts = 4;

    public async Task<UiResult<T>> RunAsync<TService, T>(Func<TService, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(operation);

        var state = await authentication.GetAuthenticationStateAsync();
        if (!PrincipalReader.TryRead(state.User, out var userId, out var permissions))
        {
            return new UiResult<T>(false, default, "La sesión ha caducado. Vuelva a iniciar sesión.");
        }

        for (var attempt = 1; ; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CurrentUserAccessor>().Set(userId, permissions, null);

            try
            {
                var value = await operation(scope.ServiceProvider.GetRequiredService<TService>(), cancellationToken);

                // Los avisos de tiempo real se publican solo ahora, con la transacción ya confirmada.
                var events = scope.ServiceProvider.GetRequiredService<PendingOperationEvents>().Drain();
                if (events.Count > 0)
                {
                    await publisher.PublishAsync(events);
                }

                return new UiResult<T>(true, value, null);
            }
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
            {
                await Task.Delay(40 * attempt, cancellationToken);
            }
            catch (ConcurrencyConflictException)
            {
                return new UiResult<T>(false, default, "Otro puesto está modificando lo mismo. Vuelva a intentarlo.");
            }
            catch (DomainException ex)
            {
                return new UiResult<T>(false, default, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogUnexpected(logger, ex);
                return new UiResult<T>(false, default, "Se ha producido un error inesperado. No se ha guardado nada; vuelva a intentarlo.");
            }
        }
    }

    public async Task<UiResult<bool>> RunAsync<TService>(Func<TService, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(operation);
        return await RunAsync<TService, bool>(
            async (service, ct) =>
            {
                await operation(service, ct);
                return true;
            },
            cancellationToken);
    }

    [LoggerMessage(EventId = 300, Level = LogLevel.Error, Message = "Error inesperado ejecutando un caso de uso.")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);
}
