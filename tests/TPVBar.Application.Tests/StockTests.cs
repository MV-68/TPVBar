using TPVBar.Application.Common;
using TPVBar.Application.Features.Catalog;
using TPVBar.Application.Features.Inventory;
using TPVBar.Domain.Auditing;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Common;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Inventory;
using TPVBar.Domain.Sales;
using TPVBar.Domain.Security;

namespace TPVBar.Application.Tests;

public class StockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Permission[] All =
    [
        Permission.TakeOrders, Permission.VoidLines, Permission.AdjustStock, Permission.ManageConfiguration, Permission.ManageCatalog, Permission.ModifyPrices,
    ];

    private static SalesFixture NewFixture(decimal stock = 10m, decimal minimum = 2m)
    {
        var f = new SalesFixture(All).WithPrinters();
        f.WithStock(f.Cana, stock, minimum);
        return f;
    }

    private static async Task<(long OrderId, long LineId)> OrderAsync(SalesFixture f, decimal quantity, int tableOffset = 0)
    {
        var table = tableOffset == 0 ? f.Mesa1 : f.Mesa2;
        var orderId = await f.Floor().OpenTableAsync(table.Id, 2, Ct);
        var view = await f.Orders().AddLineAsync(orderId, f.Cana.Id, quantity, null, Guid.NewGuid(), Ct);
        return (orderId, view.Lines.Single().Id);
    }

    private static void AssertLedgerMatches(SalesFixture f)
    {
        // El stock actual siempre es la suma de sus movimientos (con el saldo guardado en cada uno).
        var movements = f.Data.StockMovementList.Where(m => m.ProductId == f.Cana.Id).ToList();
        Assert.Equal(movements.Sum(m => m.QuantityDelta), f.Cana.StockQuantity);
        Assert.Equal(f.Cana.StockQuantity, movements[^1].QuantityAfter);
    }

    // ---------- Descuento al enviar ----------

    [Fact]
    public async Task Enviar_DescuentaElStockYRegistraUnMovimientoPorLinea()
    {
        var f = NewFixture();
        var (orderId, lineId) = await OrderAsync(f, 3);

        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        Assert.Equal(7m, f.Cana.StockQuantity);
        var sale = Assert.Single(f.Data.StockMovementList, m => m.Reason == StockMovementReason.Sale);
        Assert.Equal((-3m, 7m), (sale.QuantityDelta, sale.QuantityAfter));
        Assert.Equal(("order_line", lineId.ToString(System.Globalization.CultureInfo.InvariantCulture)), (sale.ReferenceType, sale.ReferenceId));
        Assert.Equal(f.UserId, sale.CreatedBy);
        AssertLedgerMatches(f);
    }

    [Fact]
    public async Task AnadirLineas_NoDescuentaNada_SoloElEnvio()
    {
        var f = NewFixture();

        await OrderAsync(f, 3);

        Assert.Equal(10m, f.Cana.StockQuantity);
        Assert.DoesNotContain(f.Data.StockMovementList, m => m.Reason == StockMovementReason.Sale);
    }

    [Fact]
    public async Task Enviar_SinStockSuficiente_RechazaTodoElEnvioYNoGuardaNada()
    {
        var f = NewFixture(stock: 2m);
        var (orderId, _) = await OrderAsync(f, 3);

        var ex = await Assert.ThrowsAsync<DomainException>(() => f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct));

        Assert.Contains("Stock insuficiente", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Caña", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2m, f.Cana.StockQuantity);
        Assert.DoesNotContain(f.Data.StockMovementList, m => m.Reason == StockMovementReason.Sale);
    }

    [Fact]
    public async Task Enviar_ConStockNegativoAutorizado_Funciona()
    {
        var f = NewFixture(stock: 1m);
        await f.Stock().SetAllowNegativeAsync(true, Ct);
        var (orderId, _) = await OrderAsync(f, 3);

        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        Assert.Equal(-2m, f.Cana.StockQuantity);
        AssertLedgerMatches(f);
        var overview = await f.Stock().GetOverviewAsync(Ct);
        Assert.Equal(StockStatus.Negative, overview.Rows.Single().Status);
        Assert.True(overview.AllowNegative);
    }

    [Fact]
    public async Task PermitirStockNegativo_ExigePermisoDeConfiguracionYQuedaAuditado()
    {
        var f = NewFixture();
        f.User.Set(f.UserId, [Permission.AdjustStock], "test");

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Stock().SetAllowNegativeAsync(true, Ct));

        f.User.Set(f.UserId, All, "test");
        await f.Stock().SetAllowNegativeAsync(true, Ct);
        await f.Stock().SetAllowNegativeAsync(false, Ct);
        Assert.Equal(2, f.Store.Audit.Count(e => e.Action == AuditActions.StockSettingChanged));
        Assert.Equal("false", Assert.Single(f.Data.SettingList).Value);
    }

    [Fact]
    public async Task Enviar_ProductoSinControlDeStock_NoGeneraMovimientos()
    {
        var f = NewFixture();
        var orderId = await f.Floor().OpenTableAsync(f.Mesa1.Id, 2, Ct);
        await f.Orders().AddLineAsync(orderId, f.Tortilla.Id, 2, null, Guid.NewGuid(), Ct);

        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        Assert.DoesNotContain(f.Data.StockMovementList, m => m.Reason == StockMovementReason.Sale);
        Assert.Equal(10m, f.Cana.StockQuantity);
    }

    [Fact]
    public async Task Enviar_RepetirLaClave_NoDescuentaDosVeces()
    {
        var f = NewFixture();
        var (orderId, _) = await OrderAsync(f, 3);
        var key = Guid.NewGuid();

        await f.Orders().SendToKitchenAsync(orderId, key, Ct);
        await f.Orders().SendToKitchenAsync(orderId, key, Ct);

        Assert.Equal(7m, f.Cana.StockQuantity);
        Assert.Single(f.Data.StockMovementList, m => m.Reason == StockMovementReason.Sale);
    }

    // ---------- Anulación ----------

    [Fact]
    public async Task Anular_UnaLineaEnviada_ReponeElStock()
    {
        var f = NewFixture();
        var (orderId, lineId) = await OrderAsync(f, 3);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);

        await f.Orders().VoidLineAsync(orderId, lineId, "Cliente se fue", Ct);

        Assert.Equal(10m, f.Cana.StockQuantity);
        var back = Assert.Single(f.Data.StockMovementList, m => m.Reason == StockMovementReason.VoidReturn);
        Assert.Equal(3m, back.QuantityDelta);
        Assert.Equal(lineId.ToString(System.Globalization.CultureInfo.InvariantCulture), back.ReferenceId);
        AssertLedgerMatches(f);
    }

    [Fact]
    public async Task Anular_UnaLineaQueNuncaSeDescuento_NoInflaElStock()
    {
        var f = new SalesFixture(All).WithPrinters(); // sin control de stock al vender
        var (orderId, lineId) = await OrderAsync(f, 3);
        await f.Orders().SendToKitchenAsync(orderId, Guid.NewGuid(), Ct);
        f.WithStock(f.Cana, 10m, 2m); // se activa el control DESPUÉS de vender

        await f.Orders().VoidLineAsync(orderId, lineId, "error", Ct);

        Assert.Equal(10m, f.Cana.StockQuantity); // no se repone lo que no se descontó
        Assert.DoesNotContain(f.Data.StockMovementList, m => m.Reason == StockMovementReason.VoidReturn);
    }

    [Fact]
    public async Task QuitarUnaLineaSinEnviar_NoToqueElStock()
    {
        var f = NewFixture();
        var (orderId, lineId) = await OrderAsync(f, 3);

        await f.Orders().RemoveLineAsync(orderId, lineId, Ct);

        Assert.Equal(10m, f.Cana.StockQuantity);
        Assert.DoesNotContain(f.Data.StockMovementList, m => m.Reason is StockMovementReason.Sale or StockMovementReason.VoidReturn);
    }

    // ---------- Avisos ----------

    [Fact]
    public async Task AvisoDeStockBajo_SeEmiteUnaSolaVezAlCruzarElMinimo()
    {
        var f = NewFixture(stock: 5m, minimum: 2m);
        var (order1, _) = await OrderAsync(f, 3); // 5 → 2: cruza el mínimo (2)
        f.Events.Drain();
        await f.Orders().SendToKitchenAsync(order1, Guid.NewGuid(), Ct);
        Assert.Single(f.Events.Drain(), e => e.Name == OperationEventNames.StockAlertRaised && e.ProductId == f.Cana.Id);

        var (order2, _) = await OrderAsync(f, 1, tableOffset: 1); // 2 → 1: ya estaba bajo mínimos
        f.Events.Drain();
        await f.Orders().SendToKitchenAsync(order2, Guid.NewGuid(), Ct);

        var events = f.Events.Drain();
        Assert.DoesNotContain(events, e => e.Name == OperationEventNames.StockAlertRaised);
        Assert.Contains(events, e => e.Name == OperationEventNames.StockChanged);
    }

    // ---------- Entradas, salidas y ajustes ----------

    [Fact]
    public async Task Entrada_SumaYRegistraMovimientoAuditado()
    {
        var f = NewFixture();

        await f.Stock().RegisterEntryAsync(f.Cana.Id, 24m, "Pedido del proveedor", Guid.NewGuid(), Ct);

        Assert.Equal(34m, f.Cana.StockQuantity);
        var m = f.Data.StockMovementList.Last();
        Assert.Equal((StockMovementReason.Entry, 24m, 34m, "Pedido del proveedor"), (m.Reason, m.QuantityDelta, m.QuantityAfter, m.Notes));
        Assert.Contains(f.Store.Audit, e => e.Action == AuditActions.StockMovementRegistered && e.EntityId == f.Cana.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AssertLedgerMatches(f);
    }

    [Fact]
    public async Task Salida_RestaConMotivoObligatorioYNoDejaNegativo()
    {
        var f = NewFixture();

        await Assert.ThrowsAsync<DomainException>(() => f.Stock().RegisterExitAsync(f.Cana.Id, 2m, " ", Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Stock().RegisterExitAsync(f.Cana.Id, 11m, "merma", Guid.NewGuid(), Ct));
        await f.Stock().RegisterExitAsync(f.Cana.Id, 4m, "Rotura", Guid.NewGuid(), Ct);

        Assert.Equal(6m, f.Cana.StockQuantity);
        AssertLedgerMatches(f);
    }

    [Fact]
    public async Task Ajuste_LlevaLasExistenciasAlRecuentoYExigeMotivo()
    {
        var f = NewFixture();

        await Assert.ThrowsAsync<DomainException>(() => f.Stock().AdjustToCountAsync(f.Cana.Id, 7m, "", Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Stock().AdjustToCountAsync(f.Cana.Id, 10m, "igual", Guid.NewGuid(), Ct)); // sin diferencia
        await f.Stock().AdjustToCountAsync(f.Cana.Id, 7m, "Recuento de cierre", Guid.NewGuid(), Ct);

        Assert.Equal(7m, f.Cana.StockQuantity);
        Assert.Equal(-3m, f.Data.StockMovementList.Last().QuantityDelta);
        AssertLedgerMatches(f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.0005)]
    public async Task Entrada_CantidadInvalida_SeRechaza(double quantity)
    {
        var f = NewFixture();

        await Assert.ThrowsAsync<DomainException>(() => f.Stock().RegisterEntryAsync(f.Cana.Id, (decimal)quantity, null, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Movimientos_MismaClave_NoSeRegistranDosVeces()
    {
        var f = NewFixture();
        var key = Guid.NewGuid();

        await f.Stock().RegisterEntryAsync(f.Cana.Id, 5m, null, key, Ct);
        await f.Stock().RegisterEntryAsync(f.Cana.Id, 5m, null, key, Ct);

        Assert.Equal(15m, f.Cana.StockQuantity);
    }

    [Fact]
    public async Task Movimientos_ProductoSinControlONoExistente_Falla()
    {
        var f = NewFixture();

        await Assert.ThrowsAsync<DomainException>(() => f.Stock().RegisterEntryAsync(f.Tortilla.Id, 5m, null, Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Stock().RegisterEntryAsync(999, 5m, null, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Almacen_SinPermisoAdjustStock_EsDenegado()
    {
        var f = NewFixture();
        f.User.Set(f.UserId, [Permission.TakeOrders], "test");

        await Assert.ThrowsAsync<ForbiddenException>(() => f.Stock().GetOverviewAsync(Ct));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Stock().RegisterEntryAsync(f.Cana.Id, 1m, null, Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Stock().GetAttentionCountAsync(Ct));
    }

    [Theory]
    [InlineData(10, 2, StockStatus.Ok)]
    [InlineData(2, 2, StockStatus.Low)]
    [InlineData(1, 2, StockStatus.Low)]
    [InlineData(0, 2, StockStatus.OutOfStock)]
    public void EstadoDelStock_SegunExistenciasYMinimo(double quantity, double minimum, StockStatus expected) =>
        Assert.Equal(expected, new StockRow(1, "X", "Cat", (decimal)quantity, (decimal)minimum, "unidad", true).Status);

    [Fact]
    public async Task Almacen_ResumenYContadorDeAtencion_SoloActivosBajoMinimos()
    {
        var f = NewFixture(stock: 2m, minimum: 2m);
        f.WithStock(f.Tortilla, 50m, 5m);

        var overview = await f.Stock().GetOverviewAsync(Ct);

        Assert.Equal(2, overview.Rows.Count);
        Assert.Equal(1, overview.AttentionCount);
        Assert.Equal(1, await f.Stock().GetAttentionCountAsync(Ct));
        f.Cana.SetActive(false, SalesFixture.Start);
        Assert.Equal(0, await f.Stock().GetAttentionCountAsync(Ct)); // un producto inactivo no avisa
    }

    [Fact]
    public async Task Historial_MuestraMovimientosConElNombreDelUsuario()
    {
        var f = NewFixture().WithPaymentMethods();
        await f.Stock().RegisterEntryAsync(f.Cana.Id, 5m, "reposición", Guid.NewGuid(), Ct);

        var history = await f.Stock().ListMovementsAsync(f.Cana.Id, 50, Ct);

        Assert.Equal(2, history.Count);
        Assert.Equal("Cajero de prueba", history[0].By);
        Assert.Equal(StockMovementReason.Entry, history[0].Reason);
        Assert.Equal(15m, history[0].After);
    }

    // ---------- Ficha de producto ----------

    [Fact]
    public async Task Catalogo_ConfigurarElControlDeStockEnLaFicha_NoCambiaLasExistencias()
    {
        var f = NewFixture();
        var input = new ProductInput(f.Cana.CategoryId, f.General.Id, "Caña", 1.80m, PreparationStation.Bar, null, null, 1, StockControlled: true, StockMinimum: 8m, StockUnit: "botella");

        await f.Catalog().UpdateProductAsync(f.Cana.Id, input, Ct);

        Assert.Equal((true, 8m, "botella", 10m), (f.Cana.StockControlled, f.Cana.StockMinimum, f.Cana.StockUnit, f.Cana.StockQuantity));
        var row = (await f.Catalog().ListProductsAsync(Ct)).Single(p => p.Id == f.Cana.Id);
        Assert.Equal((true, 10m, 8m, "botella"), (row.StockControlled, row.StockQuantity, row.StockMinimum, row.StockUnit));
    }

    [Fact]
    public async Task Catalogo_MinimoNegativoOUnidadLarga_SeRechaza()
    {
        var f = NewFixture();
        var bad = new ProductInput(f.Cana.CategoryId, f.General.Id, "Caña", 1.80m, PreparationStation.Bar, null, null, 1, StockControlled: true, StockMinimum: -1m);
        var longUnit = bad with { StockMinimum = 1m, StockUnit = new string('x', 21) };

        await Assert.ThrowsAsync<DomainException>(() => f.Catalog().UpdateProductAsync(f.Cana.Id, bad, Ct));
        await Assert.ThrowsAsync<DomainException>(() => f.Catalog().UpdateProductAsync(f.Cana.Id, longUnit, Ct));
    }

    [Fact]
    public void Producto_StockBajo_CuandoControladoYIgualOInferiorAlMinimo()
    {
        var product = Product.Create(1, 1, "Caña", 1.80m, PreparationStation.Bar, null, null, 1, SalesFixture.Start);
        Assert.False(product.IsLowStock); // sin control no hay aviso

        product.ConfigureStock(true, 2m, null, SalesFixture.Start);
        product.ApplyStockDelta(2m, false, SalesFixture.Start);
        Assert.True(product.IsLowStock);
        product.ApplyStockDelta(5m, false, SalesFixture.Start);
        Assert.False(product.IsLowStock);
    }
}
