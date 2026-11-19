using System.Xml.Linq;
using TPVBar.Application.Features.Invoicing;

namespace TPVBar.Application.Tests.Fakes;

/// <summary>Pasarela simulada con la AEAT: guarda los envíos y responde con lo programado.</summary>
internal sealed class FakeAeatGateway : IAeatGateway
{
    public AeatMode Mode { get; set; } = AeatMode.PreProduction;

    public string? ConfigurationProblem { get; set; }

    public List<XElement> Sent { get; } = [];

    /// <summary>Si no es <c>null</c>, el siguiente envío falla con esta excepción.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Genera la respuesta a partir del cuerpo enviado (por defecto, todo correcto).</summary>
    public Func<XElement, AeatResponse>? Responder { get; set; }

    /// <summary>Si no es <c>null</c>, el envío espera a que termine esta tarea (para simular una respuesta lenta).</summary>
    public Task? Hold { get; set; }

    public async Task<AeatResponse> SubmitAsync(XElement requestBody, CancellationToken cancellationToken)
    {
        if (FailWith is { } ex)
        {
            FailWith = null;
            throw ex;
        }

        Sent.Add(requestBody);
        if (Hold is { } hold)
        {
            await hold;
        }

        return (Responder ?? AllCorrect)(requestBody);
    }

    public static AeatResponse AllCorrect(XElement body) =>
        Respond(body, _ => ("Correcto", null, null, null), "CSV1234567890", 60);

    /// <summary>Construye una respuesta con un resultado por registro enviado: (estado, código, descripción, estado de duplicado).</summary>
    public static AeatResponse Respond(
        XElement body,
        Func<(string Number, string Operation), (string Status, int? Code, string? Description, string? Duplicate)> status,
        string csv,
        int wait)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(status);
        var lines = new List<AeatLineResult>();
        foreach (var record in body.Elements().Where(e => e.Name.LocalName == "RegistroFactura"))
        {
            var inner = record.Elements().Single();
            var operation = inner.Name.LocalName == "RegistroAlta" ? "Alta" : "Anulacion";
            var number = inner.Elements().First(e => e.Name.LocalName == "IDFactura").Elements().First(e => e.Name.LocalName.StartsWith("NumSerie", StringComparison.Ordinal)).Value;
            var s = status((number, operation));
            lines.Add(new AeatLineResult(number, operation, s.Status, s.Code, s.Description, s.Duplicate));
        }

        var overall = lines.TrueForAll(l => l.RecordStatus == "Correcto") ? "Correcto" : "ParcialmenteCorrecto";
        return new AeatResponse(csv, wait, overall, lines);
    }
}
