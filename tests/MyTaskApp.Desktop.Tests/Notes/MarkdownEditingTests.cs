using MyTaskApp.Desktop.Notes;

namespace MyTaskApp.Desktop.Tests.Notes;

/// <summary>
/// Os botões da barra de formatação. Aritmética de índice é o tipo de coisa que
/// erra em silêncio: o texto sai certo, o cursor pula três caracteres para o
/// lado e ninguém descobre até digitar de novo.
/// </summary>
public class MarkdownEditingTests
{
    private static MarkdownEdit Apply(
        string text,
        int start,
        int length,
        MarkdownCommand command) =>
        MarkdownEditing.Apply(text, start, length, command);

    // ------------------------------------------------------------------
    // Trecho
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(MarkdownCommand.Bold, "**texto**")]
    [InlineData(MarkdownCommand.Italic, "*texto*")]
    [InlineData(MarkdownCommand.Underline, "__texto__")]
    [InlineData(MarkdownCommand.Strikethrough, "~~texto~~")]
    public void ASelection_IsWrappedByTheMarker(MarkdownCommand command, string expected)
    {
        Apply("texto", 0, 5, command).Text.Should().Be(expected);
    }

    /// <summary>
    /// O trecho continua selecionado depois do botão: é o que permite clicar em
    /// B e em I em seguida sem reselecionar nada.
    /// </summary>
    [Fact]
    public void AfterWrapping_TheSameWordsAreStillSelected()
    {
        var edit = Apply("um texto aqui", 3, 5, MarkdownCommand.Bold);

        edit.Text.Should().Be("um **texto** aqui");
        edit.Text.Substring(edit.SelectionStart, edit.SelectionLength).Should().Be("texto");
    }

    /// <summary>
    /// Sem seleção o botão abre o par e põe o cursor no meio — o usuário
    /// simplesmente continua digitando, já formatado.
    /// </summary>
    [Fact]
    public void WithNoSelection_TheMarkerOpensAndTheCaretLandsInside()
    {
        var edit = Apply("", 0, 0, MarkdownCommand.Bold);

        edit.Text.Should().Be("****");
        edit.SelectionStart.Should().Be(2);
        edit.SelectionLength.Should().Be(0);
    }

    [Fact]
    public void ClickingTwice_TakesTheFormattingBackOff()
    {
        var wrapped = Apply("texto", 0, 5, MarkdownCommand.Bold);

        var undone = Apply(
            wrapped.Text,
            wrapped.SelectionStart,
            wrapped.SelectionLength,
            MarkdownCommand.Bold);

        undone.Text.Should().Be("texto");
        undone.SelectionStart.Should().Be(0);
        undone.SelectionLength.Should().Be(5);
    }

    /// <summary>
    /// Clicar em <b>B</b> e depois em <b>I</b> soma os dois traços. A armadilha
    /// que isto guarda: o <c>*</c> colado na seleção de <c>**texto**</c> é
    /// metade do marcador de negrito, e compará-lo caractere a caractere faria
    /// o botão de itálico <i>desfazer</i> o de negrito.
    /// </summary>
    [Fact]
    public void BoldThenItalic_AddsUpInsteadOfUndoingTheBold()
    {
        var bold = Apply("texto", 0, 5, MarkdownCommand.Bold);

        var both = Apply(
            bold.Text,
            bold.SelectionStart,
            bold.SelectionLength,
            MarkdownCommand.Italic);

        both.Text.Should().Be("***texto***");

        MarkdownDocument.ParseInlines(both.Text).Single().Style
            .Should().Be(MarkdownStyle.Bold | MarkdownStyle.Italic);
    }

    /// <summary>E o caminho de volta tira um traço só, não os dois.</summary>
    [Fact]
    public void TakingTheItalicBackOff_LeavesTheBoldStanding()
    {
        Apply("***texto***", 3, 5, MarkdownCommand.Italic).Text.Should().Be("**texto**");
    }

    [Fact]
    public void TakingTheBoldBackOff_LeavesTheItalicStanding()
    {
        Apply("***texto***", 3, 5, MarkdownCommand.Bold).Text.Should().Be("*texto*");
    }

    /// <summary>
    /// A seleção pode pegar os marcadores junto — é o que acontece ao dar um
    /// duplo clique arrastando. Tirar também tem de funcionar por fora.
    /// </summary>
    [Fact]
    public void ASelectionThatIncludesTheMarkers_IsUnwrappedToo()
    {
        var edit = Apply("**texto**", 0, 9, MarkdownCommand.Bold);

        edit.Text.Should().Be("texto");
        edit.SelectionLength.Should().Be(5);
    }

    /// <summary>
    /// Arrastar da direita para a esquerda entrega comprimento negativo, e um
    /// desfazer pode deixar a seleção apontando além do texto. Nenhum dos dois
    /// pode virar exceção na cara do usuário.
    /// </summary>
    [Fact]
    public void ABackwardsOrStaleSelection_IsNormalisedInsteadOfThrowing()
    {
        Apply("texto", 5, -5, MarkdownCommand.Bold).Text.Should().Be("**texto**");
        Apply("texto", 99, 40, MarkdownCommand.Bold).Text.Should().Be("texto****");
    }

    [Fact]
    public void ANullText_IsTreatedAsEmpty()
    {
        MarkdownEditing.Apply(null, 0, 0, MarkdownCommand.Italic).Text.Should().Be("**");
    }

    // ------------------------------------------------------------------
    // Linha
    // ------------------------------------------------------------------

    [Fact]
    public void ABulletList_MarksEverySelectedLine()
    {
        Apply("um\ndois", 0, 7, MarkdownCommand.Bullet).Text.Should().Be("- um\n- dois");
    }

    [Fact]
    public void ANumberedList_CountsFromOne()
    {
        Apply("um\ndois\ntrês", 0, 12, MarkdownCommand.Numbered).Text
            .Should().Be("1. um\n2. dois\n3. três");
    }

    [Fact]
    public void ClickingTheSameListButtonAgain_TakesTheMarkersOff()
    {
        Apply("- um\n- dois", 0, 11, MarkdownCommand.Bullet).Text.Should().Be("um\ndois");
    }

    /// <summary>
    /// Trocar de marcador é troca, não soma: deixar "# " e "- " na mesma linha
    /// renderizaria um dos dois como texto.
    /// </summary>
    [Fact]
    public void TurningAHeadingIntoAList_ReplacesTheMarker()
    {
        Apply("# Compras", 0, 9, MarkdownCommand.Bullet).Text.Should().Be("- Compras");
    }

    /// <summary>
    /// Só parte das linhas marcada quer dizer "complete o resto", e não
    /// "desmarque tudo" — é o que o usuário quis ao selecionar um bloco misto.
    /// </summary>
    [Fact]
    public void AMixedSelection_IsCompletedInsteadOfCleared()
    {
        Apply("- um\ndois", 0, 9, MarkdownCommand.Bullet).Text.Should().Be("- um\n- dois");
    }

    /// <summary>
    /// Linha em branco nunca recebe marcador, e também não impede o desfazer —
    /// uma lista com um respiro no meio continua podendo ser desmarcada.
    /// </summary>
    [Fact]
    public void ABlankLineInTheMiddle_NeitherGetsAMarkerNorBlocksTheUndo()
    {
        var marked = Apply("um\n\ndois", 0, 8, MarkdownCommand.Bullet);

        marked.Text.Should().Be("- um\n\n- dois");

        Apply(marked.Text, 0, marked.Text.Length, MarkdownCommand.Bullet).Text
            .Should().Be("um\n\ndois");
    }

    /// <summary>Sem seleção, o botão de linha vale para a linha do cursor.</summary>
    [Fact]
    public void WithNoSelection_ALineButtonMarksTheLineTheCaretIsOn()
    {
        Apply("um\ndois", 4, 0, MarkdownCommand.Bullet).Text.Should().Be("um\n- dois");
    }

    [Fact]
    public void ALineButton_LeavesTheRestOfTheNoteAlone()
    {
        Apply("antes\nalvo\ndepois", 6, 4, MarkdownCommand.Heading2).Text
            .Should().Be("antes\n## alvo\ndepois");
    }

    /// <summary>
    /// O que a barra escreve, o leitor tem de conseguir ler de volta. Sem isto
    /// os dois lados poderiam divergir sem nenhum teste reclamar.
    /// </summary>
    [Theory]
    [InlineData(MarkdownCommand.Heading1, MarkdownBlockKind.Heading1)]
    [InlineData(MarkdownCommand.Heading2, MarkdownBlockKind.Heading2)]
    [InlineData(MarkdownCommand.Bullet, MarkdownBlockKind.Bullet)]
    [InlineData(MarkdownCommand.Numbered, MarkdownBlockKind.Numbered)]
    public void WhatTheToolbarWrites_TheReaderReadsBack(
        MarkdownCommand command,
        MarkdownBlockKind kind)
    {
        var edit = Apply("assunto", 0, 7, command);

        var block = MarkdownDocument.Parse(edit.Text).Single();

        block.Kind.Should().Be(kind);
        block.Text.Should().Be("assunto");
    }

    [Theory]
    [InlineData(MarkdownCommand.Bold, MarkdownStyle.Bold)]
    [InlineData(MarkdownCommand.Italic, MarkdownStyle.Italic)]
    [InlineData(MarkdownCommand.Underline, MarkdownStyle.Underline)]
    [InlineData(MarkdownCommand.Strikethrough, MarkdownStyle.Strikethrough)]
    public void WhatTheToolbarStyles_TheReaderStylesBack(
        MarkdownCommand command,
        MarkdownStyle style)
    {
        var edit = Apply("assunto", 0, 7, command);

        MarkdownDocument.ParseInlines(edit.Text).Single().Style.Should().Be(style);
    }
}
