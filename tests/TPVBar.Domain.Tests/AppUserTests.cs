using TPVBar.Domain.Entities;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Security;

namespace TPVBar.Domain.Tests;

public class AppUserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly LockoutPolicy Policy = new(3, TimeSpan.FromMinutes(10));

    private static AppUser NewUser() => AppUser.Create("Ana", "Ana López", "hash", Now);

    [Fact]
    public void Create_NormalizaNombreYActivaLaCuenta()
    {
        var user = AppUser.Create("  Ana.Lopez ", " Ana López ", "hash", Now);

        Assert.Equal("Ana.Lopez", user.UserName);
        Assert.Equal("ANA.LOPEZ", user.NormalizedUserName);
        Assert.Equal("Ana López", user.DisplayName);
        Assert.True(user.IsActive);
        Assert.NotEqual(Guid.Empty, user.Id);
    }

    [Theory]
    [InlineData("", "Nombre", "hash")]
    [InlineData("   ", "Nombre", "hash")]
    [InlineData("usuario", "", "hash")]
    [InlineData("usuario", "Nombre", "")]
    public void Create_DatosObligatoriosVacios_Falla(string userName, string displayName, string hash) =>
        Assert.Throws<DomainException>(() => AppUser.Create(userName, displayName, hash, Now));

    [Fact]
    public void Create_NombreDemasiadoLargo_Falla() =>
        Assert.Throws<DomainException>(() => AppUser.Create(new string('a', 51), "Nombre", "hash", Now));

    [Fact]
    public void RegisterFailedAccess_BloqueaAlAlcanzarElLimite()
    {
        var user = NewUser();

        Assert.False(user.RegisterFailedAccess(Now, Policy));
        Assert.False(user.RegisterFailedAccess(Now, Policy));
        Assert.True(user.RegisterFailedAccess(Now, Policy));

        Assert.True(user.IsLockedOut(Now));
        Assert.Equal(Now + Policy.Duration, user.LockoutEnd);
        Assert.Equal(0, user.FailedAccessCount);
    }

    [Fact]
    public void IsLockedOut_DejaDeEstarloAlVencerElBloqueo()
    {
        var user = NewUser();
        for (var i = 0; i < Policy.MaxFailedAttempts; i++)
        {
            user.RegisterFailedAccess(Now, Policy);
        }

        Assert.True(user.IsLockedOut(Now + Policy.Duration - TimeSpan.FromSeconds(1)));
        Assert.False(user.IsLockedOut(Now + Policy.Duration));
    }

    [Fact]
    public void RegisterFailedAccess_TrasBloqueoVencidoReiniciaElContador()
    {
        var user = NewUser();
        for (var i = 0; i < Policy.MaxFailedAttempts; i++)
        {
            user.RegisterFailedAccess(Now, Policy);
        }

        var later = Now + Policy.Duration + TimeSpan.FromMinutes(1);
        var locked = user.RegisterFailedAccess(later, Policy);

        Assert.False(locked);
        Assert.Equal(1, user.FailedAccessCount);
        Assert.Null(user.LockoutEnd);
    }

    [Fact]
    public void RegisterSuccessfulAccess_LimpiaIntentosYBloqueo()
    {
        var user = NewUser();
        user.RegisterFailedAccess(Now, Policy);
        user.RegisterFailedAccess(Now, Policy);

        user.RegisterSuccessfulAccess(Now);

        Assert.Equal(0, user.FailedAccessCount);
        Assert.Null(user.LockoutEnd);
        Assert.Equal(Now, user.LastLoginAt);
    }

    [Fact]
    public void AssignRole_NoDuplicaElMismoRol()
    {
        var user = NewUser();
        var role = Role.Create("Caja", []);

        user.AssignRole(role);
        user.AssignRole(role);

        Assert.Single(user.UserRoles);
    }

    [Fact]
    public void NormalizeUserName_EsInsensibleAMayusculasYEspacios() =>
        Assert.Equal(AppUser.NormalizeUserName("  PEPE "), AppUser.NormalizeUserName("pepe"));
}
