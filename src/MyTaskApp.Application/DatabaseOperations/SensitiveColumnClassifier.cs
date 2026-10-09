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
    MaskingKind Kind,
    string Expression,
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
/// As máscaras sugeridas são do PostgreSQL Anonymizer 2.x
/// (<c>anon.partial</c>, <c>anon.partial_email</c>, <c>anon.dummy_*</c>,
/// <c>anon.random_*</c>). Confira os nomes na versão instalada antes de
/// confirmar; o script gerado falha no <c>SECURITY LABEL</c> se uma função não existir.
/// </remarks>
public static class SensitiveColumnClassifier
{
    private static readonly Pattern[] Patterns =
    [
        // Alta: identifica a pessoa sozinho, ou dá acesso a algo.
        new(ColumnSensitivity.High, ["cpf"], [], "CPF", col => $"anon.partial({col},0,$$*********$$,2)"),
        new(ColumnSensitivity.High, ["cnpj"], [], "CNPJ", col => $"anon.partial({col},0,$$************$$,2)"),
        new(ColumnSensitivity.High, ["rg"], [], "RG", col => $"anon.partial({col},0,$$*******$$,2)"),
        new(ColumnSensitivity.High, ["mail"], ["email"], "e-mail", col => $"anon.partial_email({col})", ExpectsText: true),
        new(ColumnSensitivity.High, ["fone", "phone", "tel"], ["telefone", "celular", "phone", "whatsapp"], "telefone",
            col => $"anon.partial({col},2,$$*******$$,2)"),
        new(ColumnSensitivity.High, ["pwd", "hash"], ["password", "senha", "passwd"], "senha", _ => "anon.random_string(16)", ExpectsText: true),
        new(ColumnSensitivity.High, [], ["token", "secret", "apikey"], "segredo", _ => "anon.random_string(16)", ExpectsText: true),
        new(ColumnSensitivity.High, ["iban", "pix"], ["cartao", "creditcard", "cardnumber", "numerocartao", "contabancaria", "accountnumber"],
            "dado financeiro", col => $"anon.partial({col},0,$$************$$,4)"),
        new(ColumnSensitivity.High, ["passaporte", "passport", "cnh", "ssn", "pis", "nis"], [], "documento",
            col => $"anon.partial({col},0,$$******$$,2)"),

        // Média: identifica junto com outra coisa.
        new(ColumnSensitivity.Medium, ["nome", "name", "sobrenome", "surname"], ["firstname", "lastname", "fullname", "nomecompleto"],
            "nome", _ => "anon.dummy_name()", ExpectsText: true),
        new(ColumnSensitivity.Medium, ["endereco", "address", "logradouro", "rua", "street", "bairro"], ["endereco", "logradouro"],
            "endereço", _ => "anon.dummy_street_name()", ExpectsText: true),
        new(ColumnSensitivity.Medium, ["cep", "zip", "zipcode", "postal"], [], "CEP", _ => "anon.dummy_zip_code()"),
        new(ColumnSensitivity.Medium, ["dob"], ["nascimento", "birth", "aniversario"], "data de nascimento", _ => "anon.random_date()"),
        new(ColumnSensitivity.Medium, ["ip"], ["ipaddress", "enderecoip"], "endereço IP", _ => "NULL", MaskingKind.Value),
        new(ColumnSensitivity.Medium, [], ["salario", "salary", "remuneracao"], "salário", col => $"anon.noise({col}, 0.2)"),

        // Baixa: raramente sozinha, mas costuma andar junto.
        new(ColumnSensitivity.Low, ["cidade", "city", "municipio"], [], "cidade", _ => "anon.dummy_city_name()", ExpectsText: true),
        new(ColumnSensitivity.Low, ["genero", "gender", "sexo"], [], "gênero", _ => "NULL", MaskingKind.Value, ExpectsText: true),
        new(ColumnSensitivity.Low, ["obs", "observacao", "notes", "nota", "comentario", "comment"], ["observacao", "comentario"],
            "texto livre", _ => "anon.lorem_ipsum(words := 5)", ExpectsText: true),
        new(ColumnSensitivity.Low, ["lat", "latitude", "lng", "lon", "longitude"], [], "localização", col => $"anon.noise({col}, 0.1)"),
    ];

    private static readonly string[] TextTypes = ["text", "character", "varchar", "char", "citext", "bpchar", "json"];

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
        var marked = column.Comment is { } comment
            && SensitiveComments.Any(word => comment.Contains(word, StringComparison.OrdinalIgnoreCase));

        foreach (var pattern in Patterns)
        {
            if (!pattern.Matches(tokens, compact))
            {
                continue;
            }

            // "nome_id" inteiro não é um nome: o que espera texto perde um grau fora de texto.
            var sensitivity = !pattern.ExpectsText || IsText(column.DataType)
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

            return new ColumnSuggestion(
                column.Schema,
                column.Table,
                column.Column,
                column.DataType,
                sensitivity.Value,
                pattern.Kind,
                pattern.Expression(Identifier(column.Column)),
                reason);
        }

        return marked
            ? new ColumnSuggestion(column.Schema, column.Table, column.Column, column.DataType, ColumnSensitivity.High,
                MaskingKind.Function, "anon.random_string(12)", "O comentário da coluna a marca como dado pessoal.")
            : null;
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

    private static bool IsText(string dataType) =>
        TextTypes.Any(type => dataType.Contains(type, StringComparison.OrdinalIgnoreCase));

    private static ColumnSensitivity? Lower(ColumnSensitivity sensitivity) => sensitivity switch
    {
        ColumnSensitivity.High => ColumnSensitivity.Medium,
        ColumnSensitivity.Medium => ColumnSensitivity.Low,
        _ => null,
    };

    /// <summary>A coluna dentro da expressão sugerida: entre aspas se precisar.</summary>
    private static string Identifier(string column) =>
        column.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_') && !char.IsAsciiDigit(column[0])
            ? column
            : MaskingScriptBuilder.QuoteIdentifier(column);

    private sealed record Pattern(
        ColumnSensitivity Sensitivity,
        string[] WholeTokens,
        string[] Fragments,
        string Reason,
        Func<string, string> Expression,
        MaskingKind Kind = MaskingKind.Function,
        bool ExpectsText = false)
    {
        public bool Matches(IReadOnlyList<string> tokens, string compact) =>
            tokens.Any(token => WholeTokens.Contains(token, StringComparer.Ordinal))
            || Fragments.Any(fragment => compact.Contains(fragment, StringComparison.Ordinal));
    }
}
