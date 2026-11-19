extern alias agent;

using System.Globalization;
using agent::TPVBar.PrintAgent;
using agent::TPVBar.PrintAgent.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TPVBar.Application.Features.Orders;
using TPVBar.Application.Features.Printing;
using TPVBar.Domain.Printing;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Integration.Tests;

/// <summary>
/// Flujo completo con piezas reales: comanda → envío → cola en PostgreSQL → API autenticada → agente de
/// impresión → impresora SIMULADA (archivo). No prueba hardware real.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AgentEndToEndTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => fixture.Web.Services;

    private PrintLoopWorker NewAgent(string name = "agente-test")
    {
        var options = new AgentOptions { ServerUrl = "http://localhost", ApiKey = fixture.AgentKey, Name = name };
        var server = new ServerClient(fixture.Web.CreateClient(), Options.Create(options));
        return new PrintLoopWorker(server, new PrintJobProcessor(), Options.Create(options), NullLogger<PrintLoopWorker>.Instance);
    }

    private static Task<T> Full<TService, T>(IServiceProvider sp, Func<TService, Task<T>> op)
        where TService : notnull => SalesHelpers.RunAsync(sp, SalesHelpers.Full, op);

    private async Task<(long OrderId, string Note, World World)> SendOrderAsync()
    {
        var world = await SalesHelpers.CreateWorldAsync(Services);
        var note = "nota-" + Guid.NewGuid().ToString("N")[..8];
        var orderId = await Full<FloorService, long>(Services, f => f.OpenTableAsync(world.TableId, 2, Ct));
        await Full<OrderService, OrderView>(Services, o => o.AddLineAsync(orderId, world.CanaId, 2, null, Guid.NewGuid(), Ct));
        await Full<OrderService, OrderView>(Services, o => o.AddLineAsync(orderId, world.TortillaId, 1, note, Guid.NewGuid(), Ct));
        await Full<OrderService, SendResult>(Services, o => o.SendToKitchenAsync(orderId, Guid.NewGuid(), Ct));
        return (orderId, note, world);
    }

    private async Task<List<PrintJob>> JobsOfAsync(long orderId)
    {
        await using var scope = Services.CreateAsyncScope();
        var id = orderId.ToString(CultureInfo.InvariantCulture);
        return await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().PrintJobs.AsNoTracking()
            .Where(j => j.ReferenceType == "order" && j.ReferenceId == id).ToListAsync(Ct);
    }

    private async Task RunAgentUntilAsync(PrintLoopWorker worker, long orderId, Func<List<PrintJob>, bool> done)
    {
        for (var i = 0; i < 30; i++)
        {
            await worker.RunOnceAsync(Ct);
            if (done(await JobsOfAsync(orderId)))
            {
                return;
            }
        }

        var state = string.Join("; ", (await JobsOfAsync(orderId)).Select(j => $"#{j.Id} {j.Status} intentos={j.Attempts} error={j.LastError}"));
        throw new InvalidOperationException("El agente no completó los trabajos esperados. Estado: " + state);
    }

    [Fact]
    public async Task EnviarUnaComanda_LlegaAlaImpresoraSimuladaConTodoElContenido()
    {
        var (orderId, note, _) = await SendOrderAsync();
        var worker = NewAgent();

        await RunAgentUntilAsync(worker, orderId, jobs => jobs.Count == 2 && jobs.All(j => j.Status == PrintJobStatus.Sent));

        var files = Directory.GetFiles(Path.Combine(fixture.PrintFolder, "cocina"), "*.txt")
            .Select(File.ReadAllText).Where(t => t.Contains(note, StringComparison.Ordinal)).ToList();
        var kitchen = Assert.Single(files);
        Assert.Contains("COCINA", kitchen, StringComparison.Ordinal);
        Assert.Contains("1x Tortilla", kitchen, StringComparison.Ordinal);
        Assert.Contains($">> {note}", kitchen, StringComparison.Ordinal);
        Assert.Contains("Mesa A", kitchen, StringComparison.Ordinal);
        Assert.Contains("IMPRESORA SIMULADA", kitchen, StringComparison.Ordinal);

        var jobs = await JobsOfAsync(orderId);
        Assert.All(jobs, j =>
        {
            Assert.Equal(DeliveryLevel.Simulated, j.DeliveryLevel); // nunca se afirma "impreso"
            Assert.Equal(1, j.Attempts);
            Assert.Null(j.LastError);
        });

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        Assert.Equal(2, await db.PrintJobAttempts.CountAsync(a => a.Success && jobs.Select(j => j.Id).Contains(a.JobId), Ct));
        Assert.NotNull((await db.Printers.AsNoTracking().SingleAsync(p => p.Name == "Cocina (simulada)", Ct)).LastSuccessAt);
        Assert.Contains(await db.PrintAgentHeartbeats.AsNoTracking().ToListAsync(Ct), b => b.AgentName == "agente-test");
    }

    [Fact]
    public async Task PedirLaCuenta_ImprimePreCuentaNoFiscalEnLaImpresoraDeTickets()
    {
        var (orderId, _, _) = await SendOrderAsync();
        await Full<OrderService, OrderView>(Services, o => o.RequestBillAsync(orderId, Ct));

        await RunAgentUntilAsync(NewAgent(), orderId, jobs => jobs.Count == 3 && jobs.All(j => j.Status == PrintJobStatus.Sent));

        var bill = Directory.GetFiles(Path.Combine(fixture.PrintFolder, "tickets"), "*.txt")
            .Select(File.ReadAllText).Single(t => t.Contains("Mesa A", StringComparison.Ordinal) && t.Contains("CUENTA", StringComparison.Ordinal) && t.Contains("7,10 €", StringComparison.Ordinal));
        Assert.Contains("sin validez fiscal", bill, StringComparison.Ordinal);
        Assert.Contains("no es una factura", bill, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImpresoraCaida_ElTrabajoSeReintentaYSeRecuperaCuandoVuelve()
    {
        var kitchenPrinter = await FindDemoKitchenPrinterAsync();
        var original = new PrinterInput(kitchenPrinter.Name, kitchenPrinter.Type, kitchenPrinter.Connection, kitchenPrinter.Target, kitchenPrinter.PaperWidthMm);
        try
        {
            // La impresora de red deja de responder (puerto cerrado).
            await Full<PrintAdminService, bool>(Services, async a =>
            {
                await a.UpdatePrinterAsync(kitchenPrinter.Id, original with { Connection = PrinterConnection.Network, Target = "127.0.0.1:1" }, Ct);
                return true;
            });
            var (orderId, _, _) = await SendOrderAsync();
            var worker = NewAgent("agente-fallo");

            await RunAgentUntilAsync(worker, orderId, jobs => jobs.Count == 2 && jobs.All(j => j.Attempts >= 1 && j.Status == PrintJobStatus.Pending && j.LastError != null));

            var failing = await JobsOfAsync(orderId);
            Assert.All(failing, j =>
            {
                Assert.Contains("Cocina (simulada)", j.LastError, StringComparison.Ordinal);
                Assert.True(j.NextAttemptAt > DateTimeOffset.UtcNow); // espera antes de reintentar
            });
            await using (var scope = Services.CreateAsyncScope())
            {
                var printer = await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().Printers.AsNoTracking().SingleAsync(p => p.Id == kitchenPrinter.Id, Ct);
                Assert.NotNull(printer.LastError);
            }

            // La impresora vuelve; se adelanta la espera y el agente recupera los trabajos pendientes.
            await Full<PrintAdminService, bool>(Services, async a =>
            {
                await a.UpdatePrinterAsync(kitchenPrinter.Id, original, Ct);
                return true;
            });
            await using (var scope = Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().Database
                    .ExecuteSqlRawAsync("UPDATE tpv.print_jobs SET next_attempt_at = now() - interval '1 second' WHERE status = 'Pending'", Ct);
            }

            // El agente cachea la configuración de impresoras hasta 30 s; uno recién arrancado la lee al instante.
            await RunAgentUntilAsync(NewAgent("agente-recuperado"), orderId, jobs => jobs.All(j => j.Status == PrintJobStatus.Sent));
            Assert.All(await JobsOfAsync(orderId), j => Assert.True(j.Attempts >= 2));
        }
        finally
        {
            await Full<PrintAdminService, bool>(Services, async a =>
            {
                await a.UpdatePrinterAsync(kitchenPrinter.Id, original, Ct);
                return true;
            });
        }
    }

    [Fact]
    public async Task UnReintentoDeUnTrabajoYaIntentado_SeImprimeMarcadoComoPosibleDuplicado()
    {
        var (orderId, note, _) = await SendOrderAsync();
        var jobs = await JobsOfAsync(orderId);
        var kitchenJob = jobs.Single(j => j.DedupeKey.EndsWith(":Kitchen", StringComparison.Ordinal));

        // Un agente reclama el trabajo y se cae sin informar; caduca su arrendamiento.
        await using (var scope = Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().Database
                .ExecuteSqlRawAsync("UPDATE tpv.print_jobs SET status = 'Processing', attempts = 1, locked_by = 'agente-caido', locked_until = now() - interval '1 minute' WHERE id = {0}", [kitchenJob.Id], Ct);
        }

        await RunAgentUntilAsync(NewAgent("agente-nuevo"), orderId, js => js.All(j => j.Status == PrintJobStatus.Sent));

        var text = Directory.GetFiles(Path.Combine(fixture.PrintFolder, "cocina"), "*.txt")
            .Select(File.ReadAllText).Single(t => t.Contains(note, StringComparison.Ordinal));
        Assert.Contains("REIMPRESION / REINTENTO", text, StringComparison.Ordinal);
    }

    private async Task<Printer> FindDemoKitchenPrinterAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().Printers.AsNoTracking()
            .SingleAsync(p => p.Name == "Cocina (simulada)", Ct);
    }
}
