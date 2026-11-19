using TPVBar.Domain.Common;

namespace TPVBar.Domain.Tests;

public class MoneyTests
{
    [Theory]
    [InlineData(1.005, 1.01)]
    [InlineData(1.004, 1.00)]
    [InlineData(2.675, 2.68)]
    [InlineData(0.125, 0.13)]
    public void Round_RedondeaLaMitadHaciaArribaALosDosDecimales(double input, double esperado) =>
        Assert.Equal((decimal)esperado, Money.Round((decimal)input));

    [Theory]
    [InlineData(5.40, 21, 4.46, 0.94)]
    [InlineData(7.00, 10, 6.36, 0.64)]
    [InlineData(1.10, 10, 1.00, 0.10)]
    [InlineData(1.21, 21, 1.00, 0.21)]
    [InlineData(0.00, 21, 0.00, 0.00)]
    public void SplitGross_SeparaBaseEImpuesto(double gross, double rate, double baseEsperada, double impuestoEsperado)
    {
        var (net, tax) = Money.SplitGross((decimal)gross, (decimal)rate);

        Assert.Equal((decimal)baseEsperada, net);
        Assert.Equal((decimal)impuestoEsperado, tax);
    }

    [Fact]
    public void SplitGross_BaseMasImpuestoSiempreSumaElImporte()
    {
        foreach (var rate in new[] { 0m, 4m, 10m, 21m, 7.5m })
        {
            for (var cents = 0; cents <= 20000; cents += 7)
            {
                var gross = cents / 100m;
                var (net, tax) = Money.SplitGross(gross, rate);
                Assert.Equal(gross, net + tax);
                Assert.True(tax >= 0 && net >= 0);
            }
        }
    }
}
