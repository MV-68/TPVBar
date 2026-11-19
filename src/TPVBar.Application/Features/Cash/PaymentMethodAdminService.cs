using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Cash;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Features.Cash;

/// <summary>Administración de los métodos de pago configurables.</summary>
public sealed class PaymentMethodAdminService(
    IAppData data,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    AuditWriter audit)
{
    public async Task<IReadOnlyList<PaymentMethodView>> ListAsync(CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var methods = await data.ToListAsync(data.NoTracking(data.PaymentMethods).OrderBy(m => m.DisplayOrder).ThenBy(m => m.Name), cancellationToken);
        return [.. methods.Select(CashService.ToView)];
    }

    public async Task<int> CreateAsync(string code, string name, bool requiresReference, bool affectsCashDrawer, int displayOrder, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var method = PaymentMethod.Create(code, name, requiresReference, affectsCashDrawer, displayOrder);
        data.Add(method);
        await SaveAsync(cancellationToken);

        audit.Write(AuditActions.PaymentMethodChanged, "payment_method", method.Id, new { action = "created", method.Code, method.Name, affectsCashDrawer });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return method.Id;
    }

    /// <summary>El código no cambia nunca; el efectivo del cajón tampoco se puede cambiar si el método ya tiene cobros.</summary>
    public async Task UpdateAsync(int id, string name, bool requiresReference, bool affectsCashDrawer, int displayOrder, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var method = await FindAsync(id, cancellationToken);

        if (method.AffectsCashDrawer != affectsCashDrawer && await data.AnyAsync(data.NoTracking(data.Payments).Where(p => p.PaymentMethodId == id), cancellationToken))
        {
            throw new DomainException("No se puede cambiar si el método usa el cajón: ya tiene cobros registrados y alteraría el efectivo esperado.");
        }

        method.Update(name, requiresReference, affectsCashDrawer, displayOrder);
        audit.Write(AuditActions.PaymentMethodChanged, "payment_method", id, new { action = "updated", method.Name, requiresReference, affectsCashDrawer });
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task SetActiveAsync(int id, bool active, CancellationToken cancellationToken)
    {
        user.Require(Permission.ManageCatalog);
        var method = await FindAsync(id, cancellationToken);

        if (!active && !await data.AnyAsync(data.PaymentMethods.Where(m => m.IsActive && m.Id != id), cancellationToken))
        {
            throw new DomainException("Debe quedar al menos un método de pago activo.");
        }

        method.SetActive(active);
        audit.Write(AuditActions.PaymentMethodChanged, "payment_method", id, new { action = active ? "activated" : "deactivated" });
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<PaymentMethod> FindAsync(int id, CancellationToken cancellationToken) =>
        await data.FirstOrDefaultAsync(data.PaymentMethods.Where(m => m.Id == id), cancellationToken)
            ?? throw new NotFoundException("El método de pago no existe.");

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateEntryException ex)
        {
            throw new DomainException("Ya existe un método de pago con ese código.", ex);
        }
    }
}
