using System.Text;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>Uma coluna que parece dado pessoal, com a máscara sugerida. Só sugestão: quem confirma é o usuário.</summary>
public sealed record ColumnSuggestion(
    string Schema,
    string Table,
    string Column,
    string DataType,
    ColumnSensitivity Sensitivity,
    MaskingMethod Method,
    string? Argument,
    string Reason)
{
    public string ColumnKey => $"{Schema}.{Table}.{Column}";
}

/// <summary>
/// Sugere colunas sensíveis só pelo nome, pelo tipo e pelo comentário
/// (ADR-056). Não lê dado nenhum — e não decide nada: uma coluna "name" de
/// uma tabela de produtos não é dado pessoal, e é o usuário quem sabe disso.
/// </summary>
/// <remarks>
/// A máscara sugerida é do catálogo (ADR-058) e cabe no tipo da coluna: um
/// CPF guardado como número não recebe a máscara parcial de texto.
/// </remarks>
public static class SensitiveColumnClassifier
{
    private static readonly Pattern[] Patterns =
    [
        // Alta: identifica a pessoa sozinho, ou dá acesso a algo.
        new(ColumnSensitivity.High, ["cpf"], [], "CPF", MaskingMethod.Partial, "0,2"),
        new(ColumnSensitivity.High, ["cnpj"], [], "CNPJ", MaskingMethod.Partial, "0,2"),
        new(ColumnSensitivity.High, ["rg"], [], "RG", MaskingMethod.Partial, "0,2"),
        new(ColumnSensitivity.High, ["mail"], ["email"], "e-mail", MaskingMethod.FakeEmail, ExpectsText: true),
        new(ColumnSensitivity.High, ["fone", "phone", "tel"], ["telefone", "celular", "phone", "whatsapp"], "telefone",
            MaskingMethod.Partial, "2,2"),
        new(ColumnSensitivity.High, ["pwd", "hash"], ["password", "senha", "passwd"], "senha", MaskingMethod.Hash, ExpectsText: true),
        new(ColumnSensitivity.High, [], ["token", "secret", "apikey"], "segredo", MaskingMethod.Hash, ExpectsText: true),
        new(ColumnSensitivity.High, ["iban", "pix"], ["cartao", "creditcard", "cardnumber", "numerocartao", "contabancaria", "accountnumber"],
            "dado financeiro", MaskingMethod.Partial, "0,4"),
        new(ColumnSensitivity.High, ["passaporte", "passport", "cnh", "ssn", "pis", "nis"], [], "documento", MaskingMethod.Partial, "0,2"),

        // Média: identifica junto com outra coisa.
        new(ColumnSensitivity.Medium, ["nome", "name", "sobrenome", "surname"], ["firstname", "lastname", "fullname", "nomecompleto"],
            "nome", MaskingMethod.FakeName, ExpectsText: true),
        new(ColumnSensitivity.Medium, ["endereco", "address", "logradouro", "rua", "street", "bairro"], ["endereco", "logradouro"],
            "endereço", MaskingMethod.FixedText, "Rua Exemplo, 100", ExpectsText: true),
        new(ColumnSensitivity.Medium, ["cep", "zip", "zipcode", "postal"], [], "CEP", MaskingMethod.Partial, "5,0"),
        new(ColumnSensitivity.Medium, ["dob"], ["nascimento", "birth", "aniversario"], "data de nascimento", MaskingMethod.DateShift, "365"),
        new(ColumnSensitivity.Medium, ["ip"], ["ipaddress", "enderecoip"], "endereço IP", MaskingMethod.Null),
        new(ColumnSensitivity.Medium, [], ["salario", "salary", "remuneracao"], "salário", MaskingMethod.NumberNoise, "20"),

        // Baixa: raramente sozinha, mas costuma andar junto.
        new(ColumnSensitivity.Low, ["cidade", "city", "municipio"], [], "cidade", MaskingMethod.FixedText, "Cidade Exemplo", ExpectsText: true),
        new(ColumnSensitivity.Low, ["genero", "gender", "sexo"], [], "gênero", MaskingMethod.Null, ExpectsText: true),
        new(ColumnSensitivity.Low, ["obs", "observacao", "notes", "nota", "comentario", "comment"], ["observacao", "comentario"],
            "texto livre", MaskingMethod.FixedText, "(removido)", ExpectsText: true),
        new(ColumnSensitivity.Low, ["lat", "latitude", "lng", "lon", "longitude"], [], "localização", MaskingMethod.NumberNoise, "10"),
    ];

