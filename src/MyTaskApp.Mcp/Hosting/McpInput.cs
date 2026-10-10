using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MyTaskApp.Domain;

namespace MyTaskApp.Mcp.Hosting;

/// <summary>
/// Os argumentos que chegam do cliente, convertidos com recusa explícita (ADR-059).
/// O cliente não é confiável: texto que não é data não vira "hoje", e enum
/// desconhecido não vira o padrão — vira uma frase dizendo o que se esperava.
/// </summary>
internal static class McpInput
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy"];

    private static readonly string[] TimeFormats = ["HH:mm", "H:mm", "HH:mm:ss", "HHmm"];

    public static DateOnly Date(string value, string field) =>
        OptionalDate(value, field) ?? throw new DomainException($"Informe {field} (AAAA-MM-DD).");

    public static DateOnly? OptionalDate(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new DomainException($"{Capitalize(field)} precisa ser uma data AAAA-MM-DD: \"{Shown(value)}\" não é.");
    }

    public static TimeOnly Time(string value, string field) =>
        OptionalTime(value, field) ?? throw new DomainException($"Informe {field} (HH:mm).");

    /// <summary>"08:31", "8:31", "08:31:00" ou "0831" — o mesmo atalho da tela.</summary>
    public static TimeOnly? OptionalTime(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return TimeOnly.TryParseExact(value.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new DomainException($"{Capitalize(field)} precisa ser uma hora HH:mm: \"{Shown(value)}\" não é.");
    }

    public static TEnum Enum<TEnum>(string value, string field)
        where TEnum : struct, Enum =>
        OptionalEnum<TEnum>(value, field) ?? throw new DomainException($"Informe {field}.");

    public static TEnum? OptionalEnum<TEnum>(string? value, string field)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Só pelo nome: "3" passaria por Enum.TryParse e viraria qualquer coisa.
        var match = System.Enum.GetNames<TEnum>()
            .FirstOrDefault(name => string.Equals(name, value.Trim(), StringComparison.OrdinalIgnoreCase));

        return match is null
            ? throw new DomainException(
                $"{Capitalize(field)} \"{Shown(value)}\" não existe. Valores aceitos: {string.Join(", ", System.Enum.GetNames<TEnum>())}.")
            : System.Enum.Parse<TEnum>(match);
    }

    public static IReadOnlyCollection<TEnum>? Enums<TEnum>(string[]? values, string field)
        where TEnum : struct, Enum =>
        values is null or { Length: 0 }
            ? null
            : values.Select(value => Enum<TEnum>(value, field)).Distinct().ToList();

    public static Guid Id(string value, string field) =>
        Guid.TryParse(value?.Trim(), out var id) && id != Guid.Empty
            ? id
            : throw new DomainException($"{Capitalize(field)} precisa ser um identificador (GUID): \"{Shown(value)}\" não é.");

    public static Guid? OptionalId(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null : Id(value, field);

    /// <summary>Minutos inteiros, entre os limites: a estimativa e o adiamento vêm assim.</summary>
    public static TimeSpan Minutes(int minutes, string field, int min, int max) =>
        minutes >= min && minutes <= max
            ? TimeSpan.FromMinutes(minutes)
            : throw new DomainException($"{Capitalize(field)} fica entre {min} e {max} minutos.");

    /// <summary>O texto do cliente, cortado e sem controle, para caber numa mensagem.</summary>
    public static string Shown(string? value) =>
        value is null ? "(vazio)" : new string(value.Where(c => !char.IsControl(c)).Take(60).ToArray());

    private static string Capitalize(string field) =>
        field.Length == 0 ? field : char.ToUpperInvariant(field[0]) + field[1..];
}

/// <summary>
/// Como as respostas e os argumentos viajam: camelCase, enums pelo nome, e o
/// resolvedor por reflexão para os registros da Application.
/// </summary>
internal static class McpJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();

        return options;
    }
}
