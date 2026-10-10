using System.Globalization;

namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// Como uma coluna é mascarada (ADR-058). Cada método vira um trecho de SQL
/// montado pelo app, só com funções nativas do PostgreSQL: nada é instalado
/// na origem, e o usuário nunca escreve SQL. Os valores vão para o banco: não reordene.
/// </summary>
public enum MaskingMethod
{
    /// <summary>Os 16 primeiros caracteres do md5: iguais continuam iguais, únicos continuam únicos.</summary>
    Hash = 1,

    /// <summary><c>user_&lt;md5&gt;@exemplo.invalid</c>: um e-mail que não entrega em lugar nenhum.</summary>
    FakeEmail = 2,

    /// <summary>Mantém N caracteres do começo e M do fim; o meio vira <c>*</c>.</summary>
    Partial = 3,

    /// <summary>Um nome e sobrenome de uma lista fixa, escolhidos pelo md5 do valor.</summary>
    FakeName = 4,

    FixedText = 5,

    FixedNumber = 6,

    Null = 7,

    /// <summary>A data andando até ±N dias, sempre o mesmo deslocamento para o mesmo valor.</summary>
    DateShift = 8,

    /// <summary>O número variando até ±N%.</summary>
    NumberNoise = 9,
}

/// <summary>O que uma coluna guarda, para saber quais máscaras cabem nela.</summary>
public enum MaskedValueType
{
    Text,

    Number,

    Temporal,

    Other,
}

/// <summary>Um método do catálogo, como a tela o mostra e a validação o julga.</summary>
public sealed record MaskingMethodInfo(
    MaskingMethod Method,
    string Label,
    string Description,
    IReadOnlyList<MaskedValueType> Accepts,
    bool KeepsUniqueness,
    string? ArgumentLabel = null,
    string? DefaultArgument = null)
{
    public bool NeedsArgument => ArgumentLabel is not null;
}

/// <summary>
/// O catálogo fixo de máscaras (ADR-058): o que cada uma faz, em que tipo de
/// coluna cabe e que argumento aceita. O SQL de cada uma é montado na
/// Infrastructure; aqui ficam as regras que valem antes de qualquer servidor.
/// </summary>
public static class MaskingCatalog
{
    public const int MaxFixedTextLength = 200;

    public const int MaxPartialKeep = 50;

    public const int MaxDateShiftDays = 3650;

    private static readonly MaskedValueType[] TextOnly = [MaskedValueType.Text];

    public static IReadOnlyList<MaskingMethodInfo> All { get; } =
    [
        new(MaskingMethod.Hash, "Hash",
            "Troca o valor por 16 caracteres do md5. Iguais continuam iguais, únicos continuam únicos.",
            TextOnly, KeepsUniqueness: true),
        new(MaskingMethod.FakeEmail, "E-mail falso",
            "user_<código>@exemplo.invalid. O mesmo e-mail vira sempre o mesmo falso; únicos continuam únicos.",
            TextOnly, KeepsUniqueness: true),
        new(MaskingMethod.Partial, "Parcial",
            "Mantém o começo e o fim, e troca o meio por *. Ex.: CPF 123.456.789-09 → 1**********09.",
            TextOnly, KeepsUniqueness: false, "Manter início,fim", "0,2"),
        new(MaskingMethod.FakeName, "Nome falso",
            "Um nome e sobrenome de uma lista fixa. O mesmo nome vira sempre o mesmo falso.",
            TextOnly, KeepsUniqueness: false),
        new(MaskingMethod.FixedText, "Texto fixo",
            "O mesmo texto em todas as linhas.",
            TextOnly, KeepsUniqueness: false, "Texto", "(removido)"),
        new(MaskingMethod.FixedNumber, "Número fixo",
            "O mesmo número em todas as linhas.",
            [MaskedValueType.Number], KeepsUniqueness: false, "Número", "0"),
        new(MaskingMethod.Null, "Vazio (NULL)",
            "Apaga o valor. Só em coluna que aceita NULL.",
            [MaskedValueType.Text, MaskedValueType.Number, MaskedValueType.Temporal, MaskedValueType.Other], KeepsUniqueness: false),
        new(MaskingMethod.DateShift, "Data deslocada",
            "Move a data até ±N dias. A mesma data anda sempre o mesmo tanto.",
            [MaskedValueType.Temporal], KeepsUniqueness: false, "± dias", "365"),
        new(MaskingMethod.NumberNoise, "Ruído numérico",
            "Varia o número até ±N%, para os totais continuarem parecidos sem revelar o valor.",
            [MaskedValueType.Number], KeepsUniqueness: false, "± %", "20"),
    ];

