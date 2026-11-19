namespace TPVBar.Application.Abstractions;

public enum PasswordVerification
{
    Failed = 0,
    Success = 1,

    /// <summary>La contraseña es correcta pero el hash usa parámetros antiguos y debe regenerarse.</summary>
    SuccessRehashNeeded = 2,
}

public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerification Verify(string hash, string password);

    /// <summary>
    /// Realiza una verificación de coste equivalente contra un hash ficticio. Se usa cuando el usuario
    /// no existe, para que el tiempo de respuesta no revele qué usuarios existen.
    /// </summary>
    void VerifyAgainstDummy(string password);
}
