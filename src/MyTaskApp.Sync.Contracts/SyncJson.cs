using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace MyTaskApp.Sync.Contracts;

/// <summary>As opções de JSON dos dois lados: camelCase, enums como texto.</summary>
public static class SyncJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        Configure(options);
        options.MakeReadOnly();

        return options;
    }

    /// <summary>Aplica as mesmas regras a opções de fora (o ASP.NET do servidor).</summary>
    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    }
}
