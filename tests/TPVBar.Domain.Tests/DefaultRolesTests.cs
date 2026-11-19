using TPVBar.Domain.Security;

namespace TPVBar.Domain.Tests;

public class DefaultRolesTests
{
    [Fact]
    public void TodosLosRolesInicialesTienenDefinicionDePermisos()
    {
        foreach (var name in DefaultRoles.Names)
        {
            Assert.True(DefaultRoles.Permissions.ContainsKey(name), $"Falta la definición de permisos de {name}.");
        }
    }

    [Fact]
    public void Administrador_TieneTodosLosPermisos() =>
        Assert.Equal(
            Enum.GetValues<Permission>().Order(),
            DefaultRoles.Permissions[DefaultRoles.Administrator].Order());

    [Fact]
    public void Camarero_NoPuedeOperacionesSensibles()
    {
        var permisos = DefaultRoles.Permissions[DefaultRoles.Waiter];

        Assert.DoesNotContain(Permission.ApplyDiscounts, permisos);
        Assert.DoesNotContain(Permission.VoidLines, permisos);
        Assert.DoesNotContain(Permission.ModifyPrices, permisos);
        Assert.DoesNotContain(Permission.ManageUsers, permisos);
    }

    [Fact]
    public void SoloElAdministradorGestionaUsuarios()
    {
        var conPermiso = DefaultRoles.Names
            .Where(n => DefaultRoles.Permissions[n].Contains(Permission.ManageUsers))
            .ToList();

        Assert.Equal([DefaultRoles.Administrator], conPermiso);
    }

    [Fact]
    public void LosNombresDeRolSonUnicos() =>
        Assert.Equal(DefaultRoles.Names.Count, DefaultRoles.Names.Distinct(StringComparer.Ordinal).Count());
}
