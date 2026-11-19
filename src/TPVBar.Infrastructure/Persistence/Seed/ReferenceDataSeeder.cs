using Microsoft.EntityFrameworkCore;
using TPVBar.Domain.Cash;
using TPVBar.Domain.Entities;
using TPVBar.Domain.Security;

namespace TPVBar.Infrastructure.Persistence.Seed;

/// <summary>
/// Datos de referencia imprescindibles en cualquier entorno (roles iniciales). Es idempotente y
/// los permisos solo se asignan al CREAR un rol: nunca pisa la configuración posterior.
/// </summary>
internal static class ReferenceDataSeeder
{
    public static async Task SeedAsync(TpvBarDbContext db, CancellationToken cancellationToken)
    {
        var existing = await db.Roles.Select(r => r.Name).ToListAsync(cancellationToken);

        var created = false;
        foreach (var name in DefaultRoles.Names.Except(existing, StringComparer.Ordinal))
        {
            db.Roles.Add(Role.Create(name, DefaultRoles.Permissions[name]));
            created = true;
        }

        // Métodos de pago iniciales, por código: solo se crean los que faltan y nunca se pisa un nombre o una
        // configuración editada después. «Tarjeta» es un registro manual: no hay integración con datáfonos.
        var methodCodes = await db.PaymentMethods.Select(m => m.Code).ToListAsync(cancellationToken);
        (string Code, string Name, bool Cash, int Order)[] defaults = [("CASH", "Efectivo", true, 1), ("CARD", "Tarjeta", false, 2), ("OTHER", "Otros", false, 3)];
        foreach (var (code, name, cash, order) in defaults.Where(d => !methodCodes.Contains(d.Code)))
        {
            db.PaymentMethods.Add(PaymentMethod.Create(code, name, requiresReference: false, affectsCashDrawer: cash, order));
            created = true;
        }

        if (created)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
