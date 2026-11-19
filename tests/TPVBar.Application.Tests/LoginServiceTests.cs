using Microsoft.Extensions.Logging.Abstractions;
using TPVBar.Application.Abstractions;
using TPVBar.Application.Features.Authentication;
using TPVBar.Application.Tests.Fakes;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Entities;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Tests;

public class LoginServiceTests
{
    private const string Password = "Clave-Correcta-123";
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly LockoutPolicy Policy = new(3, TimeSpan.FromMinutes(10));

    private readonly FakeStore _store = new();
    private readonly FakePasswordHasher _hasher = new();
    private readonly FakeTimeProvider _time = new(Start);

    private LoginService Service() =>
        new(_store, _hasher, _store, _store, _time, Policy, NullLogger<LoginService>.Instance);

    private AppUser SeedUser(string hash = "H:" + Password, bool active = true)
    {
        var user = AppUser.Create("ana", "Ana López", hash, Start);
        if (!active)
        {
            user.Deactivate();
        }

        _store.Seed(user);
        return user;
    }

    private static LoginRequest Request(string password, string userName = "ana") => new(userName, password, "pc-barra");

    [Fact]
    public async Task Login_CredencialesCorrectas_Entra()
    {
        var user = SeedUser();
        _store.Access = new UserAccess(["Caja"], new HashSet<Permission> { Permission.OpenCloseCash });

        var result = await Service().LoginAsync(Request(Password), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(user.Id, result.UserId);
        Assert.Equal("Ana López", result.DisplayName);
        Assert.Contains(Permission.OpenCloseCash, result.Permissions);
        Assert.Equal(Start, user.LastLoginAt);
        Assert.Contains(_store.Audit, e => e.Action == AuditActions.LoginSucceeded && e.UserId == user.Id);
    }

    [Fact]
    public async Task Login_NombreDeUsuarioNoDistingueMayusculas()
    {
        SeedUser();

        var result = await Service().LoginAsync(Request(Password, "  ANA "), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Login_ContrasenaIncorrecta_FallaYCuentaElIntento()
    {
        var user = SeedUser();

        var result = await Service().LoginAsync(Request("otra"), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(LoginFailure.InvalidCredentials, result.Failure);
        Assert.Equal(1, user.FailedAccessCount);
        Assert.Contains(_store.Audit, e => e.Action == AuditActions.LoginFailed);
    }

    [Fact]
    public async Task Login_AlSuperarElLimiteBloqueaLaCuenta()
    {
        var user = SeedUser();
        var service = Service();
        LoginResult last = null!;

        for (var i = 0; i < Policy.MaxFailedAttempts; i++)
        {
            last = await service.LoginAsync(Request("mala"), TestContext.Current.CancellationToken);
        }

        Assert.Equal(LoginFailure.LockedOut, last.Failure);
        Assert.True(user.IsLockedOut(_time.GetUtcNow()));
        Assert.Contains(_store.Audit, e => e.Action == AuditActions.AccountLocked);
    }

    [Fact]
    public async Task Login_CuentaBloqueada_RechazaIncluidoConLaContrasenaCorrecta()
    {
        SeedUser();
        var service = Service();
        for (var i = 0; i < Policy.MaxFailedAttempts; i++)
        {
            await service.LoginAsync(Request("mala"), TestContext.Current.CancellationToken);
        }

        var verificacionesAntes = _hasher.VerifyCalls;
        var result = await service.LoginAsync(Request(Password), TestContext.Current.CancellationToken);

        Assert.Equal(LoginFailure.LockedOut, result.Failure);
        Assert.Equal(verificacionesAntes, _hasher.VerifyCalls); // no se prueba la contraseña durante el bloqueo
        Assert.Contains(_store.Audit, e => e.Action == AuditActions.LoginBlocked);
    }

    [Fact]
    public async Task Login_TrasVencerElBloqueo_VuelveAEntrar()
    {
        SeedUser();
        var service = Service();
        for (var i = 0; i < Policy.MaxFailedAttempts; i++)
        {
            await service.LoginAsync(Request("mala"), TestContext.Current.CancellationToken);
        }

        _time.Advance(Policy.Duration + TimeSpan.FromSeconds(1));
        var result = await service.LoginAsync(Request(Password), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Login_UsuarioInexistente_RespondeIgualQueContrasenaIncorrecta()
    {
        SeedUser();

        var inexistente = await Service().LoginAsync(Request("x", "nadie"), TestContext.Current.CancellationToken);
        var incorrecta = await Service().LoginAsync(Request("x", "ana"), TestContext.Current.CancellationToken);

        Assert.Equal(incorrecta.Failure, inexistente.Failure);
        Assert.Equal(1, _hasher.DummyCalls); // coste equivalente: no revela qué usuarios existen
        Assert.Contains(_store.Audit, e => e.Action == AuditActions.LoginFailed && e.EntityId == "nadie");
    }

    [Fact]
    public async Task Login_CuentaInactiva_NoEntraConContrasenaCorrecta()
    {
        SeedUser(active: false);

        var result = await Service().LoginAsync(Request(Password), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(LoginFailure.InvalidCredentials, result.Failure);
    }

    [Theory]
    [InlineData("", "algo")]
    [InlineData("ana", "")]
    [InlineData("   ", "   ")]
    public async Task Login_DatosVacios_Falla(string userName, string password)
    {
        SeedUser();

        var result = await Service().LoginAsync(new LoginRequest(userName, password, null), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Login_ConflictoDeConcurrencia_ReleeYReintentaHastaGuardar()
    {
        SeedUser();
        _store.ConcurrencyFailuresToSimulate = 2;

        var result = await Service().LoginAsync(Request("mala"), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(2, _store.Reloads);
        Assert.True(_store.SaveCalls >= 1);
        // Que el contador no pierda incrementos se comprueba contra PostgreSQL real (pruebas de integración),
        // porque aquí el doble de ReloadAsync no restaura el estado de la entidad como hace EF Core.
    }

    [Fact]
    public async Task Login_ConflictoPersistente_NoConcedeAcceso()
    {
        SeedUser();
        _store.ConcurrencyFailuresToSimulate = int.MaxValue;

        await Assert.ThrowsAsync<TPVBar.Application.Common.ConcurrencyConflictException>(() =>
            Service().LoginAsync(Request("mala"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Login_HashAntiguo_SeRegeneraTrasEntrarCorrectamente()
    {
        var user = SeedUser(hash: "OLD:" + Password);

        var result = await Service().LoginAsync(Request(Password), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("H:" + Password, user.PasswordHash);
    }

    [Fact]
    public async Task Auditoria_NuncaContieneLaContrasena()
    {
        SeedUser();
        await Service().LoginAsync(Request("Secreto-Que-No-Debe-Aparecer"), TestContext.Current.CancellationToken);
        await Service().LoginAsync(Request(Password), TestContext.Current.CancellationToken);

        Assert.NotEmpty(_store.Audit);
        Assert.All(_store.Audit, e =>
        {
            Assert.DoesNotContain("Secreto-Que-No-Debe-Aparecer", e.DetailsJson, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, e.DetailsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("H:", e.DetailsJson, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Logout_QuedaAuditado()
    {
        var user = SeedUser();

        await Service().LogoutAsync(user.Id, "pc-barra", TestContext.Current.CancellationToken);

        Assert.Contains(_store.Audit, e => e.Action == AuditActions.Logout && e.UserId == user.Id && e.Workstation == "pc-barra");
    }
}
