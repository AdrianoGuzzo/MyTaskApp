using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace MyTaskApp.Remote.Contracts;

/// <summary>
/// As mesmas regras de JSON do sync (<c>SyncJson</c>): camelCase, enum como
/// texto e acentos legíveis. O controlador assina estes bytes, então os dois
/// lados precisam escrever e ler igual.
/// </summary>
public static class RemoteJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        Configure(options);
        options.MakeReadOnly();

        return options;
    }

    /// <summary>Aplica as mesmas regras a opções de fora (o ASP.NET do servidor).</summary>
    /// <remarks>
    /// Mais estrito que o sync, porque aqui o JSON vira execução: campo
    /// obrigatório ausente, <c>null</c> onde o tipo não aceita e campo
    /// desconhecido são recusados. Um agente mais velho não ignora em silêncio
    /// um campo novo que ele não sabe honrar.
    /// </remarks>
    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    }
}
