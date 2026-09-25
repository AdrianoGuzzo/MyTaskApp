namespace MyTaskApp.Desktop.SpellChecking;

/// <summary>Uma palavra do texto e onde ela está.</summary>
public readonly record struct WordSpan(int Start, int Length, string Text)
{
    public int End => Start + Length;

    /// <summary>
    /// Se o índice cai na palavra — inclusive logo depois da última letra, que
    /// é onde o cursor fica enquanto se digita e onde o clique direito cai
    /// quando acerta a metade final do último caractere.
    /// </summary>
    public bool Touches(int index) => index >= Start && index <= End;
}

/// <summary>
/// O que, num campo de texto, é palavra a verificar (ADR-032).
/// </summary>
/// <remarks>
/// <para>
/// Função pura, como <c>MarkdownEditing</c>: o corretor do sistema só recebe o
/// que sair daqui. A anotação é Markdown e o app é de desenvolvimento, então a
/// maior parte do trabalho é <b>não</b> marcar o que não é prosa — um
/// sublinhado vermelho em cada path e cada nome de branch faria o usuário
/// desligar a leitura do sublinhado, e aí ele não serve para nada.
/// </para>
/// <para>
/// Fica de fora: código Markdown (entre crases e em bloco <c>```</c>), URL,
/// e-mail, <c>@alias</c>, <c>#tag</c>, path, palavra grudada em dígito ou
/// <c>_</c>, sigla (tudo maiúsculo), camelCase e palavra de uma letra.
/// </para>
/// </remarks>
public static class SpellingTokenizer
{
    public static IReadOnlyList<WordSpan> Words(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var skip = new bool[text.Length];
        MarkCode(text, skip);

        var words = new List<WordSpan>();
        var index = 0;

        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            var chunkStart = index;

            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            AddWords(text, chunkStart, index, skip, words);
        }

        return words;
    }

    /// <summary>
    /// Um pedaço sem espaço: ou é inteiro não-prosa (URL, path, alias), ou é
    /// quebrado em palavras pela pontuação em volta.
    /// </summary>
    private static void AddWords(string text, int start, int end, bool[] skip, List<WordSpan> words)
    {
        var chunk = text.AsSpan(start, end - start);

        if (IsNotProse(chunk))
        {
            return;
        }

        var index = start;

        while (index < end)
        {
            if (!char.IsLetter(text[index]))
            {
                index++;
                continue;
            }

            var wordStart = index;

            while (index < end
                && (char.IsLetter(text[index])
                    || (IsJoiner(text[index]) && index + 1 < end && char.IsLetter(text[index + 1]))))
            {
                index++;
            }

            var length = index - wordStart;

            if (!IsGluedToCode(text, wordStart, index, start, end)
                && !IsSkipped(skip, wordStart, index)
                && IsCheckable(text.AsSpan(wordStart, length)))
            {
                words.Add(new WordSpan(wordStart, length, text.Substring(wordStart, length)));
            }
        }
    }

    private static bool IsNotProse(ReadOnlySpan<char> chunk) =>
        chunk[0] is '@' or '#'
        || chunk.Contains('@')
        || chunk.Contains('\\')
        || chunk.Contains('/')
        || chunk.Contains("://", StringComparison.Ordinal)
        || chunk.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hífen e apóstrofo entre letras: "guarda-chuva", "d'água".</summary>
    private static bool IsJoiner(char c) => c is '-' or '\'' or '’';

    /// <summary>
    /// <c>v2</c>, <c>snake_case</c>, <c>x86</c>: é identificador, não palavra.
    /// O <c>_</c> só conta com algo do outro lado — na ponta é o itálico do
    /// Markdown (<c>_assim_</c>).
    /// </summary>
    private static bool IsGluedToCode(string text, int wordStart, int wordEnd, int chunkStart, int chunkEnd) =>
        (wordStart > chunkStart && IsCodeNeighbor(text, wordStart - 1, -1, chunkStart, chunkEnd))
        || (wordEnd < chunkEnd && IsCodeNeighbor(text, wordEnd, 1, chunkStart, chunkEnd));

    private static bool IsCodeNeighbor(string text, int index, int direction, int chunkStart, int chunkEnd)
    {
        if (char.IsDigit(text[index]))
        {
            return true;
        }

        var beyond = index + direction;

        return text[index] == '_'
            && beyond >= chunkStart
            && beyond < chunkEnd
            && char.IsLetterOrDigit(text[beyond]);
    }

    private static bool IsSkipped(bool[] skip, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (skip[i])
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCheckable(ReadOnlySpan<char> word)
    {
        if (word.Length < 2)
        {
            return false;
        }

        // Sigla (API, PR, SQL) e camelCase (TaskItem, iPhone): nome próprio de
        // coisa técnica, que nenhum dicionário conhece. Só a primeira letra
        // maiúscula é começo de frase.
        for (var i = 1; i < word.Length; i++)
        {
            if (char.IsUpper(word[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Marca o que está entre crases e dentro de blocos <c>```</c>. Crase sem
    /// par numa linha não abre nada: é mais provável ser digitação pela metade
    /// do que código.
    /// </summary>
    private static void MarkCode(string text, bool[] skip)
    {
        var inFence = false;
        var lineStart = 0;

        while (lineStart <= text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);

            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            var line = text.AsSpan(lineStart, lineEnd - lineStart);

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                Array.Fill(skip, true, lineStart, lineEnd - lineStart);
                inFence = !inFence;
            }
            else if (inFence)
            {
                Array.Fill(skip, true, lineStart, lineEnd - lineStart);
            }
            else
            {
                MarkInlineCode(text, lineStart, lineEnd, skip);
            }

            lineStart = lineEnd + 1;
        }
    }

    private static void MarkInlineCode(string text, int lineStart, int lineEnd, bool[] skip)
    {
        var open = -1;

        for (var i = lineStart; i < lineEnd; i++)
        {
            if (text[i] != '`')
            {
                continue;
            }

            if (open < 0)
            {
                open = i;
            }
            else
            {
                Array.Fill(skip, true, open, i - open + 1);
                open = -1;
            }
        }
    }
}
