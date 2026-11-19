using TPVBar.Application;

namespace TPVBar.Application.Tests;

public class ArchitectureTests
{
    [Fact]
    public void Application_SoloDependeDeDomainEntreLosProyectosTPVBar()
    {
        var referencias = ApplicationAssembly.Instance.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null && n.StartsWith("TPVBar", StringComparison.Ordinal))
            .ToList();

        // Domain y el contrato público del agente (sin dependencias) son lo único permitido.
        Assert.All(referencias, n => Assert.True(n is "TPVBar.Domain" or "TPVBar.PrintContracts", $"Dependencia no permitida: {n}"));
    }

    [Fact]
    public void Application_NoDependeDeInfraestructuraNiWeb()
    {
        var prohibidos = ApplicationAssembly.Instance.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null &&
                (n.StartsWith("TPVBar.Infrastructure", StringComparison.Ordinal) ||
                 n.StartsWith("TPVBar.Web", StringComparison.Ordinal) ||
                 n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
                 n.StartsWith("Npgsql", StringComparison.Ordinal)));

        Assert.Empty(prohibidos);
    }
}
