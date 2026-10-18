using System.Globalization;
using System.Text.Json;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Sync.Contracts;

/// <summary>Um campo recusado pela validação, com a frase de quem vai ler o log ou a tela.</summary>
public sealed record SyncFieldError(string Field, string Message);

/// <summary>
/// Confere os valores de uma operação contra o contrato. O servidor roda
/// antes de gravar; o desktop roda antes de aplicar o que veio de fora.
/// </summary>
/// <remarks>
/// <see cref="Validate"/> não olha campo desconhecido: o desktop o roda no
/// pull e ignora o que não conhece. Quem recusa é o servidor, no push, com
/// <see cref="FindUnknown"/> (ADR-072) — guardar o que não está no contrato
/// deixaria a entidade crescer sem limite. Nenhum campo, conhecido ou não,
/// passa com U+0000: o banco do servidor não grava o caractere. O servidor
/// grava o que sai de <see cref="Normalize"/>, não o texto que veio.
/// </remarks>
public static class SyncFieldValidator
{
    /// <summary>
    /// O instante com fuso explícito: <c>Z</c> ou <c>±hh:mm</c>, fração de 0 a 7
    /// dígitos (o formato <c>O</c> inclusive). Sem fuso, cada lado leria um horário.
    /// </summary>
    private static readonly string[] InstantFormats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];

    /// <summary>
    /// Os caminhos fora do contrato, no formato dos erros de <see cref="Validate"/>:
    /// o campo (<c>isAdmin</c>), o membro de objeto (<c>reminder.x</c>) e o
    /// membro do item de lista (<c>parameters[2].x</c>).
    /// </summary>
    public static IReadOnlyList<string> FindUnknown(
        SyncEntityDefinition definition,
        IReadOnlyDictionary<string, JsonElement> fields)
    {
        var unknown = new List<string>();

        foreach (var (name, value) in fields)
        {
            if (definition.FindField(name) is { } field)
            {
                FindUnknown(field, value, name, unknown);
            }
            else
            {
                unknown.Add(name);
            }
        }

        // Sem o NUL, como no Validate: a frase da recusa também é gravada no servidor.
        return [.. unknown.Select(path => path.Replace("\0", "\\0", StringComparison.Ordinal))];
    }

    private static void FindUnknown(SyncFieldDefinition field, JsonElement value, string path, List<string> unknown)
    {
        if (field.Kind == SyncFieldKind.Object && value.ValueKind == JsonValueKind.Object)
        {
            foreach (var member in value.EnumerateObject())
            {
                var memberPath = $"{path}.{member.Name}";

                if (field.Members.FirstOrDefault(known => known.Name == member.Name) is { } memberField)
                {
                    FindUnknown(memberField, member.Value, memberPath, unknown);
                }
                else
                {
                    unknown.Add(memberPath);
                }
            }
        }
        else if (field is { Kind: SyncFieldKind.List, Item: { } item } && value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;

            foreach (var element in value.EnumerateArray())
            {
                FindUnknown(item, element, $"{path}[{index++}]", unknown);
            }
        }
    }

    public static IReadOnlyList<SyncFieldError> Validate(
        SyncEntityDefinition definition,
        IReadOnlyDictionary<string, JsonElement> fields,
        bool isCreate)
    {
        var errors = new List<SyncFieldError>();

        foreach (var field in definition.Fields)
        {
            if (fields.TryGetValue(field.Name, out var value))
            {
                ValidateValue(field, value, field.Name, errors);
            }
            else if (isCreate && field.IsRequired)
            {
                errors.Add(new SyncFieldError(field.Name, "Campo obrigatório ausente na criação."));
            }
        }

        // O nome vai sem o NUL: a frase da recusa também é gravada no servidor.
        foreach (var (name, value) in fields)
        {
            if (name.Contains('\0') || SyncValue.ContainsNul(value))
            {
                errors.Add(new SyncFieldError(name.Replace("\0", "\\0", StringComparison.Ordinal), "Texto com caractere nulo (U+0000)."));
            }
        }

        return errors;
    }

    private static void ValidateValue(SyncFieldDefinition field, JsonElement value, string path, List<SyncFieldError> errors)
    {
        if (SyncValue.IsNull(value))
        {
            if (field.IsRequired)
            {
                errors.Add(new SyncFieldError(path, "Campo obrigatório não pode ser nulo."));
            }

            return;
        }

        var problem = field.Kind switch
        {
            SyncFieldKind.String => CheckString(field, value),
            SyncFieldKind.Integer => CheckInteger(field, value),
            SyncFieldKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "Esperado verdadeiro ou falso.",
            SyncFieldKind.Instant => CheckText(value, text => TryParseInstant(text, out _), "instante ISO 8601 com fuso (Z ou ±hh:mm)"),
            SyncFieldKind.Date => CheckText(value, text => DateOnly.TryParseExact(text, SyncValue.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "data aaaa-MM-dd"),
            SyncFieldKind.Time => CheckText(value, text => TimeOnly.TryParseExact(text, SyncValue.TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "hora HH:mm:ss"),
            SyncFieldKind.Duration => CheckInteger(field, value),
            SyncFieldKind.Guid => CheckText(value, text => Guid.TryParse(text, out _), "GUID"),
            SyncFieldKind.Object => CheckObject(field, value, path, errors),
            SyncFieldKind.List => CheckList(field, value, path, errors),
            _ => "Tipo de campo desconhecido.",
        };

        if (problem is not null)
        {
            errors.Add(new SyncFieldError(path, problem));
        }
    }

    private static string? CheckString(SyncFieldDefinition field, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return "Esperado texto.";
        }

        return field.MaxLength is { } max && value.GetString()!.Length > max
            ? $"Texto acima de {max} caracteres."
            : null;
    }

    private static string? CheckInteger(SyncFieldDefinition field, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            return "Esperado número inteiro.";
        }

        if (field.Min is { } min && number < min)
        {
            return $"Valor abaixo de {min}.";
        }

        return field.Max is { } max && number > max ? $"Valor acima de {max}." : null;
    }

    private static string? CheckText(JsonElement value, Func<string, bool> parses, string expected) =>
        value.ValueKind == JsonValueKind.String && parses(value.GetString()!) ? null : $"Esperado {expected}.";

    private static string? CheckObject(SyncFieldDefinition field, JsonElement value, string path, List<SyncFieldError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return "Esperado objeto.";
        }

        foreach (var member in field.Members)
        {
            var memberPath = $"{path}.{member.Name}";

            if (value.TryGetProperty(member.Name, out var memberValue))
            {
                ValidateValue(member, memberValue, memberPath, errors);
            }
            else if (member.IsRequired)
            {
                errors.Add(new SyncFieldError(memberPath, "Campo obrigatório ausente."));
            }
        }

        return null;
    }

    private static string? CheckList(SyncFieldDefinition field, JsonElement value, string path, List<SyncFieldError> errors)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return "Esperada lista.";
        }

        if (field.MaxItems is { } max && value.GetArrayLength() > max)
        {
            return $"Lista acima de {max} itens.";
        }

        if (field.Item is { } item)
        {
            var index = 0;

            foreach (var element in value.EnumerateArray())
            {
                ValidateValue(item, element, $"{path}[{index++}]", errors);
            }
        }

        return null;
    }

    /// <summary>
    /// Os campos de uma operação já validada, com todo <see cref="SyncFieldKind.Guid"/>
    /// no formato <c>D</c> minúsculo e todo <see cref="SyncFieldKind.Instant"/> em
    /// UTC, em qualquer profundidade — o que <see cref="SyncValue"/> escreve. A
    /// chave de unicidade e a mescla comparam o texto: sem isso, o mesmo GUID em
    /// <c>N</c> ou <c>B</c> passaria como outro valor (SEC-014).
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> Normalize(
        SyncEntityDefinition definition,
        IReadOnlyDictionary<string, JsonElement> fields)
    {
        var normalized = new Dictionary<string, JsonElement>(fields.Count, StringComparer.Ordinal);

        foreach (var (name, value) in fields)
        {
            normalized[name] = definition.FindField(name) is { } field ? Normalize(field, value) : value;
        }

        return normalized;
    }

    private static JsonElement Normalize(SyncFieldDefinition field, JsonElement value)
    {
        if (SyncValue.IsNull(value))
        {
            return value;
        }

        switch (field.Kind)
        {
            case SyncFieldKind.Guid when value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var guid):
                return SyncValue.Guid(guid);

            case SyncFieldKind.Instant when value.ValueKind == JsonValueKind.String && TryParseInstant(value.GetString()!, out var instant):
                return SyncValue.Instant(instant);

            case SyncFieldKind.Object when value.ValueKind == JsonValueKind.Object:
                var members = value.EnumerateObject()
                    .Select(member => (member.Name, Original: member.Value, Value: field.Members.FirstOrDefault(known => known.Name == member.Name) is { } memberField
                        ? Normalize(memberField, member.Value)
                        : member.Value))
                    .ToList();

                return members.Any(member => !SameText(member.Original, member.Value))
                    ? SyncValue.Object([.. members.Select(member => (member.Name, member.Value))])
                    : value;

            case SyncFieldKind.List when field.Item is { } item && value.ValueKind == JsonValueKind.Array:
                var items = value.EnumerateArray().Select(element => (Original: element, Value: Normalize(item, element))).ToList();

                return items.Any(element => !SameText(element.Original, element.Value))
                    ? SyncValue.List(items.Select(element => element.Value))
                    : value;

            default:
                return value;
        }
    }

    /// <summary>Nada mudou: o objeto ou a lista volta como veio, sem ser reescrito.</summary>
    private static bool SameText(JsonElement original, JsonElement normalized) =>
        original.GetRawText() == normalized.GetRawText();

    private static bool TryParseInstant(string text, out DateTimeOffset instant) =>
        DateTimeOffset.TryParseExact(
            text,
            InstantFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out instant);
}

/// <summary>
/// A chave de unicidade de uma entidade no workspace: os campos de
/// <see cref="SyncEntityDefinition.UniqueKeyFields"/> em minúsculas,
/// separados por <c>/</c>.
/// </summary>
/// <remarks>
/// O <c>ToLowerInvariant</c> dobra também os acentos, o que é mais estrito que
/// o <c>NOCASE</c> do SQLite (só ASCII). Assim o servidor nunca aceita um par
/// que o banco local recusaria.
/// </remarks>
public static class SyncUniqueKey
{
    /// <summary>A chave, ou <c>null</c> sem unicidade ou com algum campo ausente.</summary>
    public static string? Build(SyncEntityDefinition definition, Func<string, JsonElement> fieldValue)
    {
        if (definition.UniqueKeyFields.Count == 0)
        {
            return null;
        }

        var parts = new List<string>(definition.UniqueKeyFields.Count);

        foreach (var name in definition.UniqueKeyFields)
        {
            var value = fieldValue(name);

            if (SyncValue.IsNull(value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            parts.Add(value.GetString()!.Trim().ToLowerInvariant());
        }

        return string.Join('/', parts);
    }
}
