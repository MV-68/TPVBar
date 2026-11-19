using System.Reflection;

namespace TPVBar.Domain;

/// <summary>
/// Punto de referencia del ensamblado de dominio (registro de servicios, pruebas de arquitectura).
/// </summary>
public static class DomainAssembly
{
    public static Assembly Instance => typeof(DomainAssembly).Assembly;
}
