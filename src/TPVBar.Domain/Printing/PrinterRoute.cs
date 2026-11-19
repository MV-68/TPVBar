using TPVBar.Domain.Catalog;

namespace TPVBar.Domain.Printing;

/// <summary>Qué impresora imprime los tickets de cada destino de preparación (cocina o barra).</summary>
public sealed class PrinterRoute
{
    private PrinterRoute()
    {
    }

    public PrinterRoute(PreparationStation station, int printerId)
    {
        Station = station;
        PrinterId = printerId;
    }

    public int Id { get; private set; }

    public PreparationStation Station { get; private set; }

    public int PrinterId { get; private set; }

    public void PointTo(int printerId) => PrinterId = printerId;
}
