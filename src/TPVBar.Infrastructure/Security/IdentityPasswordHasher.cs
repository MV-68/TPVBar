using Microsoft.AspNetCore.Identity;
using TPVBar.Application.Abstractions;
using TPVBar.Domain.Entities;

namespace TPVBar.Infrastructure.Security;

/// <summary>
/// Hash de contraseñas con el <see cref="PasswordHasher{TUser}"/> de ASP.NET Core Identity
/// (PBKDF2-HMAC-SHA512, sal aleatoria por contraseña, formato versionado y con detección de rehash).
/// </summary>
internal sealed class IdentityPasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<AppUser> _hasher = new();
    private readonly AppUser _noUser = AppUser.Create("dummy", "dummy", "dummy", DateTimeOffset.UnixEpoch);
    private readonly string _dummyHash;

    public IdentityPasswordHasher()
    {
        _dummyHash = _hasher.HashPassword(_noUser, Guid.NewGuid().ToString("N"));
    }

    public string Hash(string password) => _hasher.HashPassword(_noUser, password);

    public PasswordVerification Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(_noUser, hash, password) switch
        {
            PasswordVerificationResult.Success => PasswordVerification.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
            _ => PasswordVerification.Failed,
        };

    public void VerifyAgainstDummy(string password) => _ = _hasher.VerifyHashedPassword(_noUser, _dummyHash, password);
}
