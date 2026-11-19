using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace TPVBar.Infrastructure.Billing;

/// <summary>
/// Validación contra los esquemas oficiales de la AEAT (embebidos). Se usa antes de cada envío —un XML que no valida nunca
/// sale— y en las pruebas.
/// </summary>
public static class AeatSchemas
{
    private static readonly Lazy<XmlSchemaSet> Set = new(Load);

    /// <summary>Devuelve los errores de validación (vacío si el documento es válido).</summary>
    public static IReadOnlyList<string> Validate(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var errors = new List<string>();
        new XDocument(element).Validate(Set.Value, (_, e) => errors.Add(e.Message));
        return errors;
    }

    private static XmlSchemaSet Load()
    {
        var set = new XmlSchemaSet { XmlResolver = null };

        // xmldsig se añade primero: así los «import» de los demás esquemas se resuelven sin salir a Internet.
        Add(set, "xmldsig-core-schema.xsd");
        Add(set, "SuministroInformacion.xsd");
        Add(set, "SuministroLR.xsd");
        Add(set, "RespuestaSuministro.xsd");
        set.Compile();
        return set;
    }

    private static void Add(XmlSchemaSet set, string file)
    {
        var assembly = typeof(AeatSchemas).Assembly;
        var name = $"TPVBar.Infrastructure.Billing.Schemas.{file}";
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Falta el esquema embebido {name}.");

        // El esquema xmldsig lleva un DOCTYPE con declaraciones internas: se admite (recurso propio, sin resolución externa).
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        set.Add(null, reader);
    }

    internal static Assembly Assembly => typeof(AeatSchemas).Assembly;
}
