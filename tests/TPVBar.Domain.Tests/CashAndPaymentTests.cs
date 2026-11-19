using TPVBar.Domain.Cash;
using TPVBar.Domain.Catalog;
using TPVBar.Domain.Exceptions;
using TPVBar.Domain.Sales;

namespace TPVBar.Domain.Tests;

public class CashAndPaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid User = Guid.NewGuid();

    private static PaymentMethod Cash() => PaymentMethod.Create("CASH", "Efectivo", false, true, 1).WithId(1);

    private static PaymentMethod Card() => PaymentMethod.Create("CARD", "Tarjeta", false, false, 2).WithId(2);

    /// <summary>Comanda de 7,10 € con todas sus líneas enviadas.</summary>
    private static Order SentOrder()
    {
        var order = Order.Open(1, User, 2, Now);
        var cana = Product.Create(1, 1, "Caña", 1.80m, PreparationStation.Bar, null, null, 1, Now).WithId(1);
        var tortilla = Product.Create(1, 1, "Tortilla", 3.50m, PreparationStation.Kitchen, null, null, 2, Now).WithId(2);
        order.AddLine(cana, 21m, 2, null, User, Now).WithId(1);
        order.AddLine(tortilla, 10m, 1, null, User, Now).WithId(2);
        order.MarkPendingLinesSent(Now);
        return order;
    }

    // ---------- Comanda: cobros ----------

    [Fact]
    public void Cobro_Total_DejaLaComandaCobradaYCerrada()
    {
        var order = SentOrder();

        order.RegisterPayment(7.10m, Now);

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(0m, order.Balance);
        Assert.Equal(Now, order.ClosedAt);
    }

    [Fact]
    public void Cobro_Parcial_PasaAPendienteDeCobroYConservaElSaldo()
    {
        var order = SentOrder();

        order.RegisterPayment(2.00m, Now);

        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(5.10m, order.Balance);
        Assert.Null(order.ClosedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(7.11)]
    [InlineData(1.001)]
    public void Cobro_ImporteInvalidoOMayorQueElSaldo_Falla(double amount) =>
        Assert.Throws<DomainException>(() => SentOrder().RegisterPayment((decimal)amount, Now));

    [Fact]
    public void Cobro_SumaDePagosParcialesNuncaSuperaElTotal()
    {
        var order = SentOrder();
        order.RegisterPayment(3m, Now);
        order.RegisterPayment(3m, Now);

        Assert.Throws<DomainException>(() => order.RegisterPayment(1.11m, Now));
        order.RegisterPayment(1.10m, Now);

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(order.GrandTotal, order.PaidTotal);
    }

    [Fact]
    public void Cobro_ConLineasSinEnviarOSinImporte_Falla()
    {
        var order = Order.Open(1, User, 1, Now);
        Assert.Throws<DomainException>(() => order.RegisterPayment(1m, Now)); // vacía

        var product = Product.Create(1, 1, "Caña", 1.80m, PreparationStation.Bar, null, null, 1, Now).WithId(1);
        order.AddLine(product, 21m, 1, null, User, Now);
        Assert.Throws<DomainException>(() => order.RegisterPayment(1m, Now)); // sin enviar
    }

    [Fact]
    public void Cobro_ComandaCobradaOCancelada_NoAdmiteMasCobros()
    {
        var paid = SentOrder();
        paid.RegisterPayment(7.10m, Now);
        Assert.Throws<DomainException>(() => paid.RegisterPayment(1m, Now));

        var cancelled = Order.Open(1, User, 1, Now);
        cancelled.Cancel(Now);
        Assert.Throws<DomainException>(() => cancelled.RegisterPayment(1m, Now));
    }

    [Fact]
    public void ConCobros_LaComandaNoSePuedeEditarReabrirNiCancelar()
    {
        var order = SentOrder();
        order.RegisterPayment(2m, Now);
        var product = Product.Create(1, 1, "Café", 1.30m, PreparationStation.Bar, null, null, 3, Now).WithId(3);

        Assert.Throws<DomainException>(() => order.AddLine(product, 10m, 1, null, User, Now));
        Assert.Throws<DomainException>(() => order.VoidLine(1, "motivo", User, Now));
        Assert.Throws<DomainException>(() => order.Reopen(Now));
        Assert.Throws<DomainException>(() => order.Cancel(Now));
    }

    [Fact]
    public void Devolucion_BajaLoCobradoSinReabrirLaComandaCobrada()
    {
        var order = SentOrder();
        order.RegisterPayment(7.10m, Now);

        order.RegisterRefund(2m, Now);

        Assert.Equal(5.10m, order.PaidTotal);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Throws<DomainException>(() => order.RegisterRefund(5.11m, Now));
        Assert.Throws<DomainException>(() => order.RegisterRefund(0m, Now));
    }

    [Fact]
    public void Reabrir_SoloSinCobros_YTrasDevolverTodoYaNoHaySaldoCobrado()
    {
        var order = SentOrder();
        order.RegisterPayment(2m, Now);
        order.RegisterRefund(2m, Now);

        order.Reopen(Now);

        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Equal(0m, order.PaidTotal);
    }

    // ---------- Pago ----------

    [Fact]
    public void Pago_Efectivo_CalculaElCambio()
    {
        var payment = Payment.Create(1, 1, Cash(), 7.10m, 10m, null, User, Now);

        Assert.Equal(7.10m, payment.Amount);
        Assert.Equal(10m, payment.TenderedAmount);
        Assert.Equal(2.90m, payment.ChangeGiven);
    }

    [Fact]
    public void Pago_EfectivoSinEntregado_SeEntiendeExacto()
    {
        var payment = Payment.Create(1, 1, Cash(), 5m, null, null, User, Now);

        Assert.Equal(5m, payment.TenderedAmount);
        Assert.Equal(0m, payment.ChangeGiven);
    }

    [Fact]
    public void Pago_Tarjeta_NoLlevaEntregadoNiCambio()
    {
        var payment = Payment.Create(1, 1, Card(), 5m, null, "  OP-1  ", User, Now);

        Assert.Null(payment.TenderedAmount);
        Assert.Equal(0m, payment.ChangeGiven);
        Assert.Equal("OP-1", payment.ExternalReference);
    }

    [Fact]
    public void Pago_ValidaImporteEntregadoReferenciaYMetodoActivo()
    {
        Assert.Throws<DomainException>(() => Payment.Create(1, 1, Cash(), 0m, null, null, User, Now));
        Assert.Throws<DomainException>(() => Payment.Create(1, 1, Cash(), 5m, 4.99m, null, User, Now));
        Assert.Throws<DomainException>(() => Payment.Create(1, 1, Card(), 5m, 10m, null, User, Now));
        Assert.Throws<DomainException>(() => Payment.Create(1, 1, Card(), 5m, null, new string('x', 121), User, Now));
        var needsRef = PaymentMethod.Create("BIZUM", "Bizum", true, false, 3).WithId(3);
        Assert.Throws<DomainException>(() => Payment.Create(1, 1, needsRef, 5m, null, null, User, Now));
        var inactive = Card();
        inactive.SetActive(false);
        Assert.Throws<DomainException>(() => Payment.Create(1, 1, inactive, 5m, null, null, User, Now));
    }

    [Fact]
    public void Devolucion_ValidaImporteYMotivo()
    {
        var payment = Payment.Create(1, 1, Cash(), 5m, null, null, User, Now).WithId(9);

        var refund = PaymentRefund.Create(payment, 2, 1m, "  Mal estado ", User, Now);

        Assert.Equal(9, refund.PaymentId);
        Assert.Equal("Mal estado", refund.Reason);
        Assert.Equal(payment.PaymentMethodId, refund.PaymentMethodId);
        Assert.Throws<DomainException>(() => PaymentRefund.Create(payment, 2, 0m, "x", User, Now));
        Assert.Throws<DomainException>(() => PaymentRefund.Create(payment, 2, 1m, " ", User, Now));
    }

    // ---------- Caja ----------

    [Fact]
    public void Sesion_Abrir_ValidaElFondo()
    {
        Assert.Equal(CashSessionStatus.Open, CashSession.Open(User, 0m, Now).Status);
        Assert.Throws<DomainException>(() => CashSession.Open(User, -1m, Now));
        Assert.Throws<DomainException>(() => CashSession.Open(User, 1.005m, Now));
    }

    [Fact]
    public void Sesion_Cerrar_GuardaEsperadoContadoYDiferencia()
    {
        var session = CashSession.Open(User, 100m, Now);

        session.Close(User, 97.50m, 100m, "  faltan 2,50 ", Now.AddHours(8));

        Assert.Equal(CashSessionStatus.Closed, session.Status);
        Assert.Equal(-2.50m, session.Difference);
        Assert.Equal("faltan 2,50", session.Notes);
        Assert.Equal(Now.AddHours(8), session.ClosedAt);
        Assert.Throws<DomainException>(() => session.Close(User, 1m, 1m, null, Now));
        Assert.Throws<DomainException>(() => session.RegisterActivity(Now));
    }

    [Fact]
    public void Movimiento_ValidaImporteConceptoYTipo()
    {
        var movement = CashMovement.Create(1, CashMovementType.CashOut, 20m, "  Proveedor ", User, Now);

        Assert.Equal("Proveedor", movement.Concept);
        Assert.Throws<DomainException>(() => CashMovement.Create(1, CashMovementType.CashOut, 0m, "x", User, Now));
        Assert.Throws<DomainException>(() => CashMovement.Create(1, CashMovementType.CashIn, 5m, "", User, Now));
        Assert.Throws<DomainException>(() => CashMovement.Create(1, (CashMovementType)99, 5m, "x", User, Now));
    }

    [Theory]
    [InlineData(" cash ", "CASH")]
    [InlineData("bizum_2", "BIZUM_2")]
    public void MetodoDePago_NormalizaElCodigo(string input, string expected) =>
        Assert.Equal(expected, PaymentMethod.Create(input, "X", false, false, 1).Code);

    [Theory]
    [InlineData("")]
    [InlineData("con espacio")]
    [InlineData("ñandú")]
    public void MetodoDePago_CodigoInvalido_Falla(string code) =>
        Assert.Throws<DomainException>(() => PaymentMethod.Create(code, "X", false, false, 1));
}
