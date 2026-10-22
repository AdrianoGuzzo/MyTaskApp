namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// O script de um perfil de anonimização que roda no destino depois da cópia
/// (ADR-082): SQL livre do usuário, para ajustar o banco copiado — trocar
/// URLs e e-mails de configuração, desligar integrações, criar um usuário de teste.
/// </summary>
/// <remarks>
/// <para>
/// Aqui só a forma: tamanho, caracteres, e nenhum comando que encerre a
/// transação. Se o SQL roda, quem diz é o PostgreSQL do destino, numa transação
/// que desfaz tudo se um comando falhar. Onde ele pode rodar, quem diz é a política.
/// </para>
/// <para>
/// Um <c>COMMIT</c> no meio gravaria a metade de cima mesmo com a de baixo
/// falhando. A leitura é léxica, como a das linhas sem máscara (ADR-080): pula
/// textos, identificadores citados, comentários e dollar-quoting, e olha só a
/// primeira palavra de cada comando. Um <c>COMMIT</c> dentro de um <c>DO $$ … $$</c>
/// o servidor mesmo recusa, dentro da transação.
/// </para>
/// </remarks>
public static class PostCopyScriptText
{
    public const int MaxLength = 100_000;

    /// <summary>Comandos que abrem ou encerram uma transação: o app já abre a dele.</summary>
    private static readonly HashSet<string> TransactionControl = new(StringComparer.OrdinalIgnoreCase)
    {
        "BEGIN", "START", "COMMIT", "END", "ROLLBACK", "ABORT",
    };

    /// <summary>O script aparado e conferido; vazio ou só espaços vira <c>null</c> (sem script).</summary>
    public static string? Normalize(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return null;
        }

        var normalized = script.Trim();

        if (normalized.Length > MaxLength)
        {
            throw new DomainException($"O script depois da cópia passa de {MaxLength:N0} caracteres.");
        }

        // Quebra de linha e tabulação são do texto; outro caractere de controle é lixo colado.
        if (normalized.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            throw new DomainException("O script depois da cópia tem um caractere de controle.");
        }

        if (FindTransactionControl(normalized) is { } command)
        {
            throw new DomainException(
                $"O script depois da cópia não pode ter {command}: o app já roda o script inteiro numa transação, " +
                "e desfaz tudo se um comando falhar.");
        }

        return normalized;
    }

    /// <summary>O primeiro comando de transação do script, em maiúsculas; <c>null</c> se não houver.</summary>
    internal static string? FindTransactionControl(string script)
    {
        var atStatementStart = true;
        var index = 0;

        while (index < script.Length)
        {
            var current = script[index];
            var next = index + 1 < script.Length ? script[index + 1] : '\0';

            if (char.IsWhiteSpace(current))
            {
                index++;
            }
            else if (current == '-' && next == '-')
            {
                index = script.IndexOf('\n', index) is var end and >= 0 ? end + 1 : script.Length;
            }
            else if (current == '/' && next == '*')
            {
                index = SkipBlockComment(script, index);
            }
            else if (current == ';')
            {
                atStatementStart = true;
                index++;
            }
            else if (IsIdentifierStart(current))
            {
                var start = index;

                while (index < script.Length && IsIdentifierPart(script[index]))
                {
                    index++;
                }

                var word = script[start..index];

                // E'…': o único texto em que a barra invertida escapa a aspa.
                if (index < script.Length && script[index] == '\'' && word is "E" or "e")
                {
                    index = SkipQuoted(script, index, '\'', backslashEscapes: true);
                }

                if (atStatementStart && TransactionControl.Contains(word) && !IsRollbackToSavepoint(script, word, index))
                {
                    return word.ToUpperInvariant();
                }

                atStatementStart = false;
            }
            else
            {
                atStatementStart = false;
                index = current switch
                {
                    '\'' => SkipQuoted(script, index, '\'', backslashEscapes: false),
                    '"' => SkipQuoted(script, index, '"', backslashEscapes: false),
                    '$' => SkipDollarQuoted(script, index),
                    _ => index + 1,
                };
            }
        }

        return null;
    }

    /// <summary><c>ROLLBACK TO SAVEPOINT</c> volta a um ponto dentro da transação, sem encerrá-la.</summary>
    private static bool IsRollbackToSavepoint(string script, string word, int afterWord)
    {
        if (!word.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = script.AsSpan(afterWord).TrimStart();
        return rest.StartsWith("TO", StringComparison.OrdinalIgnoreCase) && (rest.Length == 2 || !IsIdentifierPart(rest[2]));
    }

    /// <summary>Do caractere de abertura até depois do de fechamento; a aspa dobrada continua o texto.</summary>
    private static int SkipQuoted(string script, int open, char quote, bool backslashEscapes)
    {
        var index = open + 1;

        while (index < script.Length)
        {
            if (backslashEscapes && script[index] == '\\')
            {
                index += 2;
            }
            else if (script[index] == quote)
            {
                if (index + 1 < script.Length && script[index + 1] == quote)
                {
                    index += 2;
                }
                else
                {
                    return index + 1;
                }
            }
            else
            {
                index++;
            }
        }

        return script.Length;
    }

    /// <summary><c>$$…$$</c> ou <c>$tag$…$tag$</c>; um <c>$1</c> (parâmetro) é só um caractere.</summary>
    private static int SkipDollarQuoted(string script, int open)
    {
        var close = open + 1;

        while (close < script.Length && script[close] != '$' && IsIdentifierPart(script[close]))
        {
            close++;
        }

        var tagIsValid = close < script.Length
            && script[close] == '$'
            && (close == open + 1 || IsIdentifierStart(script[open + 1]));

        if (!tagIsValid)
        {
            return open + 1;
        }

        var tag = script[open..(close + 1)];
        var end = script.IndexOf(tag, close + 1, StringComparison.Ordinal);
        return end < 0 ? script.Length : end + tag.Length;
    }

    /// <summary>O PostgreSQL aninha comentários de bloco.</summary>
    private static int SkipBlockComment(string script, int open)
    {
        var depth = 0;
        var index = open;

        while (index < script.Length)
        {
            if (script[index] == '/' && index + 1 < script.Length && script[index + 1] == '*')
            {
                depth++;
                index += 2;
            }
            else if (script[index] == '*' && index + 1 < script.Length && script[index + 1] == '/')
            {
                depth--;
                index += 2;

                if (depth == 0)
                {
                    return index;
                }
            }
            else
            {
                index++;
            }
        }

        return script.Length;
    }

    private static bool IsIdentifierStart(char character) => char.IsLetter(character) || character == '_';

    // O $ continua um identificador (a$b), mas não o começa.
    private static bool IsIdentifierPart(char character) => char.IsLetterOrDigit(character) || character is '_' or '$';
}
