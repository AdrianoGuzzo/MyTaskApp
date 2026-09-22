using MyTaskApp.Desktop.Notes;

namespace MyTaskApp.Desktop.Tests.Notes;

/// <summary>
/// O subconjunto de Markdown das anotações. Vale teste denso e sem UI porque é
/// a única parte da tela que tem regra de verdade — e porque um parser errado
/// não quebra: ele desenha o texto do usuário torto, em silêncio.
/// </summary>
public class MarkdownDocumentTests
{
    // ------------------------------------------------------------------
    // Blocos
    // ------------------------------------------------------------------

    [Fact]
    public void AnEmptyNote_HasNoBlocks()
    {
        MarkdownDocument.Parse(null).Should().BeEmpty();
        MarkdownDocument.Parse("   \n  \n").Should().BeEmpty();
    }

    [Theory]
    [InlineData("# Compras", MarkdownBlockKind.Heading1, "Compras")]
    [InlineData("## Onde", MarkdownBlockKind.Heading2, "Onde")]
    [InlineData("- leite", MarkdownBlockKind.Bullet, "leite")]
    [InlineData("* leite", MarkdownBlockKind.Bullet, "leite")]
    [InlineData("1. leite", MarkdownBlockKind.Numbered, "leite")]
    [InlineData("2) leite", MarkdownBlockKind.Numbered, "leite")]
    [InlineData("leite", MarkdownBlockKind.Paragraph, "leite")]
    public void EachMarker_NamesTheBlockAndDisappears(
        string line,
        MarkdownBlockKind kind,
        string text)
    {
        var block = MarkdownDocument.Parse(line).Single();

        block.Kind.Should().Be(kind);
        block.Text.Should().Be(text);
    }

    /// <summary>
    /// "## " tem de ser testado antes de "# ", senão todo subtítulo vira um
    /// título cujo texto começa com "# ".
    /// </summary>
    [Fact]
    public void ASubheading_IsNotAHeadingWithAStrayHash()
    {
        var block = MarkdownDocument.Parse("## Detalhes").Single();

        block.Kind.Should().Be(MarkdownBlockKind.Heading2);
        block.Text.Should().NotStartWith("#");
    }

    /// <summary>
    /// Quem escreve "1." três vezes quer três itens, não três itens número um —
    /// é o que todo editor de texto faz ao renumerar sozinho.
    /// </summary>
    [Fact]
    public void ANumberedList_IsRenumberedFromOne()
    {
        var blocks = MarkdownDocument.Parse("1. um\n1. dois\n1. três");

        blocks.Select(block => block.Ordinal).Should().Equal(1, 2, 3);
    }

    /// <summary>Uma segunda lista recomeça: a contagem não atravessa o texto.</summary>
    [Fact]
    public void ASecondList_StartsCountingAgain()
    {
        var blocks = MarkdownDocument.Parse("1. um\n1. dois\n\ntexto\n\n1. outro");

        blocks.Where(block => block.Kind == MarkdownBlockKind.Numbered)
            .Select(block => block.Ordinal)
            .Should().Equal(1, 2, 1);
    }

    /// <summary>
    /// Linhas seguidas são um parágrafo só. Sem isto, uma anotação digitada sem
    /// linha em branco viraria um bloco por linha, com o espaçamento de bloco
    /// entre cada uma — uma escada no lugar de um texto.
    /// </summary>
    [Fact]
    public void ConsecutiveLines_BecomeOneParagraph()
    {
        var blocks = MarkdownDocument.Parse("primeira\nsegunda\n\nterceira");

        blocks.Should().HaveCount(2);
        blocks[0].Text.Should().Be("primeira\nsegunda");
        blocks[1].Text.Should().Be("terceira");
    }

    [Fact]
    public void WindowsLineEndings_ReadTheSameAsUnixOnes()
    {
        MarkdownDocument.Parse("# Título\r\n- item")
            .Select(block => block.Kind)
            .Should().Equal(MarkdownBlockKind.Heading1, MarkdownBlockKind.Bullet);
    }

    // ------------------------------------------------------------------
    // Trechos
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("**forte**", MarkdownStyle.Bold, "forte")]
    [InlineData("*leve*", MarkdownStyle.Italic, "leve")]
    [InlineData("__sob__", MarkdownStyle.Underline, "sob")]
    [InlineData("~~fora~~", MarkdownStyle.Strikethrough, "fora")]
    public void EachInlineMarker_StylesWhatIsBetweenThem(
        string text,
        MarkdownStyle style,
        string content)
    {
        var span = MarkdownDocument.ParseInlines(text).Single();

        span.Text.Should().Be(content);
        span.Style.Should().Be(style);
    }

    [Fact]
    public void NestedMarkers_Accumulate()
    {
        var span = MarkdownDocument.ParseInlines("**forte com *leve* dentro**")
            .Single(candidate => candidate.Text == "leve");

        span.Style.Should().Be(MarkdownStyle.Bold | MarkdownStyle.Italic);
    }

    /// <summary>
    /// Três asteriscos são os dois traços de uma vez — e não um par de fora com
    /// um asterisco solto sobrando dentro. Importa porque é exatamente o que a
    /// barra escreve quando alguém clica em <b>B</b> e depois em <b>I</b>.
    /// </summary>
    [Fact]
    public void ATripleMarker_MeansBoldAndItalicAtOnce()
    {
        var span = MarkdownDocument.ParseInlines("***tudo***").Single();

        span.Text.Should().Be("tudo");
        span.Style.Should().Be(MarkdownStyle.Bold | MarkdownStyle.Italic);
    }

    /// <summary>
    /// A armadilha do parser ingênuo: com "*" tentado antes de "**", todo
    /// negrito vira um itálico vazio seguido de lixo.
    /// </summary>
    [Fact]
    public void Bold_IsNotReadAsTwoEmptyItalics()
    {
        var spans = MarkdownDocument.ParseInlines("**forte**");

        spans.Should().ContainSingle();
        spans[0].Style.Should().Be(MarkdownStyle.Bold);
    }

    /// <summary>
    /// Ninguém pediu itálico ao escrever uma multiplicação. Marcador sem par
    /// volta a ser texto — é o que impede a anotação de ser reescrita pelo
    /// editor sem o usuário ter feito nada.
    /// </summary>
    [Theory]
    [InlineData("2 * 3 = 6")]
    [InlineData("um asterisco * solto")]
    [InlineData("**sem fechar")]
    public void AnUnpairedMarker_StaysLiteral(string text)
    {
        var rebuilt = string.Concat(MarkdownDocument.ParseInlines(text).Select(span => span.Text));

        rebuilt.Should().Be(text);
    }

    [Fact]
    public void AnEmptyPair_IsLiteralToo()
    {
        MarkdownDocument.ParseInlines("****").Single().Text.Should().Be("****");
    }

    [Fact]
    public void PlainTextAroundAMarker_KeepsItsPlace()
    {
        MarkdownDocument.ParseInlines("antes **meio** depois")
            .Select(span => (span.Text, span.Style))
            .Should().Equal(
                ("antes ", MarkdownStyle.None),
                ("meio", MarkdownStyle.Bold),
                (" depois", MarkdownStyle.None));
    }

    // ------------------------------------------------------------------
    // Texto cru
    // ------------------------------------------------------------------

    [Fact]
    public void PlainText_DropsEveryMarker()
    {
        MarkdownDocument.ToPlainText("# Compras\n- **leite** integral")
            .Should().Be("Compras leite integral");
    }

    [Fact]
    public void PlainText_OfNothing_IsEmpty()
    {
        MarkdownDocument.ToPlainText(null).Should().BeEmpty();
    }
}
