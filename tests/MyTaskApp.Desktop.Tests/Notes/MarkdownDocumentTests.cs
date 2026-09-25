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
    // O que o preview do VS Code mostra
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("### Três", MarkdownBlockKind.Heading3)]
    [InlineData("#### Quatro", MarkdownBlockKind.Heading4)]
    [InlineData("##### Cinco", MarkdownBlockKind.Heading5)]
    [InlineData("###### Seis", MarkdownBlockKind.Heading6)]
    public void EveryHeadingLevel_IsRecognised(string line, MarkdownBlockKind kind)
    {
        MarkdownDocument.Parse(line).Single().Kind.Should().Be(kind);
    }

    [Theory]
    [InlineData("####### sete")]
    [InlineData("#hashtag")]
    public void WhatIsNotAHeading_StaysAParagraph(string line)
    {
        MarkdownDocument.Parse(line).Single().Kind.Should().Be(MarkdownBlockKind.Paragraph);
    }

    [Fact]
    public void TheClosingHashesOfAHeading_AreDecoration()
    {
        MarkdownDocument.Parse("## Título ##").Single().Text.Should().Be("Título");
    }

    [Theory]
    [InlineData("Título\n===", MarkdownBlockKind.Heading1)]
    [InlineData("Título\n---", MarkdownBlockKind.Heading2)]
    public void AnUnderlinedLine_IsAHeading(string text, MarkdownBlockKind kind)
    {
        var block = MarkdownDocument.Parse(text).Single();

        block.Kind.Should().Be(kind);
        block.Text.Should().Be("Título");
    }

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    [InlineData("- - -")]
    public void ThreeDashesAlone_AreAHorizontalRule(string line)
    {
        MarkdownDocument.Parse(line).Single().Kind.Should().Be(MarkdownBlockKind.Rule);
    }

    /// <summary>
    /// O caso que o leitor antigo desenhava torto: o recuo era jogado fora e a
    /// sublista virava parte da lista de cima.
    /// </summary>
    [Fact]
    public void AnIndentedItem_IsNestedInsideThePreviousOne()
    {
        var blocks = MarkdownDocument.Parse("- fruta\n  - maçã\n    - verde\n- legume");

        blocks.Select(block => block.Level).Should().Equal(0, 1, 2, 0);
    }

    [Fact]
    public void NestedNumberedLists_CountOnTheirOwn()
    {
        var blocks = MarkdownDocument.Parse("1. um\n   1. um.um\n   2. um.dois\n2. dois");

        blocks.Select(block => (block.Level, block.Ordinal))
            .Should().Equal((0, 1), (1, 1), (1, 2), (0, 2));
    }

    [Fact]
    public void ANumberedList_StartsWhereTheFirstItemSays()
    {
        MarkdownDocument.Parse("3. três\n1. quatro")
            .Select(block => block.Ordinal)
            .Should().Equal(3, 4);
    }

    [Fact]
    public void ALineRightAfterAnItem_ContinuesTheItem()
    {
        var block = MarkdownDocument.Parse("- item\n  continua aqui").Single();

        block.Kind.Should().Be(MarkdownBlockKind.Bullet);
        block.Text.Should().Be("item\ncontinua aqui");
    }

    [Theory]
    [InlineData("- [ ] pendente", false, "pendente")]
    [InlineData("- [x] feito", true, "feito")]
    [InlineData("* [X] feito", true, "feito")]
    public void ACheckbox_MakesATaskItem(string line, bool isChecked, string text)
    {
        var block = MarkdownDocument.Parse(line).Single();

        block.Kind.Should().Be(MarkdownBlockKind.Task);
        block.IsChecked.Should().Be(isChecked);
        block.Text.Should().Be(text);
    }

    [Fact]
    public void AFencedBlock_KeepsTheCodeExactlyAsWritten()
    {
        var block = MarkdownDocument.Parse("```csharp\nif (x)\n    **nada**\n```").Single();

        block.Kind.Should().Be(MarkdownBlockKind.Code);
        block.Language.Should().Be("csharp");
        block.Text.Should().Be("if (x)\n    **nada**");
    }

    [Fact]
    public void AFenceWithoutEnd_RunsToTheEndOfTheNote()
    {
        var block = MarkdownDocument.Parse("~~~\n# não é título").Single();

        block.Kind.Should().Be(MarkdownBlockKind.Code);
        block.Text.Should().Be("# não é título");
    }

    [Fact]
    public void AQuote_IsReadAsADocumentOfItsOwn()
    {
        var quote = MarkdownDocument.Parse("> # Aviso\n> - um\n> - dois").Single();

        quote.Kind.Should().Be(MarkdownBlockKind.Quote);
        quote.Children.Select(block => block.Kind)
            .Should().Equal(MarkdownBlockKind.Heading1, MarkdownBlockKind.Bullet, MarkdownBlockKind.Bullet);
    }

    [Fact]
    public void ATable_KeepsItsCellsAndAlignment()
    {
        var table = MarkdownDocument.Parse(
            "| Nome | Qtd | Nota |\n| :--- | ---: | :---: |\n| leite | 2 | a \\| b |\n| pão |").Single();

        table.Kind.Should().Be(MarkdownBlockKind.Table);
        table.Alignments.Should().Equal(MarkdownAlignment.Left, MarkdownAlignment.Right, MarkdownAlignment.Center);
        table.Rows.Should().HaveCount(3);
        table.Rows[0].Should().Equal("Nome", "Qtd", "Nota");
        table.Rows[1].Should().Equal("leite", "2", "a | b");

        // Linha curta completa com vazio: a grade fica retangular.
        table.Rows[2].Should().Equal("pão", "", "");
    }

    [Fact]
    public void APipeWithoutASeparatorLine_IsJustText()
    {
        MarkdownDocument.Parse("a | b\nc | d").Single().Kind.Should().Be(MarkdownBlockKind.Paragraph);
    }

    [Fact]
    public void InlineCode_IsNotFormatted()
    {
        MarkdownDocument.ParseInlines("rode `git **status**` agora")
            .Select(span => (span.Text, span.Style))
            .Should().Equal(
                ("rode ", MarkdownStyle.None),
                ("git **status**", MarkdownStyle.Code),
                (" agora", MarkdownStyle.None));
    }

    [Fact]
    public void ALink_ShowsItsLabelAndCarriesItsTarget()
    {
        var spans = MarkdownDocument.ParseInlines("veja [a **doc**](https://exemplo.com \"título\") já");

        spans.Should().Contain(span => span.Text == "a " && span.Url == "https://exemplo.com");
        spans.Should().Contain(span =>
            span.Text == "doc" && span.Style == MarkdownStyle.Bold && span.Url == "https://exemplo.com");
        string.Concat(spans.Select(span => span.Text)).Should().Be("veja a doc já");
    }

    [Theory]
    [InlineData("abra https://exemplo.com/a_b.", "https://exemplo.com/a_b", "https://exemplo.com/a_b")]
    [InlineData("abra <https://exemplo.com>", "https://exemplo.com", "https://exemplo.com")]
    [InlineData("abra www.exemplo.com", "www.exemplo.com", "https://www.exemplo.com")]
    [InlineData("(ver https://pt.wikipedia.org/wiki/X_(Y))", "https://pt.wikipedia.org/wiki/X_(Y)", "https://pt.wikipedia.org/wiki/X_(Y)")]
    public void ABareAddress_BecomesALink(string text, string shown, string target)
    {
        MarkdownDocument.ParseInlines(text)
            .Should().ContainSingle(span => span.Url != null)
            .Which.Should().Be(new MarkdownSpan(shown, MarkdownStyle.None, target));
    }

    [Fact]
    public void AnUnderscorePair_IsItalic()
    {
        MarkdownDocument.ParseInlines("_leve_").Single().Style.Should().Be(MarkdownStyle.Italic);
    }

    /// <summary>Nome de variável e de pasta não é itálico.</summary>
    [Theory]
    [InlineData("nome_de_variavel")]
    [InlineData(@"C:\Projetos\meu_app_novo")]
    public void AnUnderscoreInsideAWord_StaysLiteral(string text)
    {
        MarkdownDocument.ParseInlines(text).Single().Should().Be(new MarkdownSpan(text, MarkdownStyle.None));
    }

    [Theory]
    [InlineData("2 * 3 * 4")]
    [InlineData("a ** b ** c")]
    public void AMarkerNextToASpace_IsNotEmphasis(string text)
    {
        MarkdownDocument.ParseInlines(text).Single().Style.Should().Be(MarkdownStyle.None);
    }

    [Fact]
    public void ABackslash_EscapesTheMarker()
    {
        MarkdownDocument.ParseInlines(@"\*não é itálico\*").Single()
            .Should().Be(new MarkdownSpan("*não é itálico*", MarkdownStyle.None));
    }

    /// <summary>O limite que o ADR-024 aceitava: a ênfase de dentro encostada na de fora.</summary>
    [Fact]
    public void NestedEmphasis_TouchingTheOuterMarker_IsResolved()
    {
        MarkdownDocument.ParseInlines("**muito *mesmo***")
            .Select(span => (span.Text, span.Style))
            .Should().Equal(
                ("muito ", MarkdownStyle.Bold),
                ("mesmo", MarkdownStyle.Bold | MarkdownStyle.Italic));
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
    public void PlainText_ReadsInsideQuotesTablesAndCode()
    {
        MarkdownDocument.ToPlainText("> citado\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n---\n\n```\nx = 1\n```")
            .Should().Be("citado a b 1 2 x = 1");
    }

    [Fact]
    public void PlainText_OfNothing_IsEmpty()
    {
        MarkdownDocument.ToPlainText(null).Should().BeEmpty();
    }
}
