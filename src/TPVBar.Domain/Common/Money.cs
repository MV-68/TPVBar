namespace TPVBar.Domain.Common;

/// <summary>Cálculos monetarios. Todo importe es <see cref="decimal"/> con redondeo a 2 decimales.</summary>
public static class Money
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Separa un importe con IVA incluido en base imponible e impuesto. La base se redondea y el
    /// impuesto es la diferencia, de modo que base + impuesto es siempre exactamente el importe.
    /// </summary>
    public static (decimal Base, decimal Tax) SplitGross(decimal gross, decimal taxRatePercent)
    {
        var net = Round(gross / (1 + (taxRatePercent / 100m)));
        return (net, gross - net);
    }
}
