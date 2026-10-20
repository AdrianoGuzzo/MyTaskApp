namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// A condição de um filtro de linhas sem máscara (ADR-079): o único SQL do
/// usuário que o app roda — e só como expressão, dentro de parênteses, numa
/// sessão só-leitura da origem.
/// </summary>
/// <remarks>
/// <para>
/// A leitura aqui é léxica, não um parser: garante que a condição não sai dos
/// parênteses em volta dela. Sem <c>;</c>, sem comentário, sem dollar-quoting,
/// sem barra invertida (que mudaria onde um texto termina), com aspas e
/// parênteses fechados. Se a expressão faz sentido, quem diz é o PostgreSQL, na
/// conferência antes da cópia.
/// </para>
/// </remarks>
public static class RowFilterCondition
{
    public const int MaxLength = 2000;

    /// <summary>A condição aparada e conferida; <paramref name="table"/> só entra nas mensagens.</summary>
    public static string Normalize(string? condition, string table)
    {
        var normalized = condition?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            throw new DomainException($"Informe a condição das linhas sem máscara de {table}.");
        }

        if (normalized.Length > MaxLength)
        {
            throw new DomainException($"A condição de {table} passa de {MaxLength} caracteres.");
        }

        // Quem cola "WHERE id = 1" quis dizer "id = 1".
        if (normalized.Length > 5 && normalized.StartsWith("where", StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(normalized[5]))
        {
            normalized = normalized[5..].TrimStart();
        }

        if (Problem(normalized) is { } problem)
        {
            throw new DomainException($"Condição inválida em {table}: {problem}");
        }

        return normalized;
    }

    private static string? Problem(string condition)
    {
        var depth = 0;
        var index = 0;

        while (index < condition.Length)
        {
            var current = condition[index];
            var next = index + 1 < condition.Length ? condition[index + 1] : '\0';

            switch (current)
            {
                case '\'' or '"':
                    var end = Closing(condition, index);

                    if (end < 0)
                    {
                        return current == '\'' ? "um texto entre aspas simples não foi fechado." : "um nome entre aspas duplas não foi fechado.";
                    }

                    if (current == '\'' && condition.AsSpan(index, end - index).Contains('\\'))
                    {
                        return "barra invertida não é aceita dentro de texto.";
                    }

                    index = end + 1;
                    continue;
                case ';':
                    return "o ; não é aceito: a condição é uma expressão só.";
                case '-' when next == '-':
                case '/' when next == '*':
                    return "comentário não é aceito.";
                case '$':
                    return "o $ não é aceito (parâmetro ou dollar-quoting).";
                case '\\':
                    return "barra invertida não é aceita.";
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;

                    if (depth < 0)
                    {
                        return "há um ) sem o ( correspondente.";
                    }

                    break;
                default:
                    if (char.IsControl(current) && current is not ('\r' or '\n' or '\t'))
                    {
                        return "há um caractere de controle.";
                    }

                    break;
            }

            index++;
        }

        return depth == 0 ? null : "há um ( sem o ) correspondente.";
    }

    /// <summary>Onde fecha o texto ou o nome citado que abre em <paramref name="start"/>; aspa dobrada é aspa dentro dele.</summary>
    private static int Closing(string condition, int start)
    {
        var quote = condition[start];

        for (var index = start + 1; index < condition.Length; index++)
        {
            if (condition[index] != quote)
            {
                continue;
            }

            if (index + 1 < condition.Length && condition[index + 1] == quote)
            {
                index++;
                continue;
            }

            return index;
        }

        return -1;
    }
}
