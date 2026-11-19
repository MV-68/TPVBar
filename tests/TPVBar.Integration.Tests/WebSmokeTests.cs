using System.Net;

namespace TPVBar.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class WebSmokeTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Health_Responde200SinSesion()
    {
        using var client = fixture.Web.CreateClient();

        var respuesta = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public void Infraestructura_CargaElProveedorPostgreSql()
    {
        // Comprueba que el conjunto EF Core 10 + Npgsql 10 se resuelve y carga sin conflictos de versión.
        var tipo = typeof(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder);
        Assert.NotNull(tipo.Assembly);
    }
}
