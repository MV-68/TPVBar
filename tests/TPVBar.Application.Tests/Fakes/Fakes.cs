using TPVBar.Application.Abstractions;
using TPVBar.Application.Common;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Entities;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Tests.Fakes;

internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset value) => _now = value;
}

/// <summary>Hash de pega: "H:" + contraseña. "OLD:" + contraseña simula un hash con parámetros antiguos.</summary>
internal sealed class FakePasswordHasher : IPasswordHasher
{
    public int VerifyCalls { get; private set; }

    public int DummyCalls { get; private set; }

    public string Hash(string password) => "H:" + password;

    public PasswordVerification Verify(string hash, string password)
    {
        VerifyCalls++;
        if (hash == "H:" + password)
        {
            return PasswordVerification.Success;
        }

        return hash == "OLD:" + password ? PasswordVerification.SuccessRehashNeeded : PasswordVerification.Failed;
    }

    public void VerifyAgainstDummy(string password) => DummyCalls++;
}

internal sealed class FakeStore : IUserStore, IAuditLog, IUnitOfWork
{
    private readonly Dictionary<string, AppUser> _users = [];

    public List<AuditEvent> Audit { get; } = [];

    public int SaveCalls { get; private set; }

    public int Reloads { get; private set; }

    /// <summary>Número de guardados que fallarán con conflicto de concurrencia antes de funcionar.</summary>
    public int ConcurrencyFailuresToSimulate { get; set; }

    public UserAccess Access { get; set; } = new(["Camarero"], new HashSet<Permission>());

    public void Seed(AppUser user) => _users[user.NormalizedUserName] = user;

    public Task<AppUser?> FindByUserNameAsync(string userName, CancellationToken cancellationToken) =>
        Task.FromResult(_users.GetValueOrDefault(AppUser.NormalizeUserName(userName)));

    public Task<UserAccess> GetAccessAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(Access);

    public Task<bool> UserNameExistsAsync(string userName, CancellationToken cancellationToken) =>
        Task.FromResult(_users.ContainsKey(AppUser.NormalizeUserName(userName)));

    public Task AddAsync(AppUser user, IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken)
    {
        Seed(user);
        return Task.CompletedTask;
    }

    public Task ReloadAsync(AppUser user, CancellationToken cancellationToken)
    {
        Reloads++;
        return Task.CompletedTask;
    }

    public void Add(AuditEvent auditEvent) => Audit.Add(auditEvent);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (ConcurrencyFailuresToSimulate > 0)
        {
            ConcurrencyFailuresToSimulate--;
            throw new ConcurrencyConflictException();
        }

        SaveCalls++;
        return Task.CompletedTask;
    }
}
