using System.Reflection;

namespace TPVBar.Infrastructure;

/// <summary>
/// Punto de referencia del ensamblado de infraestructura (registro de servicios, pruebas de arquitectura).
/// </summary>
public static class InfrastructureAssembly
{
    public static Assembly Instance => typeof(InfrastructureAssembly).Assembly;
}