    public static MaskingMethodInfo Of(MaskingMethod method) =>
        All.FirstOrDefault(info => info.Method == method)
        ?? throw new DomainException("Máscara desconhecida.");

    /// <summary>
    /// O tipo de uma coluna pelo nome que o PostgreSQL dá (<c>format_type</c>):
    /// <c>character varying(14)</c> é texto, <c>numeric(10,2)</c> é número.
    /// Arrays e tipos próprios ficam em <see cref="MaskedValueType.Other"/>: só aceitam NULL.
    /// </summary>
    public static MaskedValueType ValueTypeOf(string? dataType)
    {
        var type = dataType?.Trim().ToLowerInvariant() ?? string.Empty;

        if (type.EndsWith("[]", StringComparison.Ordinal))
        {
            return MaskedValueType.Other;
        }

        var name = type.Split('(')[0].Trim();

        return name switch
        {
            "text" or "character varying" or "varchar" or "character" or "char" or "bpchar" or "citext" or "name" =>
                MaskedValueType.Text,
            "smallint" or "integer" or "bigint" or "numeric" or "decimal" or "real" or "double precision" or "money" =>
                MaskedValueType.Number,
            "date" or "timestamp without time zone" or "timestamp with time zone" or "timestamp" or "timestamptz" =>
                MaskedValueType.Temporal,
            _ => MaskedValueType.Other,
        };
    }

    public static bool Fits(MaskingMethod method, MaskedValueType type) => Of(method).Accepts.Contains(type);

    /// <summary>
    /// O argumento como fica gravado, ou recusa com a mensagem para o usuário.
    /// Nenhum argumento vira SQL cru: número é lido aqui, texto é citado na hora de montar.
    /// </summary>
    public static string? NormalizeArgument(MaskingMethod method, string? argument, string column)
    {
        var raw = argument?.Trim();
        var info = Of(method);

        if (!info.NeedsArgument)
        {
            return null;
        }

        // Texto fixo vai como foi digitado, espaços inclusive; vazio é válido: a coluna sai em branco.
        if (method == MaskingMethod.FixedText)
        {
            return FixedText(argument ?? string.Empty, column);
        }

        if (string.IsNullOrEmpty(raw))
        {
            throw new DomainException($"Informe {info.ArgumentLabel!.ToLowerInvariant()} da máscara de {column}.");
        }

        return method switch
        {
            MaskingMethod.Partial => PartialArgument(raw, column),
            MaskingMethod.FixedNumber => decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : throw new DomainException($"O número fixo de {column} precisa ser um número, como 0 ou 12.5."),
            MaskingMethod.DateShift => Bounded(raw, 1, MaxDateShiftDays, $"Os dias de {column} vão de 1 a {MaxDateShiftDays}."),
            MaskingMethod.NumberNoise => Bounded(raw, 1, 100, $"O ruído de {column} vai de 1% a 100%."),
            _ => null,
        };
    }

    /// <summary>"0,2" → (0, 2): quantos caracteres ficam no começo e no fim.</summary>
    public static (int Start, int End) PartialKeep(string? argument)
    {
        var parts = (argument ?? "0,0").Split(',');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static string PartialArgument(string raw, string column)
    {
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end)
            || start > MaxPartialKeep
            || end > MaxPartialKeep)
        {
            throw new DomainException(
                $"A máscara parcial de {column} usa \"início,fim\": quantos caracteres manter, de 0 a {MaxPartialKeep}. Ex.: 0,2.");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{start},{end}");
    }

    private static string FixedText(string text, string column)
    {
        if (text.Length > MaxFixedTextLength || text.Any(char.IsControl))
        {
            throw new DomainException($"O texto fixo de {column} tem até {MaxFixedTextLength} caracteres, sem quebra de linha.");
        }

        return text;
    }

    private static string Bounded(string raw, int min, int max, string message) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value.ToString(CultureInfo.InvariantCulture)
            : throw new DomainException(message);
}
