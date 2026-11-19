using TPVBar.Domain.Catalog;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Sales;

namespace TPVBar.Domain.Tests;

public class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Waiter = Guid.NewGuid();

    private static Product Make(int id, string name, decimal price, PreparationStation station = PreparationStation.Bar) =>
        Product.Create(1, 1, name, price, station, null, null, id, Now).WithId(id);

    private static Order NewOrder() => Order.Open(7, Waiter, 2, Now);

    private static OrderLine Add(Order order, Product product, decimal tax, decimal qty = 1, string? notes = null)
    {
        var line = order.AddLine(product, tax, qty, notes, Waiter, Now);
        if (line.Id == 0)
        {
            line.WithId(order.Lines.Count);
        }

        return line;
    }

    [Fact]
    public void Open_ComienzaAbiertaYVacia()
    {
        var order = NewOrder();

        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Equal(0, order.GrandTotal);
        Assert.Empty(order.Lines);
        Assert.Equal(2, order.GuestCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Open_ComensalesFueraDeRango_Falla(int guests) =>
        Assert.Throws<DomainException>(() => Order.Open(1, Waiter, guests, Now));

    [Fact]
    public void Totales_SeCalculanConIvaIncluidoYCuadran()
    {
        var order = NewOrder();
        Add(order, Make(1, "Caña", 1.80m), 21m, 3);
        Add(order, Make(2, "Tortilla", 3.50m), 10m, 2);

        Assert.Equal(12.40m, order.GrandTotal);
        Assert.Equal(10.82m, order.Subtotal);
        Assert.Equal(1.58m, order.TaxTotal);
        Assert.Equal(order.GrandTotal, order.Subtotal + order.TaxTotal);
    }

    [Fact]
    public void AddLine_GuardaFotografiaDelProducto()
    {
        var order = NewOrder();
        var caña = Make(1, "Caña", 1.80m);

        var line = Add(order, caña, 21m);
        caña.Update(1, 1, "Caña grande", 2.50m, PreparationStation.Bar, null, null, 1, Now);

        Assert.Equal("Caña", line.ProductNameSnapshot);
        Assert.Equal(1.80m, line.UnitPrice);
        Assert.Equal(21m, line.TaxRateSnapshot);
        Assert.Equal(1.80m, order.GrandTotal);
    }

    [Fact]
    public void AddLine_MismoProductoSinEnviar_SumaCantidad()
    {
        var order = NewOrder();
        var caña = Make(1, "Caña", 1.80m);

        Add(order, caña, 21m);
        Add(order, caña, 21m);

        Assert.Single(order.Lines);
        Assert.Equal(2, order.Lines.Single().Quantity);
        Assert.Equal(3.60m, order.GrandTotal);
    }

    [Fact]
    public void AddLine_ConObservacionesDistintas_NoFusiona()
    {
        var order = NewOrder();
        var caña = Make(1, "Caña", 1.80m);

        Add(order, caña, 21m, notes: "bien fría");
        Add(order, caña, 21m);

        Assert.Equal(2, order.Lines.Count);
    }

    [Fact]
    public void AddLine_TrasEnviar_CreaLineaNuevaYNoFusionaConLaEnviada()
    {
        var order = NewOrder();
        var caña = Make(1, "Caña", 1.80m);
        Add(order, caña, 21m);
        order.MarkPendingLinesSent(Now);

        Add(order, caña, 21m);

        Assert.Equal(2, order.Lines.Count);
        Assert.Equal(OrderLineStatus.Pending, order.Lines.Last().Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000)]
    [InlineData(1.0005)]
    public void AddLine_CantidadInvalida_Falla(double quantity)
    {
        var order = NewOrder();

        Assert.Throws<DomainException>(() => order.AddLine(Make(1, "Caña", 1.80m), 21m, (decimal)quantity, null, Waiter, Now));
    }

    [Fact]
    public void ChangeQuantity_SoloLineasSinEnviar()
    {
        var order = NewOrder();
        var line = Add(order, Make(1, "Caña", 1.80m), 21m);
        order.ChangeQuantity(line.Id, 4, Now);
        Assert.Equal(7.20m, order.GrandTotal);

        order.MarkPendingLinesSent(Now);

        Assert.Throws<DomainException>(() => order.ChangeQuantity(line.Id, 5, Now));
    }

    [Fact]
    public void RemoveLine_QuitaLaLineaSinEnviarYRecalcula()
    {
        var order = NewOrder();
        var line = Add(order, Make(1, "Caña", 1.80m), 21m);

        order.RemoveLine(line.Id, Now);

        Assert.Empty(order.Lines);
        Assert.Equal(0, order.GrandTotal);
    }

    [Fact]
    public void RemoveLine_YaEnviada_ObligaAAnular()
    {
        var order = NewOrder();
        var line = Add(order, Make(1, "Caña", 1.80m), 21m);
        order.MarkPendingLinesSent(Now);

        Assert.Throws<DomainException>(() => order.RemoveLine(line.Id, Now));
    }

    [Fact]
    public void VoidLine_ConservaLaLineaConMotivoYDejaDeSumar()
    {
        var order = NewOrder();
        var line = Add(order, Make(1, "Caña", 1.80m), 21m, 2);
        order.MarkPendingLinesSent(Now);
        var by = Guid.NewGuid();

        order.VoidLine(line.Id, "  Cliente se equivocó ", by, Now);

        Assert.Single(order.Lines);
        Assert.Equal(OrderLineStatus.Cancelled, line.Status);
        Assert.Equal("Cliente se equivocó", line.VoidReason);
        Assert.Equal(by, line.VoidedBy);
        Assert.Equal(0, order.GrandTotal);
    }

    [Fact]
    public void VoidLine_SinMotivoOSinEnviar_Falla()
    {
        var order = NewOrder();
        var line = Add(order, Make(1, "Caña", 1.80m), 21m);

        Assert.Throws<DomainException>(() => order.VoidLine(line.Id, "motivo", Waiter, Now)); // sin enviar
        order.MarkPendingLinesSent(Now);
        Assert.Throws<DomainException>(() => order.VoidLine(line.Id, "  ", Waiter, Now));
    }

    [Fact]
    public void VoidLine_DosVeces_Falla()
    {
        var order = NewOrder();
        var line = Add(order, Make(1, "Caña", 1.80m), 21m);
        order.MarkPendingLinesSent(Now);
        order.VoidLine(line.Id, "error", Waiter, Now);

        Assert.Throws<DomainException>(() => order.VoidLine(line.Id, "otra vez", Waiter, Now));
    }

    [Fact]
    public void MarkPendingLinesSent_MarcaSoloLasPendientesYDevuelveLasAfectadas()
    {
        var order = NewOrder();
        Add(order, Make(1, "Caña", 1.80m), 21m);
        order.MarkPendingLinesSent(Now);
        var nueva = Add(order, Make(2, "Tortilla", 3.50m, PreparationStation.Kitchen), 10m);

        var sent = order.MarkPendingLinesSent(Now);

        Assert.Equal([nueva], sent);
        Assert.All(order.Lines, l => Assert.Equal(OrderLineStatus.Sent, l.Status));
    }

    [Fact]
    public void MarkPendingLinesSent_SinPendientes_Falla()
    {
        var order = NewOrder();

        Assert.Throws<DomainException>(() => order.MarkPendingLinesSent(Now));
    }

    [Fact]
    public void RequestBill_ExigeLineasActivasYNadaPendiente()
    {
        var order = NewOrder();
        Assert.Throws<DomainException>(() => order.RequestBill(Now)); // vacía

        Add(order, Make(1, "Caña", 1.80m), 21m);
        Assert.Throws<DomainException>(() => order.RequestBill(Now)); // pendiente de enviar

        order.MarkPendingLinesSent(Now);
        order.RequestBill(Now);

        Assert.Equal(OrderStatus.PendingPayment, order.Status);
    }

    [Fact]
    public void PendienteDeCobro_NoPermiteModificar_HastaReabrir()
    {
        var order = NewOrder();
        Add(order, Make(1, "Caña", 1.80m), 21m);
        order.MarkPendingLinesSent(Now);
        order.RequestBill(Now);

        Assert.Throws<DomainException>(() => order.AddLine(Make(2, "Café", 1.30m), 10m, 1, null, Waiter, Now));

        order.Reopen(Now);
        order.AddLine(Make(2, "Café", 1.30m), 10m, 1, null, Waiter, Now);
        Assert.Equal(2, order.Lines.Count);
    }

    [Fact]
    public void Cancel_ConLineasSinEnviar_LiberaLaMesa()
    {
        var order = NewOrder();
        Add(order, Make(1, "Caña", 1.80m), 21m);

        order.Cancel(Now);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.NotNull(order.ClosedAt);
        Assert.Empty(order.Lines);
    }

    [Fact]
    public void Cancel_ConProductosYaEnviados_Falla()
    {
        var order = NewOrder();
        Add(order, Make(1, "Caña", 1.80m), 21m);
        order.MarkPendingLinesSent(Now);

        Assert.Throws<DomainException>(() => order.Cancel(Now));
    }

    [Fact]
    public void Cancel_TrasAnularLoEnviado_Permite()
    {
        var order = NewOrder();
        var line = Add(order, Make(1, "Caña", 1.80m), 21m);
        order.MarkPendingLinesSent(Now);
        order.VoidLine(line.Id, "error", Waiter, Now);

        order.Cancel(Now);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public void ComandaCancelada_NoSePuedeModificar()
    {
        var order = NewOrder();
        order.Cancel(Now);

        Assert.Throws<DomainException>(() => order.AddLine(Make(1, "Caña", 1.80m), 21m, 1, null, Waiter, Now));
        Assert.Throws<DomainException>(() => order.Reopen(Now));
    }
}
