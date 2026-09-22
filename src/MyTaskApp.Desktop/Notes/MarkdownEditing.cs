using System.Text;

namespace MyTaskApp.Desktop.Notes;

/// <summary>Os botões da barra de formatação, e os atalhos que os repetem.</summary>
public enum MarkdownCommand
{
    Bold,
    Italic,
    Underline,
    Strikethrough,
    Heading1,
    Heading2,
    Bullet,
    Numbered,
}

/// <summary>O texto depois do botão, e onde a seleção tem de ficar.</summary>
public readonly record struct MarkdownEdit(string Text, int SelectionStart, int SelectionLength);

/// <summary>
/// O que cada botão da barra faz com o texto e com a seleção.
/// </summary>
/// <remarks>
/// <para>
/// Função pura de propósito: recebe texto e seleção, devolve texto e seleção.
/// A <see cref="Avalonia.Controls.TextBox"/> não entra aqui, então toda a
/// aritmética de índice — a parte que erra em silêncio e só aparece quando o
/// cursor pula para o lugar errado — é testável sem levantar janela.
/// </para>
/// <para>
/// Todo comando alterna. Clicar em <b>B</b> com um trecho já em negrito tira o
/// negrito; é o que qualquer editor faz, e sem isso o único jeito de desfazer
/// seria caçar os asteriscos na mão.
/// </para>
/// </remarks>
public static class MarkdownEditing
{
    private const string BulletPrefix = "- ";

    public static MarkdownEdit Apply(
        string? text,
        int selectionStart,
        int selectionLength,
        MarkdownCommand command)
    {
        var source = text ?? string.Empty;

        // A seleção chega em qualquer ordem — arrastar da direita para a
        // esquerda entrega um comprimento negativo — e pode apontar para fora
        // do texto depois de um desfazer.
        var start = Math.Clamp(selectionStart, 0, source.Length);
        var end = Math.Clamp(selectionStart + selectionLength, 0, source.Length);

        if (end < start)
        {
            (start, end) = (end, start);
        }

        return InlineToken(command) is { } token
            ? ApplyInline(source, start, end, token)
            : ApplyBlock(source, start, end, command);
    }

    private static string? InlineToken(MarkdownCommand command) => command switch
    {
        MarkdownCommand.Bold => "**",
        MarkdownCommand.Italic => "*",
        MarkdownCommand.Underline => "__",
        MarkdownCommand.Strikethrough => "~~",
        _ => null,
    };

    private static MarkdownEdit ApplyInline(string text, int start, int end, string token)
    {
        // Sem seleção: abre o par e põe o cursor no meio, para o usuário
        // simplesmente continuar digitando já formatado.
        if (start == end)
        {
            return new MarkdownEdit(
                text.Insert(start, token + token),
                start + token.Length,
                0);
        }

        // Marcadores por fora da seleção: o usuário selecionou "negrito" de
        // "**negrito**" e clicou em B de novo.
        if (AlreadyWrapped(text, start, end, token))
        {
            var stripped = text
                .Remove(end, token.Length)
                .Remove(start - token.Length, token.Length);

            return new MarkdownEdit(stripped, start - token.Length, end - start);
        }

        var selected = text[start..end];

        // Marcadores por dentro: a seleção pegou "**negrito**" inteiro.
        if (selected.Length > token.Length * 2
            && selected.StartsWith(token, StringComparison.Ordinal)
            && selected.EndsWith(token, StringComparison.Ordinal))
        {
            var inner = selected[token.Length..^token.Length];

            return new MarkdownEdit(
                string.Concat(text.AsSpan(0, start), inner, text.AsSpan(end)),
                start,
                inner.Length);
        }

        return new MarkdownEdit(
            string.Concat(text[..start], token, selected, token, text[end..]),
            start + token.Length,
            selected.Length);
    }

    /// <summary>
    /// Se o traço deste botão já cerca a seleção — contando o <b>tamanho do
    /// bando</b> de asteriscos, e não só os caracteres colados nela.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Comparar caractere a caractere parece bastar e não basta: em
    /// <c>**negrito**</c>, o <c>*</c> colado na seleção é metade do marcador de
    /// negrito. Clicar em <b>I</b> ali tiraria um asterisco de cada lado e
    /// transformaria negrito em itálico — um botão desfazendo o outro.
    /// </para>
    /// <para>
    /// A convenção do Markdown resolve pelo tamanho do bando: um asterisco é
    /// itálico, dois são negrito, três são os dois. Então itálico está presente
    /// quando o bando é ímpar, e negrito quando tem dois ou mais.
    /// </para>
    /// </remarks>
    private static bool AlreadyWrapped(string text, int start, int end, string token)
    {
        var delimiter = token[0];

        var before = 0;

        while (start - before - 1 >= 0 && text[start - before - 1] == delimiter)
        {
            before++;
        }

        var after = 0;

        while (end + after < text.Length && text[end + after] == delimiter)
        {
            after++;
        }

        var run = Math.Min(before, after);

        return token.Length == 1 ? run % 2 == 1 : run >= token.Length;
    }

