using System.Text;

namespace MyTaskApp.Desktop.Notes;

/// <summary>Os traços que o subconjunto de Markdown sabe aplicar a um trecho.</summary>
[Flags]
public enum MarkdownStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strikethrough = 8,
}

/// <summary>Um pedaço de texto com a formatação já resolvida.</summary>
public sealed record MarkdownSpan(string Text, MarkdownStyle Style);

/// <summary>O que uma linha é, depois de lido o marcador do começo.</summary>
public enum MarkdownBlockKind
{
    Paragraph,
    Heading1,
    Heading2,
    Bullet,
    Numbered,
}

/// <param name="Kind">Título, item de lista ou parágrafo.</param>
/// <param name="Text">A linha sem o marcador, ainda com a formatação de trecho.</param>
/// <param name="Ordinal">O número desta linha na lista numerada; 0 nos demais.</param>
public sealed record MarkdownBlock(MarkdownBlockKind Kind, string Text, int Ordinal = 0);

/// <summary>
/// O subconjunto de Markdown que a tela de anotações entende, lido sem nenhuma
/// dependência de UI.
/// </summary>
/// <remarks>
/// <para>
/// É um subconjunto escrito à mão, e não um pacote. O
/// <c>Avalonia.Controls.Markdown</c> e o <c>Avalonia.Controls.RichTextEditor</c>
/// oficiais resolveriam o problema inteiro, mas os dois exigem licença Avalonia
/// Pro paga — e o app não tem nenhuma dependência paga. O custo aceito é este
/// arquivo; o que se ganha é que a anotação do usuário continua sendo texto
/// puro no banco, legível por qualquer coisa que abra o SQLite.
/// </para>
/// <para>
/// Bloco e trecho são separados de propósito: título e item de lista são
/// construções de <b>linha</b>, e negrito/itálico são construções de
/// <b>trecho</b>. Resolver os dois num passo só é o que faz um parser de
/// Markdown caseiro virar um emaranhado.
/// </para>
/// </remarks>
public static class MarkdownDocument
{
    /// <summary>
    /// Os marcadores de trecho, na ordem em que são tentados. Do mais longo
    /// para o mais curto, e isso é obrigatório: com <c>*</c> testado antes de
    /// <c>**</c>, todo negrito viraria um itálico vazio seguido de lixo.
    /// </summary>
    /// <remarks>
    /// <c>***</c> não é decoração da lista: é o que a própria barra escreve
    /// quando alguém clica em <b>B</b> e depois em <b>I</b> no mesmo trecho. Sem
    /// ele, o parser leria o par de fora, sobraria um asterisco solto dentro, e
    /// o texto do usuário apareceria com o marcador à mostra.
    /// </remarks>
    private static readonly (string Token, MarkdownStyle Style)[] InlineMarkers =
    [
        ("***", MarkdownStyle.Bold | MarkdownStyle.Italic),
        ("**", MarkdownStyle.Bold),
        ("__", MarkdownStyle.Underline),
        ("~~", MarkdownStyle.Strikethrough),
        ("*", MarkdownStyle.Italic),
    ];

    /// <summary>Quebra o texto em blocos de linha, já sem os marcadores.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var blocks = new List<MarkdownBlock>();
        var paragraph = new List<string>();

        // A numeração é recontada, e não copiada: quem escreve "1." três vezes
        // quer uma lista de três itens, não três itens número um.
        var numbering = 0;

        foreach (var rawLine in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.Length == 0)
            {
                FlushParagraph(paragraph, blocks);
                numbering = 0;
                continue;
            }

