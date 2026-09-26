namespace MyTaskApp.Desktop.Notes;

/// <summary>O <c>@texto</c> que está sendo digitado: onde começa e o que vem depois do <c>@</c>.</summary>
public readonly record struct AliasToken(int Start, string Query);

/// <summary>O texto depois de aceitar um atalho, com o cursor no fim do que entrou.</summary>
public readonly record struct AliasEdit(string Text, int CaretIndex);

/// <summary>
/// A aritmética do autocomplete de diretórios (ADR-026), sem controle nenhum —
/// testada como <see cref="MarkdownEditing"/>.
/// </summary>
/// <remarks>
/// O alias é atalho de digitação: aceitar troca <c>@query</c> pelo path real, e
/// o texto não guarda marca nenhuma de onde ele veio.
/// </remarks>
public static class AliasCompletion
{
    /// <summary>Os caracteres que um alias aceita depois do <c>@</c> (ver <c>TagDirectory.NormalizeAlias</c>).</summary>
    public static bool IsAliasChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_';

    /// <summary>
    /// O <c>@</c> ativo antes do cursor, se houver. Ele precisa abrir o texto ou
    /// vir depois de espaço ou pontuação: em "fulano@empresa.com" o <c>@</c> é
    /// de um e-mail, e abrir a lista ali seria atrapalhar.
    /// </summary>
    public static AliasToken? FindToken(string? text, int caretIndex) =>
        FindToken(text, caretIndex, IsAliasChar);

    /// <summary>
    /// Como <see cref="FindToken(string?, int)"/>, com outros caracteres depois
    /// do <c>@</c> — a referência a arquivo (ADR-039) aceita também a barra.
    /// </summary>
    public static AliasToken? FindToken(string? text, int caretIndex, Func<char, bool> isTokenChar)
    {
        text ??= string.Empty;

        if (caretIndex < 0 || caretIndex > text.Length)
        {
            return null;
        }

        var start = caretIndex;

        while (start > 0 && isTokenChar(text[start - 1]))
        {
            start--;
        }

        if (start == 0 || text[start - 1] != '@')
        {
            return null;
        }

        var at = start - 1;

        if (at > 0 && !IsBoundary(text[at - 1]))
        {
            return null;
        }

        return new AliasToken(at, text[start..caretIndex]);
    }

    /// <summary>
    /// Filtra por trecho, e não só por começo: "@eco" acha "@scripts-eco". O que
    /// começa com o digitado vem primeiro; o resto mantém a ordem recebida.
    /// O nome do diretório também conta, para quem lembra dele e não do alias.
    /// </summary>
    public static IReadOnlyList<T> Filter<T>(
        IEnumerable<T> items,
        string query,
        Func<T, string> alias,
        Func<T, string?> name)
    {
        if (string.IsNullOrEmpty(query))
        {
            return [.. items];
        }

        return
        [
            .. items
                .Select(item => (Item: item, Body: alias(item).TrimStart('@')))
                .Where(entry =>
                    entry.Body.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (name(entry.Item)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderBy(entry => entry.Body.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .Select(entry => entry.Item),
        ];
    }

    /// <summary>
    /// Troca o <c>@query</c> pelo path. Leva junto o resto do alias depois do
    /// cursor, para quem voltou ao meio de um "@eco-co" não ficar com "co" solto.
    /// </summary>
    public static AliasEdit Accept(string? text, AliasToken token, int caretIndex, string path) =>
        Accept(text, token, caretIndex, path, IsAliasChar);

    /// <summary>Como <see cref="Accept(string?, AliasToken, int, string)"/>, com os caracteres do token de quem chama.</summary>
    public static AliasEdit Accept(
        string? text,
        AliasToken token,
        int caretIndex,
        string path,
        Func<char, bool> isTokenChar)
    {
        text ??= string.Empty;

        var end = Math.Clamp(caretIndex, token.Start, text.Length);

        while (end < text.Length && isTokenChar(text[end]))
        {
            end++;
        }

        var result = string.Concat(text.AsSpan(0, token.Start), path, text.AsSpan(end));

        return new AliasEdit(result, token.Start + path.Length);
    }

    private static bool IsBoundary(char c) =>
        char.IsWhiteSpace(c) || (!char.IsLetterOrDigit(c) && !IsAliasChar(c) && c != '@');
}
