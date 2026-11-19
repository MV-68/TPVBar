using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TPVBar.Domain.Security;
using TPVBar.Web.Security;

namespace TPVBar.Web.Realtime;

/// <summary>
/// Hub de tiempo real. Solo el servidor emite; los clientes escuchan avisos de "algo ha cambiado" con
/// identificadores (nunca datos) y vuelven a leer el estado. Exige sesión; cada conexión entra solo en los
/// grupos que le corresponden por sus permisos.
/// </summary>
[Authorize]
public sealed class OperationsHub(ConnectionTracker tracker) : Hub
{
    public const string Path = "/hubs/operations";
    public const string FloorGroup = "floor";
    public const string KitchenGroup = "kitchen";
    public const string PrintingGroup = "printing";

    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        if (user is not null)
        {
            await JoinIf(user, Permission.TakeOrders, FloorGroup);
            await JoinIf(user, Permission.UseKitchen, KitchenGroup);
            await JoinIf(user, Permission.ManagePrinting, PrintingGroup);
        }

        tracker.Opened();
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        tracker.Closed();
        await base.OnDisconnectedAsync(exception);
    }

    private async Task JoinIf(System.Security.Claims.ClaimsPrincipal user, Permission permission, string group)
    {
        if (user.HasClaim(TpvClaimTypes.Permission, permission.ToString()))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
        }
    }
}
