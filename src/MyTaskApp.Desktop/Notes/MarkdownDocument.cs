using System.Text;

namespace MyTaskApp.Desktop.Notes;

/// <summary>Os traços que o Markdown das anotações sabe aplicar a um trecho.</summary>
[Flags]
public enum MarkdownStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strikethrough = 8,

    /// <summary>Entre crases: fonte de terminal, e nada dentro é formatação.</summary>
    Code = 16,
}

/// <summary>Um pedaço de texto com a formatação já resolvida.</summary>
/// <param name="Url">O destino, quando o trecho é um link; nulo nos demais.</param>
public sealed record MarkdownSpan(string Text, MarkdownStyle Style, string? Url = null);

/// <summary>O que um bloco é, depois de lido o marcador do começo.</summary>
public enum MarkdownBlockKind
{
    Paragraph,
    Heading1,
    Heading2,
    Heading3,
    Heading4,
    Heading5,
    Heading6,
    Bullet,
    Numbered,

    /// <summary>Item de lista com caixa: <c>- [ ]</c> ou <c>- [x]</c>.</summary>
    Task,

    /// <summary>Citação (<c>&gt;</c>). O conteúdo está em <see cref="MarkdownBlock.Children"/>.</summary>
    Quote,

    /// <summary>Bloco de código cercado. O texto é literal, com o recuo preservado.</summary>
    Code,

    /// <summary>Linha horizontal (<c>---</c>, <c>***</c>, <c>___</c>).</summary>
    Rule,

    /// <summary>Tabela no formato do GitHub. As células estão em <see cref="MarkdownBlock.Rows"/>.</summary>
    Table,
}

/// <summary>O alinhamento de uma coluna da tabela, lido dos dois-pontos da linha separadora.</summary>
public enum MarkdownAlignment
{
    Left,
    Center,
    Right,
}

/// <param name="Kind">Título, item de lista, parágrafo, código…</param>
/// <param name="Text">
/// O conteúdo sem o marcador, ainda com a formatação de trecho. No código, o
/// texto literal; na citação e na tabela, vazio.
/// </param>
/// <param name="Ordinal">O número desta linha na lista numerada; 0 nos demais.</param>
public sealed record MarkdownBlock(MarkdownBlockKind Kind, string Text, int Ordinal = 0)
{
    /// <summary>A profundidade do item de lista: 0 na margem, 1 dentro de outro item…</summary>
    public int Level { get; init; }

    /// <summary>A caixa do item de tarefa está marcada.</summary>
    public bool IsChecked { get; init; }

    /// <summary>A linguagem escrita depois da cerca do código, se houver.</summary>
    public string? Language { get; init; }

    /// <summary>Os blocos dentro da citação.</summary>
    public IReadOnlyList<MarkdownBlock> Children { get; init; } = [];

    /// <summary>As linhas da tabela, a primeira sendo o cabeçalho.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = [];

    /// <summary>O alinhamento de cada coluna da tabela.</summary>
    public IReadOnlyList<MarkdownAlignment> Alignments { get; init; } = [];

    public bool IsListItem =>
        Kind is MarkdownBlockKind.Bullet or MarkdownBlockKind.Numbered or MarkdownBlockKind.Task;
}