            if (TryTakePrefix(line, "## ", out var heading2))
            {
                FlushParagraph(paragraph, blocks);
                numbering = 0;
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading2, heading2));
                continue;
            }

            if (TryTakePrefix(line, "# ", out var heading1))
            {
                FlushParagraph(paragraph, blocks);
                numbering = 0;
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading1, heading1));
                continue;
            }

            if (TryTakePrefix(line, "- ", out var bullet)
                || TryTakePrefix(line, "* ", out bullet))
            {
                FlushParagraph(paragraph, blocks);
                numbering = 0;
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Bullet, bullet));
                continue;
            }

            if (TryTakeOrdinal(line, out var numbered))
            {
                FlushParagraph(paragraph, blocks);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Numbered, numbered, ++numbering));
                continue;
            }

            numbering = 0;
            paragraph.Add(line);
        }

        FlushParagraph(paragraph, blocks);

        return blocks;
    }

    /// <summary>
    /// Resolve os marcadores de trecho de uma linha. Marcador sem par volta a
    /// ser texto literal — quem digitou "2 * 3 = 6" não pediu itálico nenhum.
    /// </summary>
    public static IReadOnlyList<MarkdownSpan> ParseInlines(string? text)
    {
        var spans = new List<MarkdownSpan>();

        if (!string.IsNullOrEmpty(text))
        {
            Scan(text, MarkdownStyle.None, spans);
        }

        return spans;
    }

    /// <summary>
    /// O texto sem marcador nenhum, para quem só precisa de uma linha crua —
    /// o balão da lista, por exemplo.
    /// </summary>
    public static string ToPlainText(string? markdown)
    {
        var plain = new StringBuilder();

        foreach (var block in Parse(markdown))
        {
            if (plain.Length > 0)
            {
                plain.Append(' ');
            }

            foreach (var span in ParseInlines(block.Text))
            {
                plain.Append(span.Text);
            }
        }

        return plain.ToString();
    }

    private static void Scan(string text, MarkdownStyle style, List<MarkdownSpan> spans)
    {
        var literal = new StringBuilder();
        var index = 0;

        while (index < text.Length)
        {
            if (MatchMarker(text, index, style) is not { } marker)
            {
                literal.Append(text[index]);
                index++;
                continue;
            }

            var contentStart = index + marker.Token.Length;
            var close = text.IndexOf(marker.Token, contentStart, StringComparison.Ordinal);

            // Sem fechamento, ou fechando no vácuo: o marcador é só texto.
            if (close < 0 || close == contentStart)
            {
                literal.Append(marker.Token);
                index = contentStart;
                continue;
            }

            Flush(literal, style, spans);
            Scan(text[contentStart..close], style | marker.Style, spans);
            index = close + marker.Token.Length;
        }

        Flush(literal, style, spans);
    }

    /// <summary>
    /// O marcador que começa nesta posição, se houver. Marcador cujo traço já
    /// está valendo é ignorado: dentro de negrito, <c>**</c> é texto.
    /// </summary>
    private static (string Token, MarkdownStyle Style)? MatchMarker(
        string text,
        int index,
        MarkdownStyle style)
    {
        foreach (var marker in InlineMarkers)
        {
            if (style.HasFlag(marker.Style))
            {
                continue;
            }

            if (string.CompareOrdinal(text, index, marker.Token, 0, marker.Token.Length) == 0)
            {
                return marker;
            }
        }

        return null;
    }

    private static void Flush(StringBuilder literal, MarkdownStyle style, List<MarkdownSpan> spans)
    {
        if (literal.Length == 0)
        {
            return;
        }

        spans.Add(new MarkdownSpan(literal.ToString(), style));
        literal.Clear();
    }

    /// <summary>
    /// Linhas seguidas viram um parágrafo só, separadas por quebra simples. É o
    /// que faz uma anotação digitada sem linha em branco continuar parecendo
    /// uma lista de recados, e não uma parede de texto.
    /// </summary>
    private static void FlushParagraph(List<string> paragraph, List<MarkdownBlock> blocks)
    {
        if (paragraph.Count == 0)
        {
            return;
        }

        blocks.Add(new MarkdownBlock(
            MarkdownBlockKind.Paragraph,
            string.Join('\n', paragraph)));

        paragraph.Clear();
    }

    private static bool TryTakePrefix(string line, string prefix, out string rest)
    {
        if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            rest = line[prefix.Length..].Trim();
            return true;
        }

        rest = string.Empty;
        return false;
    }

    /// <summary>Reconhece "1. ", "2) " e companhia.</summary>
    private static bool TryTakeOrdinal(string line, out string rest)
    {
        var digits = 0;

        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        if (digits > 0
            && digits + 1 < line.Length
            && line[digits] is '.' or ')'
            && line[digits + 1] == ' ')
        {
            rest = line[(digits + 2)..].Trim();
            return true;
        }

        rest = string.Empty;
        return false;
    }
}