    private static MarkdownEdit ApplyBlock(
        string text,
        int start,
        int end,
        MarkdownCommand command)
    {
        var blockStart = LineStart(text, start);
        var blockEnd = LineEnd(text, end);
        var lines = text[blockStart..blockEnd].Split('\n');

        // Tirar só quando *todas* as linhas já têm o marcador. Com uma linha de
        // fora, o clique completa a seleção em vez de esvaziá-la — que é o que
        // o usuário quis dizer ao selecionar um bloco misto.
        //
        // Linha em branco não conta para nenhum dos lados: ela nunca recebe
        // marcador, então exigir que ela tivesse um deixaria uma lista com um
        // respiro no meio impossível de desfazer.
        var marked = 0;
        var eligible = 0;

        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            eligible++;

            if (HasPrefix(line, command))
            {
                marked++;
            }
        }

        var removing = eligible > 0 && marked == eligible;

        var rewritten = new StringBuilder();
        var ordinal = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                rewritten.Append('\n');
            }

            var bare = StripPrefix(lines[index]);

            // Linha em branco no meio da seleção não vira marcador solto.
            if (removing || bare.Length == 0)
            {
                rewritten.Append(bare);
                continue;
            }

            rewritten.Append(PrefixFor(command, ++ordinal)).Append(bare);
        }

        var replacement = rewritten.ToString();

        return new MarkdownEdit(
            string.Concat(text.AsSpan(0, blockStart), replacement, text.AsSpan(blockEnd)),
            blockStart,
            replacement.Length);
    }

    private static string PrefixFor(MarkdownCommand command, int ordinal) => command switch
    {
        MarkdownCommand.Heading1 => "# ",
        MarkdownCommand.Heading2 => "## ",
        MarkdownCommand.Bullet => BulletPrefix,
        MarkdownCommand.Numbered => $"{ordinal}. ",
        _ => string.Empty,
    };

    private static bool HasPrefix(string line, MarkdownCommand command)
    {
        var trimmed = line.TrimStart();

        return command switch
        {
            MarkdownCommand.Heading1 => trimmed.StartsWith("# ", StringComparison.Ordinal),
            MarkdownCommand.Heading2 => trimmed.StartsWith("## ", StringComparison.Ordinal),
            MarkdownCommand.Bullet => trimmed.StartsWith(BulletPrefix, StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal),
            MarkdownCommand.Numbered => OrdinalLength(trimmed) > 0,
            _ => false,
        };
    }

    /// <summary>
    /// Tira qualquer marcador de linha, não só o do botão clicado: transformar
    /// um título em item de lista é uma troca, e deixar os dois marcadores na
    /// mesma linha renderizaria "# " como texto.
    /// </summary>
    private static string StripPrefix(string line)
    {
        var trimmed = line.TrimStart();

        foreach (var prefix in (string[])["## ", "# ", BulletPrefix, "* "])
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return trimmed[prefix.Length..].TrimStart();
            }
        }

        var digits = OrdinalLength(trimmed);

        return digits > 0 ? trimmed[digits..].TrimStart() : trimmed;
    }

    /// <summary>Quantos caracteres o "1. " do começo ocupa; 0 se não houver.</summary>
    private static int OrdinalLength(string line)
    {
        var digits = 0;

        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        return digits > 0
            && digits + 1 < line.Length
            && line[digits] is '.' or ')'
            && line[digits + 1] == ' '
                ? digits + 2
                : 0;
    }

    private static int LineStart(string text, int index)
    {
        if (index <= 0)
        {
            return 0;
        }

        var previous = text.LastIndexOf('\n', Math.Min(index, text.Length) - 1);

        return previous < 0 ? 0 : previous + 1;
    }

    private static int LineEnd(string text, int index)
    {
        if (index >= text.Length)
        {
            return text.Length;
        }

        var next = text.IndexOf('\n', index);

        return next < 0 ? text.Length : next;
    }
}
