using TPVBar.Domain;

namespace TPVBar.Domain.Tests;

public class ArchitectureTests
{
    [Fact]
    public void Domain_NoDependeDeOtrosProyectosTPVBar()
    {
        var referencias = DomainAssembly.Instance.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null && n.StartsWith("TPVBar", StringComparison.Ordinal));

        Assert.Empty(referencias);
    }

    [Fact]
    public void Domain_NoDependeDeEntityFrameworkNiAspNet()
    {
        var prohibidos = DomainAssembly.Instance.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null &&
                (n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
                 n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
                 n.StartsWith("Npgsql", StringComparison.Ordinal)));

        Assert.Empty(prohibidos);
    }
}
