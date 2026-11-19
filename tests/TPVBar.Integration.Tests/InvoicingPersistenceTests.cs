using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TPVBar.Application.Features.Invoicing;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Invoicing;
using TPVBar.Infrastructure.Persistence;

namespace TPVBar.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class InvoicingPersistenceTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly InvoiceRecipient Customer = new("Cliente Ejemplo S.L.", "A58818501", "Calle Falsa 123, Madrid");

    private static Task<T> Full<TService, T>(IServiceProvider sp, Func<TService, Task<T>> op)
        where TService : notnull => CashHelpers.Full(sp, op);

    /// <summary>Configura el emisor y devuelve una comanda de 7,10 € ya cobrada en efectivo.</summary>
    private static async Task<long> PaidOrderAsync(IServiceProvider sp)
    {
        await Full<InvoiceService, bool>(sp, async s =>
        {
            await s.SaveSettingsAsync("Bar de Pruebas S.L.", "B12345674", "Calle Mayor 1, 28001 Madrid", 400m, Ct);
            return true;
        });
        await CashHelpers.EnsureOpenAsync(sp);
        var orderId = await CashHelpers.SentOrderAsync(sp);
        await CashHelpers.PayAsync(sp, orderId, "CASH", 7.10m, 10m);
        return orderId;
    }

    private static Task<IssueInvoiceResult> IssueAsync(IServiceProvider sp, long orderId, InvoiceKind kind = InvoiceKind.Simplified) =>
        Full<InvoiceService, IssueInvoiceResult>(sp, s => s.IssueAsync(
            new IssueInvoiceRequest(orderId, kind, kind == InvoiceKind.Complete ? Customer : null, Guid.NewGuid()), Ct));

    [Fact]
    public async Task EmitirFactura_SeGuardaConLineasRegistroYCabezaDeCadena()
    {
        await using var services = fixture.BuildServices();
        var orderId = await PaidOrderAsync(services);

        var issued = await IssueAsync(services, orderId);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.Lines).SingleAsync(i => i.Id == issued.InvoiceId, Ct);
        Assert.Equal(7.10m, invoice.Total);
        Assert.Equal(2, invoice.Lines.Count);
        Assert.Equal(invoice.Total, invoice.Lines.Sum(l => l.Total));
        Assert.Equal("F2", invoice.TypeCode);

        var record = await db.BillingRecords.AsNoTracking().SingleAsync(r => r.InvoiceId == invoice.Id && r.Type == BillingRecordType.Alta, Ct);
        Assert.Equal(record.Hash, record.RecomputeHash());
        Assert.Equal(SubmissionStatus.Pending, (await db.BillingSubmissions.AsNoTracking().SingleAsync(s => s.RecordSequence == record.Sequence, Ct)).Status);
        var head = await db.BillingChainHeads.AsNoTracking().SingleAsync(Ct);
        Assert.True(head.LastSequence >= record.Sequence);

        var view = await Full<InvoiceService, InvoiceView>(services, s => s.GetAsync(invoice.Id, Ct));
        Assert.Equal(invoice.FullNumber, view.FullNumber);
        Assert.Equal(record.Hash, view.RecordHash);
        var check = await Full<InvoiceService, ChainVerification>(services, s => s.VerifyChainAsync(Ct));
        Assert.True(check.Ok, check.Problem);
    }

    [Fact]
    public async Task FacturaCompletaRectificativaYAnulacion_SeEncadenanYLaCadenaSigueSiendoValida()
    {
        await using var services = fixture.BuildServices();
        var first = await PaidOrderAsync(services);
        var second = await PaidOrderAsync(services);

        var complete = await IssueAsync(services, first, InvoiceKind.Complete);
        var simplified = await IssueAsync(services, second);
        var line = (await Full<InvoiceService, InvoiceView>(services, s => s.GetAsync(complete.InvoiceId, Ct))).Lines[0];
        var rect = await Full<InvoiceService, IssueInvoiceResult>(services, s => s.RectifyAsync(
            new RectifyInvoiceRequest(complete.InvoiceId, "Devolución parcial", [new(line.Id, 1)], Guid.NewGuid()), Ct));
        await Full<InvoiceService, bool>(services, async s =>
        {
            await s.CancelAsync(simplified.InvoiceId, "Emitida por error", Guid.NewGuid(), Ct);
            return true;
        });

        Assert.StartsWith("R", rect.FullNumber, StringComparison.Ordinal);
        var check = await Full<InvoiceService, ChainVerification>(services, s => s.VerifyChainAsync(Ct));
        Assert.True(check.Ok, check.Problem);
        var view = await Full<InvoiceService, InvoiceView>(services, s => s.GetAsync(simplified.InvoiceId, Ct));
        Assert.True(view.Cancelled);
    }

    [Fact]
    public async Task EmisionesSimultaneas_ObtienenNumerosDistintosSinHuecosYLaCadenaNoSeBifurca()
    {
        await using var services = fixture.BuildServices();
        var orders = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            orders.Add(await PaidOrderAsync(services));
        }

        var results = await Task.WhenAll(orders.Select(o => IssueAsync(services, o)));

        Assert.Equal(5, results.Select(r => r.FullNumber).Distinct(StringComparer.Ordinal).Count());
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        var numbers = await db.Invoices.AsNoTracking().Select(i => new { i.Series, i.Number }).ToListAsync(Ct);
        foreach (var group in numbers.GroupBy(n => n.Series))
        {
            // Correlativa: del 1 al máximo, sin huecos ni repetidos.
            Assert.Equal(Enumerable.Range(1, group.Count()), group.Select(n => n.Number).Order());
        }

        var check = await Full<InvoiceService, ChainVerification>(services, s => s.VerifyChainAsync(Ct));
        Assert.True(check.Ok, check.Problem);
    }

    [Fact]
    public async Task EmitirLaMismaComandaVariasVecesALaVez_SoloUnaSale()
    {
        await using var services = fixture.BuildServices();
        var orderId = await PaidOrderAsync(services);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            try
            {
                return (await IssueAsync(services, orderId)).InvoiceId;
            }
            catch (DomainException)
            {
                return 0L;
            }
        }));

        Assert.Equal(1, attempts.Count(id => id > 0));
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();
        Assert.Equal(1, await db.Invoices.AsNoTracking().CountAsync(i => i.OrderId == orderId, Ct));
    }

    [Fact]
    public async Task FacturasLineasYRegistros_SonInmutablesYLaCabezaNoSePuedeBorrar()
    {
        await using var services = fixture.BuildServices();
        await IssueAsync(services, await PaidOrderAsync(services));
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();

        var statements = new[]
        {
            "UPDATE tpv.invoices SET total = total + 1",
            "DELETE FROM tpv.invoices",
            "TRUNCATE tpv.invoices",
            "UPDATE tpv.invoice_lines SET total = total + 1",
            "DELETE FROM tpv.invoice_lines",
            "TRUNCATE tpv.invoice_lines",
            "UPDATE tpv.billing_records SET grand_total = grand_total + 1",
            "DELETE FROM tpv.billing_records",
            "TRUNCATE tpv.billing_records",
            "DELETE FROM tpv.billing_chain_head",
            "TRUNCATE tpv.billing_chain_head",
        };
        foreach (var sql in statements)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, Ct));

            // TRUNCATE de tablas referenciadas por claves foráneas ya lo rechaza PostgreSQL (0A000) antes del trigger.
            var acceptable = sql.StartsWith("TRUNCATE", StringComparison.Ordinal)
                ? new[] { PostgresErrorCodes.RestrictViolation, PostgresErrorCodes.FeatureNotSupported }
                : [PostgresErrorCodes.RestrictViolation];
            Assert.Contains(ex.SqlState, acceptable);
        }
    }

    [Fact]
    public async Task LaBaseDeDatos_RechazaFacturasIncoherentes()
    {
        await using var services = fixture.BuildServices();
        var orderId = await PaidOrderAsync(services);
        var issued = await IssueAsync(services, orderId);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TpvBarDbContext>();

        const string columns = "series, number, full_number, kind, type_code, order_id, issued_at, issue_date, issuer_name, issuer_nif, issuer_address, base, tax, total, created_by";

        // Misma serie y número que una existente (numeración duplicada).
        var dup = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO tpv.invoices ({columns}) SELECT series, number, full_number || 'x', kind, type_code, order_id, issued_at, issue_date, issuer_name, issuer_nif, issuer_address, base, tax, total, created_by FROM tpv.invoices WHERE id = {{0}}",
            [issued.InvoiceId],
            Ct));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, dup.SqlState);

        // Base + IVA distinto del total.
        var bad = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO tpv.invoices ({columns}) SELECT 'X9999', 1, 'X9999-000001', kind, type_code, order_id, issued_at, issue_date, issuer_name, issuer_nif, issuer_address, base, tax + 1, total, created_by FROM tpv.invoices WHERE id = {{0}}",
            [issued.InvoiceId],
            Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, bad.SqlState);

        // Factura completa sin datos del cliente.
        var noRecipient = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO tpv.invoices ({columns}) SELECT 'X9998', 1, 'X9998-000001', 'Complete', 'F1', order_id, issued_at, issue_date, issuer_name, issuer_nif, issuer_address, base, tax, total, created_by FROM tpv.invoices WHERE id = {{0}}",
            [issued.InvoiceId],
            Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, noRecipient.SqlState);
    }
}
