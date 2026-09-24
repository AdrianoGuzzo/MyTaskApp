using MyTaskApp.Desktop.SpellChecking;

namespace MyTaskApp.Desktop.Tests.SpellChecking;

/// <summary>
/// O que chega ao corretor. O custo de errar aqui é o sublinhado em tudo que
/// não é prosa — e aí o usuário aprende a não olhar para ele.
/// </summary>
public class SpellingTokenizerTests
{
    private static string[] Words(string text) =>
        SpellingTokenizer.Words(text).Select(word => word.Text).ToArray();

    [Fact]
    public void Prose_ComesOutWordByWord_WithAccentsAndOffsets()
    {
        var words = SpellingTokenizer.Words("Estou testando a revisão, então.");

        words.Select(word => word.Text).Should().Equal("Estou", "testando", "revisão", "então");
        words[2].Start.Should().Be(17);
        words[2].Length.Should().Be(7);
    }

    [Fact]
    public void HyphenAndApostrophe_BetweenLetters_KeepTheWordWhole()
    {
        Words("guarda-chuva d'água - fim").Should().Equal("guarda-chuva", "d'água", "fim");
    }

    [Fact]
    public void MarkdownPunctuation_IsNotPartOfTheWord()
    {
        Words("**negrito** _itálico_ ~~riscado~~ (parêntese)").Should()
            .Equal("negrito", "itálico", "riscado", "parêntese");
    }

    [Fact]
    public void InlineCode_IsSkipped()
    {
        Words("rode `dotnet tesst` antes").Should().Equal("rode", "antes");
    }

    [Fact]
    public void UnpairedBacktick_DoesNotSwallowTheRestOfTheLine()
    {
        Words("meio `digitado aqui").Should().Equal("meio", "digitado", "aqui");
    }

    [Fact]
    public void FencedBlock_IsSkipped_UntilItCloses()
    {
        Words("antes\n```bash\ngit comit\n```\ndepois").Should().Equal("antes", "depois");
    }

    [Theory]
    [InlineData("https://github.com/foo/barr")]
    [InlineData("www.exemplo.com")]
    [InlineData("fulano@empresa.com")]
    [InlineData("@ecossistema-core")]
    [InlineData("#urgente")]
    [InlineData(@"C:\Projetos\MyTaskApp")]
    [InlineData("src/MyTaskApp.Desktop")]
    [InlineData("feature/nova-tela")]
    public void NotProse_IsSkipped(string chunk)
    {
        Words($"veja {chunk} depois").Should().Equal("veja", "depois");
    }

    [Theory]
    [InlineData("v2")]
    [InlineData("x86")]
    [InlineData("snake_case")]
    [InlineData("API")]
    [InlineData("TaskItem")]
    [InlineData("iPhone")]
    [InlineData("a")]
    public void CodeLikeWords_AreSkipped(string word)
    {
        Words($"veja {word} depois").Should().Equal("veja", "depois");
    }

    [Fact]
    public void Heading_IsCheckedAfterTheHashes()
    {
        Words("## Título da seção").Should().Equal("Título", "da", "seção");
    }

    [Fact]
    public void Empty_HasNoWords()
    {
        SpellingTokenizer.Words(null).Should().BeEmpty();
        SpellingTokenizer.Words(string.Empty).Should().BeEmpty();
    }
}
