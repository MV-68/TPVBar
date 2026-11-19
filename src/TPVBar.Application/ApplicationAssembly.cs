using System.Reflection;

namespace TPVBar.Application;

/// <summary>
/// Punto de referencia del ensamblado de aplicación (registro de servicios, pruebas de arquitectura).
/// </summary>
public static class ApplicationAssembly
{
    public static Assembly Instance => typeof(ApplicationAssembly).Assembly;
}
