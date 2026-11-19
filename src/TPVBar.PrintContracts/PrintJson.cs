using System.Text.Json;
using System.Text.Json.Serialization;

namespace TPVBar.PrintContracts;

/// <summary>Serialización única del contrato (servidor y agente deben usar las mismas opciones).</summary>
public static class PrintJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
