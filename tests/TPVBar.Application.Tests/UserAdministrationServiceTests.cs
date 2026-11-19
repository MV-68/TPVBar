using TPVBar.Application.Features.Authentication;
using TPVBar.Application.Tests.Fakes;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Exceptions;

namespace TPVBar.Application.Tests;

public class UserAdministrationServiceTests
{
    private readonly FakeStore _store = new();
    private readonly FakePasswordHasher _hasher = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));

    private UserAdministrationService Service() => new(_store, _hasher, _store, _store, _time);

    [Fact]
    public async Task CreateUser_GuardaElHashYNoLaContrasena()
    {
        var id = await Service().CreateUserAsync("ana", "Ana", "Cafe-con-leche-77", ["Camarero"], null, TestContext.Current.CancellationToken);

        var user = await _store.FindByUserNameAsync("ana", TestContext.Current.CancellationToken);
        Assert.NotNull(user);
        Assert.Equal(id, user.Id);
        Assert.Equal("H:Cafe-con-leche-77", user.PasswordHash);
        Assert.Contains(_store.Audit, e => e.Action == AuditActions.UserCreated && !e.DetailsJson.Contains("Cafe-con-leche", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateUser_ContrasenaDebil_Falla() =>
        await Assert.ThrowsAsync<DomainException>(() =>
            Service().CreateUserAsync("ana", "Ana", "corta", ["Camarero"], null, TestContext.Current.CancellationToken));

    [Fact]
    public async Task CreateUser_SinRoles_Falla() =>
        await Assert.ThrowsAsync<DomainException>(() =>
            Service().CreateUserAsync("ana", "Ana", "Cafe-con-leche-77", [], null, TestContext.Current.CancellationToken));

    [Fact]
    public async Task CreateUser_NombreRepetido_FallaSinImportarMayusculas()
    {
        await Service().CreateUserAsync("ana", "Ana", "Cafe-con-leche-77", ["Camarero"], null, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DomainException>(() =>
            Service().CreateUserAsync("ANA", "Otra", "Otra-clave-segura-1", ["Camarero"], null, TestContext.Current.CancellationToken));
    }
}