/// <summary>
/// O Markdown que a tela de anotações entende, lido sem nenhuma dependência de
/// UI.
/// </summary>
/// <remarks>
/// <para>
/// É escrito à mão, e não um pacote. O <c>Avalonia.Controls.Markdown</c> e o
/// <c>Avalonia.Controls.RichTextEditor</c> oficiais resolveriam o problema
/// inteiro, mas os dois exigem licença Avalonia Pro paga — e o app não tem
/// nenhuma dependência paga. O custo aceito é este arquivo; o que se ganha é
/// que a anotação do usuário continua sendo texto puro no banco, legível por
/// qualquer coisa que abra o SQLite.
/// </para>
/// <para>
/// A régua é o que o preview do VS Code mostra (CommonMark + as extensões do
/// GitHub): títulos de 1 a 6, listas aninhadas, tarefas, citação, código,
/// linha horizontal, tabela, link e escape. Três diferenças são de propósito:
/// <c>__</c> é sublinhado, porque é o que a barra escreve desde o ADR-024; a
/// quebra de linha simples continua sendo quebra, porque as anotações foram
/// escritas assim; e HTML embutido aparece como texto.
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
        ("_", MarkdownStyle.Italic),
    ];

    /// <summary>Quebra o texto em blocos, já sem os marcadores.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        return new BlockReader(markdown.ReplaceLineEndings("\n").Split('\n')).Read();
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
            Scan(text, MarkdownStyle.None, null, spans);
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
        AppendPlain(Parse(markdown), plain);
        return plain.ToString();
    }

    private static void AppendPlain(IEnumerable<MarkdownBlock> blocks, StringBuilder plain)
    {
        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Quote:
                    AppendPlain(block.Children, plain);
                    continue;

                case MarkdownBlockKind.Rule:
                    continue;

                case MarkdownBlockKind.Code:
                    AppendWord(plain, block.Text.ReplaceLineEndings(" "));
                    continue;

                case MarkdownBlockKind.Table:
                    foreach (var cell in block.Rows.SelectMany(row => row))
                    {
                        AppendWord(plain, InlineText(cell));
                    }

                    continue;

                default:
                    AppendWord(plain, InlineText(block.Text));
                    continue;
            }
        }
    }

    private static string InlineText(string text) =>
        string.Concat(ParseInlines(text).Select(span => span.Text));

    private static void AppendWord(StringBuilder plain, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (plain.Length > 0)
        {
            plain.Append(' ');
        }

        plain.Append(text);
    }

    // ------------------------------------------------------------------
    // Trechos
    // ------------------------------------------------------------------

    private static void Scan(string text, MarkdownStyle style, string? url, List<MarkdownSpan> spans)
    {
        var literal = new StringBuilder();
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            // "\*" é um asterisco, e não o começo de um itálico.
            // Só pontuação ASCII se escapa: "C:\Projetos" continua com a barra.
            if (current == '\\' && index + 1 < text.Length && IsEscapable(text[index + 1]))
            {
                literal.Append(text[index + 1]);
                index += 2;
                continue;
            }

            // Crase antes de tudo: dentro de código, nada é formatação.
            if (current == '`')
            {
                var run = CountRun(text, index, '`');

                if (FindBacktickClose(text, index + run, run) is var close and >= 0)
                {
                    Flush(literal, style, url, spans);
                    spans.Add(new MarkdownSpan(TrimCodeSpan(text[(index + run)..close]), style | MarkdownStyle.Code, url));
                    index = close + run;
                }
                else
                {
                    literal.Append(text, index, run);
                    index += run;
                }

                continue;
            }

            // Link dentro de link não existe: o de fora já decidiu o destino.
            if (url is null && TryLink(text, index, out var label, out var target, out var linkEnd))
            {
                Flush(literal, style, url, spans);

                if (label.Length == 0)
                {
                    spans.Add(new MarkdownSpan(target, style, target));
                }
                else
                {
                    Scan(label, style, target, spans);
                }

                index = linkEnd;
                continue;
            }

            if (url is null && TryAutolink(text, index, out var autolink, out var autolinkEnd))
            {
                Flush(literal, style, url, spans);
                spans.Add(new MarkdownSpan(autolink, style, Normalize(autolink)));
                index = autolinkEnd;
                continue;
            }

            if (MatchMarker(text, index, style) is { } marker
                && FindClose(text, index, marker.Token) is var closeAt and >= 0)
            {
                Flush(literal, style, url, spans);
                Scan(text[(index + marker.Token.Length)..closeAt], style | marker.Style, url, spans);
                index = closeAt + marker.Token.Length;
                continue;
            }

            // Sem par: o marcador inteiro é texto, e não só o primeiro caractere
            // — senão "**sem fechar" tentaria de novo com o "*" que sobrou.
            if (MatchMarker(text, index, style) is { } unpaired)
            {
                literal.Append(unpaired.Token);
                index += unpaired.Token.Length;
                continue;
            }

            literal.Append(current);
            index++;
        }

        Flush(literal, style, url, spans);
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

    /// <summary>
    /// Onde o marcador que abre em <paramref name="open"/> fecha, ou -1.
    /// </summary>
    /// <remarks>
    /// A regra de "flanco" do CommonMark, simplificada: quem abre não pode vir
    /// seguido de espaço, e quem fecha não pode vir depois de um — é o que faz
    /// "2 * 3 * 4" continuar sendo uma conta. O sublinhado ainda não pode estar
    /// grudado numa palavra, senão todo <c>nome_de_variavel</c> viraria itálico.
    /// </remarks>
    private static int FindClose(string text, int open, string token)
    {
        var contentStart = open + token.Length;

        if (contentStart >= text.Length || char.IsWhiteSpace(text[contentStart]))
        {
            return -1;
        }

        var underscore = token[0] == '_';

        if (underscore && open > 0 && char.IsLetterOrDigit(text[open - 1]))
        {
            return -1;
        }

        var from = contentStart;

        while (from < text.Length)
        {
            var close = text.IndexOf(token, from, StringComparison.Ordinal);

            if (close < 0)
            {
                return -1;
            }

            var after = close + token.Length;

            // Fechando no vácuo, ou colado num marcador mais longo do mesmo
            // caractere ("*" achando o primeiro de "**"): não é este o par.
            var valid = close > contentStart
                && !char.IsWhiteSpace(text[close - 1])
                && (after >= text.Length || text[after] != token[0] || token.Length == 3)
                && (!underscore || after >= text.Length || !char.IsLetterOrDigit(text[after]));

            if (valid)
            {
                return close;
            }

            from = close + 1;
        }

        return -1;
    }

    private static bool IsEscapable(char character) =>
        char.IsAscii(character) && (char.IsPunctuation(character) || char.IsSymbol(character));

    private static int CountRun(string text, int index, char character)
    {
        var end = index;

        while (end < text.Length && text[end] == character)
        {
            end++;
        }

        return end - index;
    }

    /// <summary>A crase de fechamento tem de ter o mesmo tamanho da de abertura.</summary>
    private static int FindBacktickClose(string text, int from, int run)
    {
        var index = from;

        while (index < text.Length)
        {
            if (text[index] != '`')
            {
                index++;
                continue;
            }

            var length = CountRun(text, index, '`');

            if (length == run)
            {
                return index;
            }

            index += length;
        }

        return -1;
    }

    /// <summary>
    /// Um espaço de cada lado sai, como no CommonMark: é o que permite escrever
    /// <c>`` `crase` ``</c>.
    /// </summary>
    private static string TrimCodeSpan(string code) =>
        code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0
            ? code[1..^1]
            : code;

    /// <summary>
    /// <c>[texto](destino "título")</c>, e a imagem <c>![alt](destino)</c>, que
    /// aparece como um link para ela: a anotação não carrega arquivos.
    /// </summary>
    private static bool TryLink(string text, int index, out string label, out string target, out int end)
    {
        label = target = string.Empty;
        end = index;

        var open = text[index] == '!' && index + 1 < text.Length && text[index + 1] == '[' ? index + 1
            : text[index] == '[' ? index
            : -1;

        if (open < 0)
        {
            return false;
        }

        var closeLabel = FindMatching(text, open, '[', ']');

        if (closeLabel < 0 || closeLabel + 1 >= text.Length || text[closeLabel + 1] != '(')
        {
            return false;
        }

        var closeTarget = FindMatching(text, closeLabel + 1, '(', ')');

        if (closeTarget < 0)
        {
            return false;
        }

        var destination = text[(closeLabel + 2)..closeTarget].Trim();

        if (destination.StartsWith('<') && destination.IndexOf('>') is var angle and > 0)
        {
            destination = destination[1..angle];
        }
        else if (destination.IndexOfAny([' ', '\t']) is var space and > 0)
        {
            // O que vem depois do espaço é o título do link, que o VS Code só
            // mostra no balão.
            destination = destination[..space];
        }

        if (destination.Length == 0)
        {
            return false;
        }

        label = text[(open + 1)..closeLabel];
        target = destination;
        end = closeTarget + 1;
        return true;
    }

    private static int FindMatching(string text, int open, char opening, char closing)
    {
        var depth = 0;

        for (var index = open; index < text.Length; index++)
        {
            if (text[index] == '\\')
            {
                index++;
                continue;
            }

            if (text[index] == opening)
            {
                depth++;
            }
            else if (text[index] == closing && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// <c>&lt;https://…&gt;</c> e o endereço solto, como o VS Code faz com o
    /// "linkify" ligado: colar uma URL numa anotação já é pedir para clicar nela.
    /// </summary>
    private static bool TryAutolink(string text, int index, out string link, out int end)
    {
        link = string.Empty;
        end = index;

        if (text[index] == '<')
        {
            var close = text.IndexOf('>', index + 1);

            if (close > index + 1)
            {
                var inside = text[(index + 1)..close];

                if (IsUrlStart(inside, 0) && !inside.Contains(' ', StringComparison.Ordinal))
                {
                    link = inside;
                    end = close + 1;
                    return true;
                }
            }

            return false;
        }

        if (!IsUrlStart(text, index) || (index > 0 && char.IsLetterOrDigit(text[index - 1])))
        {
            return false;
        }

        var stop = index;

        while (stop < text.Length && !char.IsWhiteSpace(text[stop]) && text[stop] != '<')
        {
            stop++;
        }

        // A pontuação do fim é da frase, e não do endereço — e o ")" só é do
        // endereço quando abriu dentro dele, como nos links da Wikipédia.
        while (stop > index)
        {
            var last = text[stop - 1];

            if (last is '.' or ',' or ':' or ';' or '!' or '?' or '"' or '\'' or '*' or '_' or '~')
            {
                stop--;
            }
            else if (last == ')' && text[index..stop].Count(c => c == '(') < text[index..stop].Count(c => c == ')'))
            {
                stop--;
            }
            else
            {
                break;
            }
        }

        link = text[index..stop];

        if (link.Length <= "www.".Length || link.EndsWith("://", StringComparison.Ordinal))
        {
            return false;
        }

        end = stop;
        return true;
    }

    private static bool IsUrlStart(string text, int index) =>
        string.Compare(text, index, "https://", 0, 8, StringComparison.OrdinalIgnoreCase) == 0
        || string.Compare(text, index, "http://", 0, 7, StringComparison.OrdinalIgnoreCase) == 0
        || string.Compare(text, index, "www.", 0, 4, StringComparison.OrdinalIgnoreCase) == 0;

    private static string Normalize(string link) =>
        link.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + link : link;

    private static void Flush(StringBuilder literal, MarkdownStyle style, string? url, List<MarkdownSpan> spans)
    {
        if (literal.Length == 0)
        {
            return;
        }

        spans.Add(new MarkdownSpan(literal.ToString(), style, url));
        literal.Clear();
    }

    // ------------------------------------------------------------------
    // Blocos
    // ------------------------------------------------------------------

    /// <summary>
    /// Lê as linhas de cima para baixo. É uma classe, e não um método, porque o
    /// estado entre uma linha e a próxima — o parágrafo em curso, a numeração
    /// de cada nível, o recuo dos itens abertos — é o que decide o que a linha
    /// seguinte é.
    /// </summary>
    private sealed class BlockReader(string[] lines)
    {
        private readonly List<MarkdownBlock> _blocks = [];

        private readonly List<string> _paragraph = [];

        /// <summary>O recuo de cada item de lista aberto, do mais raso ao mais fundo.</summary>
        private readonly List<int> _listIndents = [];

        /// <summary>
        /// A numeração por nível. Recontada, e não copiada: quem escreve "1."
        /// três vezes quer uma lista de três itens, não três itens número um.
        /// </summary>
        private readonly List<int> _numbering = [];

        private int _index;

        public List<MarkdownBlock> Read()
        {
            while (_index < lines.Length)
            {
                ReadLine();
            }

            FlushParagraph();

            return _blocks;
        }

        private void ReadLine()
        {
            var raw = lines[_index];
            var line = raw.Trim();
            var indent = IndentOf(raw);

            if (line.Length == 0)
            {
                FlushParagraph();
                _index++;
                return;
            }

            if (IsFence(line, out var fence))
            {
                FlushParagraph();
                EndList();
                ReadCode(fence, indent, line[fence.Length..].Trim());
                return;
            }

            if (line.StartsWith('>'))
            {
                FlushParagraph();
                EndList();
                ReadQuote();
                return;
            }

            if (TryHeading(line, out var level, out var heading))
            {
                FlushParagraph();
                EndList();
                _blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading1 + (level - 1), heading));
                _index++;
                return;
            }

            // "Texto" seguido de "===" ou "---" é o título sublinhado do
            // Markdown — e é por isso que ele vem antes da linha horizontal.
            if (_paragraph.Count > 0 && IsSetextUnderline(line, out var setext))
            {
                var text = string.Join('\n', _paragraph);
                _paragraph.Clear();
                _blocks.Add(new MarkdownBlock(setext, text));
                _index++;
                return;
            }

            if (IsRule(line))
            {
                FlushParagraph();
                EndList();
                _blocks.Add(new MarkdownBlock(MarkdownBlockKind.Rule, string.Empty));
                _index++;
                return;
            }

            if (TryTable())
            {
                return;
            }

            if (TryListItem(line, indent))
            {
                _index++;
                return;
            }

            // Linha comum logo depois de um item, sem linha em branco entre os
            // dois: é continuação do item, como no CommonMark.
            if (_paragraph.Count == 0 && _listIndents.Count > 0 && _blocks is [.., { IsListItem: true } item]
                && _index > 0 && lines[_index - 1].Trim().Length > 0)
            {
                _blocks[^1] = item with { Text = item.Text + "\n" + line };
                _index++;
                return;
            }

            if (_paragraph.Count == 0 && (indent == 0 || _listIndents.Count == 0))
            {
                EndList();
            }

            _paragraph.Add(line);
            _index++;
        }

        private void ReadCode(string fence, int indent, string info)
        {
            var code = new List<string>();
            _index++;

            while (_index < lines.Length)
            {
                var candidate = lines[_index].Trim();

                if (candidate.Length >= fence.Length
                    && candidate.All(c => c == fence[0])
                    && candidate.StartsWith(fence, StringComparison.Ordinal))
                {
                    _index++;
                    break;
                }

                code.Add(Unindent(lines[_index], indent));
                _index++;
            }

            var language = info.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            _blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, string.Join('\n', code))
            {
                Language = language,
            });
        }

        /// <summary>
        /// As linhas com "&gt;" em sequência, sem ele, lidas de novo como um
        /// documento — é o que deixa haver lista, código e outra citação dentro.
        /// </summary>
        private void ReadQuote()
        {
            var inner = new List<string>();

            while (_index < lines.Length && lines[_index].TrimStart() is var candidate && candidate.StartsWith('>'))
            {
                var content = candidate[1..];
                inner.Add(content.StartsWith(' ') ? content[1..] : content);
                _index++;
            }

            _blocks.Add(new MarkdownBlock(MarkdownBlockKind.Quote, string.Empty)
            {
                Children = new BlockReader([.. inner]).Read(),
            });
        }

        private bool TryTable()
        {
            var header = lines[_index].Trim();

            if (!header.Contains('|', StringComparison.Ordinal)
                || _index + 1 >= lines.Length
                || !TryAlignments(lines[_index + 1].Trim(), out var alignments))
            {
                return false;
            }

            var headerCells = SplitRow(header);

            if (headerCells.Count != alignments.Count)
            {
                return false;
            }

            FlushParagraph();
            EndList();

            var rows = new List<IReadOnlyList<string>> { headerCells };
            _index += 2;

            while (_index < lines.Length
                && lines[_index].Trim() is { Length: > 0 } row
                && row.Contains('|', StringComparison.Ordinal))
            {
                // Linha mais curta ganha células vazias, mais longa perde as
                // sobras: é o que o GitHub faz, e a grade fica retangular.
                var cells = SplitRow(row);
                rows.Add([.. Enumerable.Range(0, alignments.Count)
                    .Select(column => column < cells.Count ? cells[column] : string.Empty)]);
                _index++;
            }

            _blocks.Add(new MarkdownBlock(MarkdownBlockKind.Table, string.Empty)
            {
                Rows = rows,
                Alignments = alignments,
            });

            return true;
        }

        private bool TryListItem(string line, int indent)
        {
            MarkdownBlockKind kind;
            string text;
            var number = 0;

            if (line.Length >= 2 && line[0] is '-' or '*' or '+' && line[1] is ' ' or '\t')
            {
                text = line[2..].Trim();
                kind = MarkdownBlockKind.Bullet;
            }
            else if (line == "-" || line == "*" || line == "+")
            {
                text = string.Empty;
                kind = MarkdownBlockKind.Bullet;
            }
            else if (TryOrdinal(line, out number, out text))
            {
                kind = MarkdownBlockKind.Numbered;
            }
            else
            {
                return false;
            }

            FlushParagraph();

            // Itens com recuo maior ou igual ao do aberto mais fundo o fecham;
            // os que sobram são os pais deste.
            while (_listIndents.Count > 0 && _listIndents[^1] >= indent)
            {
                _listIndents.RemoveAt(_listIndents.Count - 1);
            }

            var level = _listIndents.Count;
            _listIndents.Add(indent);

            if (_numbering.Count > level + 1)
            {
                _numbering.RemoveRange(level + 1, _numbering.Count - level - 1);
            }

            while (_numbering.Count <= level)
            {
                _numbering.Add(0);
            }

            var isChecked = false;

            if (kind == MarkdownBlockKind.Bullet && TryTaskBox(text, out isChecked, out var task))
            {
                kind = MarkdownBlockKind.Task;
                text = task;
            }

            var ordinal = 0;

            if (kind == MarkdownBlockKind.Numbered)
            {
                // A primeira linha dá o começo, como no CommonMark; as
                // seguintes só contam.
                _numbering[level] = _numbering[level] == 0 ? number : _numbering[level] + 1;
                ordinal = _numbering[level];
            }
            else
            {
                _numbering[level] = 0;
            }

            _blocks.Add(new MarkdownBlock(kind, text, ordinal) { Level = level, IsChecked = isChecked });
            return true;
        }

        private void EndList()
        {
            _listIndents.Clear();
            _numbering.Clear();
        }

        /// <summary>
        /// Linhas seguidas viram um parágrafo só, separadas por quebra simples. É o
        /// que faz uma anotação digitada sem linha em branco continuar parecendo
        /// uma lista de recados, e não uma parede de texto.
        /// </summary>
        private void FlushParagraph()
        {
            if (_paragraph.Count == 0)
            {
                return;
            }

            _blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, string.Join('\n', _paragraph)));
            _paragraph.Clear();
        }
    }

    /// <summary>Tabulação conta como quatro espaços, como no CommonMark.</summary>
    private static int IndentOf(string raw)
    {
        var width = 0;

        foreach (var character in raw)
        {
            if (character == ' ')
            {
                width++;
            }
            else if (character == '\t')
            {
                width += 4 - (width % 4);
            }
            else
            {
                break;
            }
        }

        return width;
    }

    /// <summary>O código dentro de uma lista perde o recuo da cerca, e não o próprio.</summary>
    private static string Unindent(string raw, int indent)
    {
        var remove = 0;

        while (remove < raw.Length && remove < indent && raw[remove] == ' ')
        {
            remove++;
        }

        return raw[remove..];
    }

    private static bool IsFence(string line, out string fence)
    {
        fence = string.Empty;

        if (line.Length < 3 || line[0] is not ('`' or '~'))
        {
            return false;
        }

        var run = CountRun(line, 0, line[0]);

        // A crase na informação da cerca abriria um trecho de código, e não um bloco.
        if (run < 3 || (line[0] == '`' && line.IndexOf('`', run) >= 0))
        {
            return false;
        }

        fence = line[..run];
        return true;
    }

    /// <summary>De "#" a "######", seguidos de espaço; os "#" do fim são enfeite.</summary>
    private static bool TryHeading(string line, out int level, out string text)
    {
        level = CountRun(line, 0, '#');
        text = string.Empty;

        if (level is < 1 or > 6 || (line.Length > level && line[level] is not (' ' or '\t')))
        {
            return false;
        }

        text = line[level..].Trim();

        var closing = text.Length;

        while (closing > 0 && text[closing - 1] == '#')
        {
            closing--;
        }

        if (closing == 0)
        {
            text = string.Empty;
        }
        else if (closing < text.Length && text[closing - 1] == ' ')
        {
            text = text[..closing].TrimEnd();
        }

        return true;
    }

    private static bool IsSetextUnderline(string line, out MarkdownBlockKind kind)
    {
        kind = line[0] == '=' ? MarkdownBlockKind.Heading1 : MarkdownBlockKind.Heading2;
        return line[0] is '=' or '-' && line.All(c => c == line[0]);
    }

    private static bool IsRule(string line)
    {
        if (line[0] is not ('-' or '*' or '_'))
        {
            return false;
        }

        var marker = line[0];
        var count = 0;

        foreach (var character in line)
        {
            if (character == marker)
            {
                count++;
            }
            else if (character is not (' ' or '\t'))
            {
                return false;
            }
        }

        return count >= 3;
    }

    /// <summary>"[ ] " ou "[x] " no começo do item.</summary>
    private static bool TryTaskBox(string text, out bool isChecked, out string rest)
    {
        isChecked = false;
        rest = text;

        if (text.Length < 3 || text[0] != '[' || text[2] != ']' || text[1] is not (' ' or 'x' or 'X'))
        {
            return false;
        }

        if (text.Length > 3 && text[3] is not (' ' or '\t'))
        {
            return false;
        }

        isChecked = text[1] != ' ';
        rest = text[3..].Trim();
        return true;
    }

    /// <summary>Reconhece "1. ", "2) " e companhia.</summary>
    private static bool TryOrdinal(string line, out int number, out string rest)
    {
        var digits = 0;

        while (digits < line.Length && digits < 9 && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        if (digits > 0
            && digits < line.Length
            && line[digits] is '.' or ')'
            && (digits + 1 == line.Length || line[digits + 1] is ' ' or '\t'))
        {
            number = int.Parse(line.AsSpan(0, digits), System.Globalization.CultureInfo.InvariantCulture);
            rest = line[(digits + 1)..].Trim();
            return true;
        }

        number = 0;
        rest = string.Empty;
        return false;
    }

    /// <summary>A linha separadora da tabela: "| --- | :---: | ---: |".</summary>
    private static bool TryAlignments(string line, out List<MarkdownAlignment> alignments)
    {
        alignments = [];

        if (!line.Contains('-', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var cell in SplitRow(line))
        {
            if (cell.Length == 0 || cell.Trim(':').Length == 0 || cell.Trim(':').Any(c => c != '-'))
            {
                return false;
            }

            var left = cell.StartsWith(':');
            var right = cell.EndsWith(':');

            alignments.Add(left && right ? MarkdownAlignment.Center
                : right ? MarkdownAlignment.Right
                : MarkdownAlignment.Left);
        }

        return alignments.Count > 0;
    }

    /// <summary>As células de uma linha da tabela. "\|" é uma barra dentro da célula.</summary>
    private static List<string> SplitRow(string line)
    {
        var row = line.Trim();

        if (row.StartsWith('|'))
        {
            row = row[1..];
        }

        if (row.EndsWith('|') && !row.EndsWith("\\|", StringComparison.Ordinal))
        {
            row = row[..^1];
        }

        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;

        for (var index = 0; index < row.Length; index++)
        {
            var character = row[index];

            if (character == '\\' && index + 1 < row.Length && row[index + 1] == '|')
            {
                cell.Append('|');
                index++;
                continue;
            }

            if (character == '`')
            {
                inCode = !inCode;
            }

            if (character == '|' && !inCode)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }

            cell.Append(character);
        }

        cells.Add(cell.ToString().Trim());
        return cells;
    }
}
