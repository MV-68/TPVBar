namespace TPVBar.Domain.Security;

/// <summary>Política de bloqueo temporal por intentos de acceso fallidos.</summary>
public sealed record LockoutPolicy(int MaxFailedAttempts, TimeSpan Duration)
{
    public static LockoutPolicy Default { get; } = new(5, TimeSpan.FromMinutes(15));
}
