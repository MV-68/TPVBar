using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TPVBar.Application.Common;
using TPVBar.Application.Features.Kitchen;
using TPVBar.Application.Features.Orders;
using TPVBar.Application.Features.Printing;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Printing;
using TPVBar.Domain.Sales;
using TPVBar.Infrastructure.Persistence;
using TPVBar.PrintContracts;
using Delivery = TPVBar.PrintContracts.DeliveryLevel;

namespace TPVBar.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class PrintingPersistenceTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<T> Full<TService, T>(IServiceProvider sp, Func<TService, Task<T>> op)
        where TService : notnull => SalesHelpers.RunAsync(sp, SalesHelpers.Full, op);

    /// <summary>Lo que hace el endpoint del agente: ámbito nuevo en cada intento y reintento si otro agente reclamó a la vez.</summary>
    private static async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(IServiceProvider sp, string agent, int max = 20)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = sp.CreateAsyncScope();
            try
            {
                return await scope.ServiceProvider.GetRequiredService<PrintQueueService>().ClaimAsync(agent, "test", max, Ct);
            }
            catch (ConcurrencyConflictException) when (attempt < 12)
            {
                await Task.Delay(10 * attempt, Ct);
            }
        }
    }

    private static async Task<ResultOutcome> ReportAsync(IServiceProvider sp, long jobId, string agent, bool success, string? error = null)
    {
        await using var scope = sp.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PrintQueueService>()
            .ReportResultAsync(jobId, agent, success, Delivery.SentToDevice, error, Ct);
    }

    private static async Task<int> NewPrinterAsync(IServiceProvider sp) =>
        await Full<PrintAdminService, int>(sp, a => a.CreatePrinterAsync(
            new PrinterInput("Impresora " + Guid.NewGuid().ToString("N")[..8], PrinterType.Kitchen, PrinterConnection.Simulated, Path.GetTempPath(), 80), Ct));

    private static async Task<long> NewTestJobAsync(IServiceProvider sp, int printerId) =>
        await Full<PrintAdminService, long>(sp, a => a.TestPrintAsync(printerId, Ct));

    /// <summary>Reclama hasta que aparece el trabajo buscado (la cola es compartida con otras pruebas).</summary>
    private static async Task<ClaimedJob> ClaimJobAsync(IServiceProvider sp, string agent, long jobId)
    {
        for (var i = 0; i < 10; i++)
        {
            var found = (await ClaimAsync(sp, agent)).FirstOrDefault(j => j.Id == jobId);
            if (found is not null)
            {
                return found;
            }
        }

        throw new InvalidOperationException($"El trabajo {jobId} no se pudo reclamar.");
    }

    private static async Task SqlAsync(IServiceProvider sp, string sql, params object[] parameters)
    {
        await using var scope = sp.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().Database.ExecuteSqlRawAsync(sql, parameters, Ct);
    }

    private static async Task<PrintJob> ReadJobAsync(IServiceProvider sp, long jobId)
    {
        await using var scope = sp.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TpvBarDbContext>().PrintJobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct);
    }

    [Fact]
    public async Task Migraciones_IncluyenCocinaEImpresion()
    {
        await using var services = fixture.BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();

        var tablas = await db.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'tpv'")
            .ToListAsync(Ct);

        foreach (var esperada in new[] { "printers", "printer_routes", "print_jobs", "print_job_attempts", "print_agent_heartbeats" })
        {
            Assert.Contains(esperada, tablas);
        }

        var roles = await db.Roles.Include(r => r.Permissions).ToListAsync(Ct);
        Assert.Contains(roles.Single(r => r.Name == "Cocina").Permissions, p => p.Permission == TPVBar.Domain.Security.Permission.UseKitchen);
        Assert.Contains(roles.Single(r => r.Name == "Encargado").Permissions, p => p.Permission == TPVBar.Domain.Security.Permission.ManagePrinting);
    }

    [Fact]
    public async Task DosAgentesReclamandoAlaVez_NuncaRecibenElMismoTrabajo()
    {
        await using var services = fixture.BuildServices();
        var printer = await NewPrinterAsync(services);
        var mine = new List<long>();
        for (var i = 0; i < 12; i++)
        {
            mine.Add(await NewTestJobAsync(services, printer));
        }

        async Task<List<long>> Drain(string agent)
        {
            var got = new List<long>();
            while (true)
            {
                var batch = await ClaimAsync(services, agent, 3);
                if (batch.Count == 0)
                {
                    return got;
                }

                got.AddRange(batch.Select(j => j.Id));
            }
        }

        var results = await Task.WhenAll(Task.Run(() => Drain("agente-A"), Ct), Task.Run(() => Drain("agente-B"), Ct));

        Assert.Empty(results[0].Intersect(results[1]));
        var all = results[0].Concat(results[1]).ToHashSet();
        Assert.All(mine, id => Assert.Contains(id, all));
        Assert.Equal(results[0].Count + results[1].Count, all.Count); // ninguno repetido
    }

    [Fact]
    public async Task AgenteCaido_ElTrabajoVuelveACaducarElArrendamientoYElResultadoViejoSeRechaza()
    {
        await using var services = fixture.BuildServices();
        var jobId = await NewTestJobAsync(services, await NewPrinterAsync(services));
        var first = await ClaimJobAsync(services, "agente-A", jobId);
        Assert.Equal(1, first.Attempt);

        // El agente A se cae a mitad: su arrendamiento caduca.
        await SqlAsync(services, "UPDATE tpv.print_jobs SET locked_until = now() - interval '1 minute' WHERE id = {0}", jobId);
        var second = await ClaimJobAsync(services, "agente-B", jobId);

        Assert.Equal(2, second.Attempt);
        Assert.True(second.IsRetry);
        await Assert.ThrowsAsync<DomainException>(() => ReportAsync(services, jobId, "agente-A", true));
        Assert.Equal(ResultOutcome.Recorded, await ReportAsync(services, jobId, "agente-B", true));
        Assert.Equal(PrintJobStatus.Sent, (await ReadJobAsync(services, jobId)).Status);
    }

    [Fact]
    public async Task LaClaveDeDeduplicacion_ImpideEncolarElMismoTicketDosVeces()
    {
        await using var services = fixture.BuildServices();
        var printer = await NewPrinterAsync(services);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var key = "kitchen:" + Guid.NewGuid().ToString("N");
        db.PrintJobs.Add(PrintJob.Create(printer, PrintJobType.KitchenTicket, "order", "1", "{}", key, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
        db.PrintJobs.Add(PrintJob.Create(printer, PrintJobType.KitchenTicket, "order", "1", "{}", key, DateTimeOffset.UtcNow));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [Fact]
    public async Task Fallo_SeReintentaConEsperaYNoSeOfreceAntesDeTiempo()
    {
        await using var services = fixture.BuildServices();
        var jobId = await NewTestJobAsync(services, await NewPrinterAsync(services));
        await ClaimJobAsync(services, "agente-A", jobId);

        await ReportAsync(services, jobId, "agente-A", false, "Sin papel");

        var job = await ReadJobAsync(services, jobId);
        Assert.Equal(PrintJobStatus.Pending, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Equal("Sin papel", job.LastError);
        Assert.True(job.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.DoesNotContain(await ClaimAsync(services, "agente-A"), j => j.Id == jobId);
    }

    [Fact]
    public async Task AgotadosLosIntentos_QuedaFallidoAuditadoSinBloquearAOtrosYSePuedeReintentar()
    {
        await using var services = fixture.BuildServices();
        var printer = await NewPrinterAsync(services);
        var bad = await NewTestJobAsync(services, printer);
        var good = await NewTestJobAsync(services, printer);

        // El agente reclama por tandas (la cola es compartida con otras pruebas) hasta tener los dos; uno falla
        // y el otro se imprime sin que el fallo lo afecte.
        var claimedIds = new HashSet<long>();
        for (var i = 0; i < 30 && !(claimedIds.Contains(bad) && claimedIds.Contains(good)); i++)
        {
            claimedIds.UnionWith((await ClaimAsync(services, "agente-A")).Select(j => j.Id));
        }

        Assert.Contains(bad, claimedIds);
        Assert.Contains(good, claimedIds);
        await ReportAsync(services, bad, "agente-A", false, "Impresora apagada");
        await ReportAsync(services, good, "agente-A", true);

        for (var i = 1; i < PrintJob.DefaultMaxAttempts; i++)
        {
            await SqlAsync(services, "UPDATE tpv.print_jobs SET next_attempt_at = now() - interval '1 second' WHERE id = {0} AND status = 'Pending'", bad);
            await ClaimJobAsync(services, "agente-A", bad);
            await ReportAsync(services, bad, "agente-A", false, "Impresora apagada");
        }

        var failed = await ReadJobAsync(services, bad);
        Assert.Equal(PrintJobStatus.Failed, failed.Status);
        Assert.Equal(PrintJob.DefaultMaxAttempts, failed.Attempts);
        Assert.Equal(PrintJobStatus.Sent, (await ReadJobAsync(services, good)).Status);

        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == AuditActions.PrintJobFailed && e.EntityId == bad.ToString(CultureInfo.InvariantCulture), Ct));
            Assert.Equal(PrintJob.DefaultMaxAttempts, await db.PrintJobAttempts.CountAsync(a => a.JobId == bad && !a.Success, Ct));
        }

        await Full<PrintAdminService, bool>(services, async a =>
        {
            await a.RetryAsync(bad, Ct);
            return true;
        });
        var retried = await ReadJobAsync(services, bad);
        Assert.Equal(PrintJobStatus.Pending, retried.Status);
        Assert.Equal(PrintJob.DefaultMaxAttempts + 3, retried.MaxAttempts);
    }

    [Fact]
    public async Task Enviar_GuardaTicketsYTrabajosDeImpresionJuntosYSinDuplicados()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        var orderId = await Full<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));
        await Full<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.CanaId, 1, null, Guid.NewGuid(), Ct));
        await Full<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.TortillaId, 1, null, Guid.NewGuid(), Ct));
        var key = Guid.NewGuid();

        await Full<OrderService, SendResult>(services, o => o.SendToKitchenAsync(orderId, key, Ct));
        await Full<OrderService, SendResult>(services, o => o.SendToKitchenAsync(orderId, key, Ct)); // doble clic

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        Assert.Equal(2, await db.KitchenTickets.CountAsync(t => t.OrderId == orderId, Ct));
        var jobs = await db.PrintJobs.Where(j => j.DedupeKey.StartsWith($"kitchen:{key:N}:")).ToListAsync(Ct);
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, j => Assert.Equal(orderId.ToString(CultureInfo.InvariantCulture), j.ReferenceId));
    }

    [Fact]
    public async Task EnvioRechazado_NoDejaTrabajosDeImpresionHuerfanos()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        var orderId = await Full<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));
        var key = Guid.NewGuid();

        await Assert.ThrowsAsync<DomainException>(() => Full<OrderService, SendResult>(services, o => o.SendToKitchenAsync(orderId, key, Ct)));

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        Assert.False(await db.PrintJobs.AnyAsync(j => j.DedupeKey.StartsWith($"kitchen:{key:N}:"), Ct));
    }

    [Fact]
    public async Task Cocina_AvanzarPersisteElEstadoEnTicketYComandaYSoloLoHaceQuienTienePermiso()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        var orderId = await Full<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));
        await Full<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.CanaId, 1, null, Guid.NewGuid(), Ct));
        await Full<OrderService, SendResult>(services, o => o.SendToKitchenAsync(orderId, Guid.NewGuid(), Ct));
        var ticketId = (await Full<KitchenService, IReadOnlyList<KitchenTicketView>>(services, k => k.GetBoardAsync(null, Ct)))
            .Single(t => t.OrderId == orderId).Id;

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            SalesHelpers.RunAsync<KitchenService, bool>(services, SalesHelpers.Waiter, async k =>
            {
                await k.AdvanceAsync(ticketId, KitchenTicketStatus.Preparing, Ct);
                return true;
            }));

        await SalesHelpers.RunAsync<KitchenService, bool>(services, SalesHelpers.Kitchen, async k =>
        {
            await k.AdvanceAsync(ticketId, KitchenTicketStatus.Preparing, Ct);
            await k.AdvanceAsync(ticketId, KitchenTicketStatus.Ready, Ct);
            return true;
        });

        var view = await Full<OrderService, OrderView>(services, o => o.GetAsync(orderId, Ct));
        Assert.Equal(OrderLineStatus.Ready, Assert.Single(view.Lines).Status);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var ticket = await db.KitchenTickets.AsNoTracking().SingleAsync(t => t.Id == ticketId, Ct);
        Assert.Equal(KitchenTicketStatus.Ready, ticket.Status);
        Assert.NotNull(ticket.UpdatedBy);
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.KitchenTicketAdvanced && e.EntityId == ticketId.ToString(CultureInfo.InvariantCulture), Ct));
    }

    [Fact]
    public async Task AnularYAvanzarLaCocinaAlaVez_NuncaResucitanLaLineaAnulada()
    {
        await using var services = fixture.BuildServices();
        var world = await SalesHelpers.CreateWorldAsync(services);
        var orderId = await Full<FloorService, long>(services, f => f.OpenTableAsync(world.TableId, 2, Ct));
        var view = await Full<OrderService, OrderView>(services, o => o.AddLineAsync(orderId, world.CanaId, 1, null, Guid.NewGuid(), Ct));
        await Full<OrderService, SendResult>(services, o => o.SendToKitchenAsync(orderId, Guid.NewGuid(), Ct));
        var lineId = view.Lines.Single().Id;
        var ticketId = (await Full<KitchenService, IReadOnlyList<KitchenTicketView>>(services, k => k.GetBoardAsync(null, Ct)))
            .Single(t => t.OrderId == orderId).Id;

        await Task.WhenAll(
            Task.Run(async () =>
            {
                try
                {
                    await Full<KitchenService, bool>(services, async k =>
                    {
                        await k.AdvanceAsync(ticketId, KitchenTicketStatus.Ready, Ct);
                        return true;
                    });
                }
                catch (DomainException)
                {
                    // Si la anulación ganó, el ticket ya está cerrado: es el resultado correcto.
                }
            }, Ct),
            Task.Run(() => Full<OrderService, OrderView>(services, o => o.VoidLineAsync(orderId, lineId, "Cliente se fue", Ct)), Ct));

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var line = await db.OrderLines.AsNoTracking().SingleAsync(l => l.Id == lineId, Ct);
        var ticket = await db.KitchenTickets.AsNoTracking().Include(t => t.Lines).SingleAsync(t => t.Id == ticketId, Ct);
        Assert.Equal(OrderLineStatus.Cancelled, line.Status);
        Assert.Equal(KitchenTicketStatus.Cancelled, ticket.Status);
        Assert.All(ticket.Lines, l => Assert.Equal(KitchenTicketStatus.Cancelled, l.Status));
    }
}