    private static readonly string[] SensitiveComments = ["pii", "lgpd", "gdpr", "sensível", "sensivel", "sensitive", "pessoal", "personal"];

    public static IReadOnlyList<ColumnSuggestion> Suggest(IEnumerable<ColumnInfo> columns) =>
        columns.Select(Classify)
            .OfType<ColumnSuggestion>()
            .OrderByDescending(suggestion => suggestion.Sensitivity)
            .ThenBy(suggestion => suggestion.ColumnKey, StringComparer.Ordinal)
            .ToList();

    public static ColumnSuggestion? Classify(ColumnInfo column)
    {
        var tokens = Tokens(column.Column);
        var compact = string.Concat(tokens);
        var type = MaskingCatalog.ValueTypeOf(column.DataType);
        var marked = column.Comment is { } comment
            && SensitiveComments.Any(word => comment.Contains(word, StringComparison.OrdinalIgnoreCase));

        foreach (var pattern in Patterns)
        {
            if (!pattern.Matches(tokens, compact))
            {
                continue;
            }

            // "nome_id" inteiro não é um nome: o que espera texto perde um grau fora de texto.
            var sensitivity = !pattern.ExpectsText || type == MaskedValueType.Text
                ? pattern.Sensitivity
                : Lower(pattern.Sensitivity);

            if (marked)
            {
                sensitivity = ColumnSensitivity.High;
            }

            if (sensitivity is null)
            {
                return null;
            }

            var reason = marked
                ? $"Parece {pattern.Reason}; o comentário da coluna a marca como dado pessoal."
                : $"Parece {pattern.Reason}.";
            var (method, argument) = Fit(pattern.Method, pattern.Argument, type);

            return new ColumnSuggestion(
                column.Schema, column.Table, column.Column, column.DataType, sensitivity.Value, method, argument, reason);
        }

        if (!marked)
        {
            return null;
        }

        var (fallback, fallbackArgument) = Fit(MaskingMethod.Hash, null, type);
        return new ColumnSuggestion(column.Schema, column.Table, column.Column, column.DataType, ColumnSensitivity.High,
            fallback, fallbackArgument, "O comentário da coluna a marca como dado pessoal.");
    }

    /// <summary>
    /// A máscara do padrão, se couber no tipo; senão, a mais segura para o tipo:
    /// hash no texto, zero no número, data deslocada na data, vazio no resto.
    /// </summary>
    internal static (MaskingMethod Method, string? Argument) Fit(MaskingMethod method, string? argument, MaskedValueType type)
    {
        if (MaskingCatalog.Fits(method, type))
        {
            return (method, argument);
        }

        return type switch
        {
            MaskedValueType.Text => (MaskingMethod.Hash, null),
            MaskedValueType.Number => (MaskingMethod.FixedNumber, "0"),
            MaskedValueType.Temporal => (MaskingMethod.DateShift, "365"),
            _ => (MaskingMethod.Null, null),
        };
    }

    /// <summary>"dataNascimento", "data_nascimento", "DATA-NASCIMENTO" → [data, nascimento].</summary>
    internal static IReadOnlyList<string> Tokens(string name)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(RemoveAccents(current.ToString()).ToLowerInvariant());
                current.Clear();
            }
        }

        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];

            if (!char.IsLetterOrDigit(character))
            {
                Flush();
                continue;
            }

            if (char.IsUpper(character) && index > 0 && char.IsLower(name[index - 1]))
            {
                Flush();
            }

            current.Append(character);
        }

        Flush();
        return tokens;
    }

    private static string RemoveAccents(string text) =>
        new(text.Normalize(NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());

    private static ColumnSensitivity? Lower(ColumnSensitivity sensitivity) => sensitivity switch
    {
        ColumnSensitivity.High => ColumnSensitivity.Medium,
        ColumnSensitivity.Medium => ColumnSensitivity.Low,
        _ => null,
    };

    private sealed record Pattern(
        ColumnSensitivity Sensitivity,
        string[] WholeTokens,
        string[] Fragments,
        string Reason,
        MaskingMethod Method,
        string? Argument = null,
        bool ExpectsText = false)
    {
        public bool Matches(IReadOnlyList<string> tokens, string compact) =>
            tokens.Any(token => WholeTokens.Contains(token, StringComparer.Ordinal))
            || Fragments.Any(fragment => compact.Contains(fragment, StringComparison.Ordinal));
    }
}
