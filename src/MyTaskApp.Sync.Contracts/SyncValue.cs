using System.Globalization;
using System.Text.Json;

namespace MyTaskApp.Sync.Contracts;

/// <summary>
/// Escreve e lê os valores do fio no formato do contrato. Os dois lados usam
/// estas funções, e é isso que faz "o mesmo instante" virar o mesmo texto —
/// sem o que a comparação de campos acusaria conflito entre valores iguais.
/// </summary>
public static class SyncValue
{
    public const string DateFormat = "yyyy-MM-dd";

    public const string TimeFormat = "HH:mm:ss.FFFFFFF";

    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static JsonElement Null { get; } = Parse("null");

    /// <summary>O mesmo escape das opções do contrato: acento fica acento.</summary>
    private static readonly JsonWriterOptions Writer = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonElement String(string? value) => value is null ? Null : JsonSerializer.SerializeToElement(value, SyncJson.Options);

    public static JsonElement Integer(long? value) => value is null ? Null : JsonSerializer.SerializeToElement(value.Value);

    public static JsonElement Boolean(bool value) => JsonSerializer.SerializeToElement(value);

    public static JsonElement Instant(DateTimeOffset? value) =>
        value is null ? Null : String(value.Value.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture));

    public static JsonElement Date(DateOnly? value) =>
        value is null ? Null : String(value.Value.ToString(DateFormat, CultureInfo.InvariantCulture));

    public static JsonElement Time(TimeOnly? value) =>
        value is null ? Null : String(value.Value.ToString(TimeFormat, CultureInfo.InvariantCulture));

    /// <summary>Duração em milissegundos inteiros: lembretes e estimativas são minutos.</summary>
    public static JsonElement Duration(TimeSpan? value) =>
        value is null ? Null : Integer((long)value.Value.TotalMilliseconds);

    public static JsonElement Guid(Guid? value) => value is null ? Null : String(value.Value.ToString("D"));

    public static JsonElement Object(params (string Name, JsonElement Value)[] members)
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, Writer))
        {
            writer.WriteStartObject();

            foreach (var (name, value) in members)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Parse(buffer.ToArray());
    }

    public static JsonElement List(IEnumerable<JsonElement> items)
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, Writer))
        {
            writer.WriteStartArray();

            foreach (var item in items)
            {
                item.WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        return Parse(buffer.ToArray());
    }

    public static bool IsNull(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    public static string? ReadString(JsonElement value) => IsNull(value) ? null : value.GetString();

    public static long? ReadInteger(JsonElement value) => IsNull(value) ? null : value.GetInt64();

    public static bool ReadBoolean(JsonElement value) => value.GetBoolean();

    public static DateTimeOffset? ReadInstant(JsonElement value) =>
        IsNull(value)
            ? null
            : DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static DateOnly? ReadDate(JsonElement value) =>
        IsNull(value) ? null : DateOnly.ParseExact(value.GetString()!, DateFormat, CultureInfo.InvariantCulture);

    public static TimeOnly? ReadTime(JsonElement value) =>
        IsNull(value) ? null : TimeOnly.ParseExact(value.GetString()!, TimeFormat, CultureInfo.InvariantCulture);

    public static TimeSpan? ReadDuration(JsonElement value) =>
        IsNull(value) ? null : TimeSpan.FromMilliseconds(value.GetInt64());

    public static Guid? ReadGuid(JsonElement value) => IsNull(value) ? null : System.Guid.Parse(value.GetString()!);

    /// <summary>O membro de um objeto, ou <see cref="Null"/> se não veio.</summary>
    public static JsonElement Member(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) ? member : Null;

    /// <summary>Igualdade estrutural: a ordem das propriedades de um objeto não importa.</summary>
    public static bool AreEqual(JsonElement left, JsonElement right) =>
        (IsNull(left) && IsNull(right)) || JsonElement.DeepEquals(left, right);

    /// <summary>
    /// Algum texto (ou nome de membro) com U+0000, em qualquer profundidade. O
    /// PostgreSQL não grava esse caractere em <c>text</c> nem em <c>jsonb</c>.
    /// </summary>
    public static bool ContainsNul(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Contains('\0'),
        JsonValueKind.Object => value.EnumerateObject().Any(member => member.Name.Contains('\0') || ContainsNul(member.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsNul),
        _ => false,
    };

    /// <summary>Uma cópia que sobrevive ao <see cref="JsonDocument"/> de onde o valor veio.</summary>
    public static JsonElement Detach(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? Null : value.Clone();

    public static JsonElement Parse(string json) => Parse(System.Text.Encoding.UTF8.GetBytes(json));

    private static JsonElement Parse(byte[] utf8)
    {
        using var document = JsonDocument.Parse(utf8);
        return document.RootElement.Clone();
    }
}
