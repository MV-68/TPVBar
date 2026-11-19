using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TPVBar.Application.Features.Authentication;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Security;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class PersistenceTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migraciones_CreanElEsquemaEsperado()
    {
        await using var services = fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();

        var tablas = await db.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'tpv'")
            .ToListAsync(Ct);

        foreach (var esperada in new[] { "users", "roles", "user_roles", "role_permissions", "audit_events", "__ef_migrations_history" })
        {
            Assert.Contains(esperada, tablas);
        }

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task DatosDeReferencia_CreanLosRolesInicialesConSusPermisos()
    {
        await using var services = fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();

        var roles = await db.Roles.Include(r => r.Permissions).ToListAsync(Ct);

        Assert.Equal(DefaultRoles.Names.Order(), roles.Select(r => r.Name).Order());
        var admin = roles.Single(r => r.Name == DefaultRoles.Administrator);
        Assert.Equal(Enum.GetValues<Permission>().Length, admin.Permissions.Count);
    }

    [Fact]
    public async Task DatosDeReferencia_SonIdempotentesYNoPisanLaConfiguracionPosterior()
    {
        await using var services = fixture.BuildServices();
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
            // El encargado de verdad quita un permiso al rol Caja.
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM tpv.role_permissions WHERE permission = 'PerformRefunds' AND role_id = (SELECT id FROM tpv.roles WHERE name = 'Caja')", Ct);
        }

        await TPVBar.Infrastructure.Persistence.DatabaseInitializer.SeedReferenceDataAsync(services, Ct);
        await TPVBar.Infrastructure.Persistence.DatabaseInitializer.SeedReferenceDataAsync(services, Ct);

        await using var check = services.CreateAsyncScope();
        var db2 = check.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var caja = await db2.Roles.Include(r => r.Permissions).SingleAsync(r => r.Name == "Caja", Ct);
        Assert.DoesNotContain(caja.Permissions, p => p.Permission == Permission.PerformRefunds);
        Assert.Equal(DefaultRoles.Names.Count, await db2.Roles.CountAsync(Ct));
    }

    [Fact]
    public async Task CrearUsuario_GuardaSoloElHashYSobreviveAlReinicio()
    {
        await using var services = fixture.BuildServices();
        var (userName, password) = await fixture.CreateUserAsync(DefaultRoles.Cashier, services);

        // "Reinicio del servidor": contenedor de servicios nuevo, mismas bases de datos.
        await using var reiniciado = fixture.BuildServices();
        await using var scope = reiniciado.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.NormalizedUserName == TPVBar.Domain.Entities.AppUser.NormalizeUserName(userName), Ct);

        Assert.NotEqual(password, user.PasswordHash);
        Assert.DoesNotContain(password, user.PasswordHash, StringComparison.Ordinal);
        Assert.StartsWith("AQAAAA", user.PasswordHash, StringComparison.Ordinal); // formato versionado de Identity (PBKDF2)

        var login = scope.ServiceProvider.GetRequiredService<LoginService>();
        var result = await login.LoginAsync(new LoginRequest(userName, password, "test"), Ct);
        Assert.True(result.Succeeded);
        Assert.Contains(Permission.OpenCloseCash, result.Permissions);
        Assert.DoesNotContain(Permission.ManageUsers, result.Permissions);
    }

    [Fact]
    public async Task CrearUsuario_NombreRepetidoEnOtraCapitalizacion_SeRechaza()
    {
        await using var services = fixture.BuildServices();
        var (userName, _) = await fixture.CreateUserAsync(DefaultRoles.Waiter, services);

        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserAdministrationService>();

        await Assert.ThrowsAsync<DomainException>(() =>
            users.CreateUserAsync(userName.ToUpperInvariant(), "Otro", "Pw-" + Guid.NewGuid().ToString("N"), [DefaultRoles.Waiter], null, Ct));
    }

    [Fact]
    public async Task CrearUsuario_DosAltasSimultaneasConElMismoNombre_SoloUnaTieneExito()
    {
        await using var services = fixture.BuildServices();
        var userName = "c" + Guid.NewGuid().ToString("N")[..12];

        async Task<bool> TryCreate()
        {
            await using var scope = services.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<UserAdministrationService>()
                    .CreateUserAsync(userName, "Carrera", "Pw-" + Guid.NewGuid().ToString("N"), [DefaultRoles.Waiter], null, Ct);
                return true;
            }
            catch (DomainException)
            {
                return false;
            }
        }

        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(TryCreate, Ct)));

        Assert.Equal(1, resultados.Count(r => r));
        await using var check = services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        Assert.Equal(1, await db.Users.CountAsync(u => u.NormalizedUserName == TPVBar.Domain.Entities.AppUser.NormalizeUserName(userName), Ct));
    }

    [Fact]
    public async Task CincoIntentosFallidosSimultaneos_BloqueanLaCuentaSinPerderIncrementos()
    {
        await using var services = fixture.BuildServices();
        var (userName, password) = await fixture.CreateUserAsync(DefaultRoles.Waiter, services);

        async Task Fail()
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<LoginService>()
                .LoginAsync(new LoginRequest(userName, "mala-" + Guid.NewGuid(), "test"), Ct);
        }

        await Task.WhenAll(Enumerable.Range(0, LockoutPolicy.Default.MaxFailedAttempts).Select(_ => Task.Run(Fail, Ct)));

        await using var check = services.CreateAsyncScope();
        var login = check.ServiceProvider.GetRequiredService<LoginService>();
        var result = await login.LoginAsync(new LoginRequest(userName, password, "test"), Ct);

        // Aunque la contraseña es correcta, la cuenta debe estar bloqueada: ningún intento se perdió.
        Assert.Equal(LoginFailure.LockedOut, result.Failure);
    }

    [Fact]
    public async Task ConcurrenciaOptimista_DosEscritoresSobreElMismoUsuario_ElSegundoFalla()
    {
        await using var services = fixture.BuildServices();
        var (userName, _) = await fixture.CreateUserAsync(DefaultRoles.Waiter, services);

        await using var a = services.CreateAsyncScope();
        await using var b = services.CreateAsyncScope();
        var dbA = a.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var dbB = b.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var normalized = userName.ToUpperInvariant();

        var userA = await dbA.Users.SingleAsync(u => u.NormalizedUserName == normalized, Ct);
        var userB = await dbB.Users.SingleAsync(u => u.NormalizedUserName == normalized, Ct);

        userA.Deactivate();
        await dbA.SaveChangesAsync(Ct);

        userB.Deactivate();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Auditoria_LaBaseDeDatosRechazaModificarBorrarOVaciar()
    {
        await using var services = fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        db.AuditEvents.Add(AuditEvent.Create("test.inmutable", "test", "1", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE tpv.audit_events SET action = 'x'", Ct));
        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM tpv.audit_events", Ct));
        var truncate = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("TRUNCATE tpv.audit_events", Ct));

        Assert.All([update, delete, truncate], e => Assert.Equal(PostgresErrorCodes.RestrictViolation, e.SqlState));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "test.inmutable", Ct));
    }

    [Fact]
    public async Task Auditoria_LoginFallidoYCorrectoQuedanRegistradosSinSecretos()
    {
        await using var services = fixture.BuildServices();
        var (userName, password) = await fixture.CreateUserAsync(DefaultRoles.Waiter, services);

        await using var scope = services.CreateAsyncScope();
        var login = scope.ServiceProvider.GetRequiredService<LoginService>();
        await login.LoginAsync(new LoginRequest(userName, "contrasena-equivocada-1", "pc-1"), Ct);
        await login.LoginAsync(new LoginRequest(userName, password, "pc-1"), Ct);

        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var userId = (await db.Users.AsNoTracking().SingleAsync(u => u.NormalizedUserName == TPVBar.Domain.Entities.AppUser.NormalizeUserName(userName), Ct)).Id;
        var eventos = await db.AuditEvents.AsNoTracking().Where(e => e.UserId == userId).ToListAsync(Ct);

        Assert.Contains(eventos, e => e.Action == AuditActions.LoginFailed);
        Assert.Contains(eventos, e => e.Action == AuditActions.LoginSucceeded && e.Workstation == "pc-1");
        Assert.All(eventos, e =>
        {
            Assert.DoesNotContain("contrasena-equivocada-1", e.DetailsJson, StringComparison.Ordinal);
            Assert.DoesNotContain(password, e.DetailsJson, StringComparison.Ordinal);
        });
    }
}
