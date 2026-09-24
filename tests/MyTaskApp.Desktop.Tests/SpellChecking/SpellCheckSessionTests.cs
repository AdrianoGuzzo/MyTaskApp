using MyTaskApp.Desktop.SpellChecking;

namespace MyTaskApp.Desktop.Tests.SpellChecking;

/// <summary>
/// Cache e palavra em digitação: as duas regras que, erradas, só aparecem como
/// sublinhado no lugar errado ou piscando a cada tecla.
/// </summary>
public class SpellCheckSessionTests
{
    [Fact]
    public void Check_ReturnsTheMisspelledWords_WithTheirPosition()
    {
        var session = new SpellCheckSession(new FakeSpellChecker("aplicativoo"));

        var misspelled = session.Check("Estou testando este aplicativoo agora");

        misspelled.Should().ContainSingle()
            .Which.Should().Be(new WordSpan(20, 11, "aplicativoo"));
        session.Misspelled.Should().BeEquivalentTo(misspelled);
    }

    [Fact]
    public void Check_AsksTheSystemOncePerWord()
    {
        var checker = new FakeSpellChecker("errro");
        var session = new SpellCheckSession(checker);

        session.Check("errro e errro");
        session.Check("errro e errro de novo");

        checker.Asked.Should().Equal("errro", "de", "novo");
    }

    [Fact]
    public void CachedOnly_TreatsNewWordsAsCorrect_WithoutAsking()
    {
        var checker = new FakeSpellChecker("errro", "outroo");
        var session = new SpellCheckSession(checker);
        session.Check("errro");
        checker.Asked.Clear();

        var misspelled = session.Check("mais errro outroo", cachedOnly: true);

        misspelled.Select(word => word.Text).Should().Equal("errro");
        checker.Asked.Should().BeEmpty();
    }

    [Theory]
    [InlineData(11)] // logo depois da última letra: onde o cursor fica ao digitar
    [InlineData(8)]
    [InlineData(6)]
    public void TheWordBeingTyped_IsNotMarked(int caret)
    {
        var session = new SpellCheckSession(new FakeSpellChecker("aplic", "errro"));

        var misspelled = session.Check("errro aplic", editingAt: caret);

        misspelled.Select(word => word.Text).Should().Equal("errro");
    }

    [Fact]
    public void AfterTheSpace_TheWordIsJudged()
    {
        var session = new SpellCheckSession(new FakeSpellChecker("aplic"));

        session.Check("aplic ", editingAt: 6).Select(word => word.Text).Should().Equal("aplic");
    }

    [Fact]
    public void DeveloperTerms_AreCorrect_EvenWithoutTheEnglishDictionary()
    {
        var checker = new FakeSpellChecker("branch", "Deploy");
        var session = new SpellCheckSession(checker);

        session.Check("Deploy da branch nova").Should().BeEmpty();
        checker.Asked.Should().Equal("da", "nova");
    }

    [Fact]
    public void MisspelledAt_FindsTheWordUnderTheIndex()
    {
        var session = new SpellCheckSession(new FakeSpellChecker("errro"));
        session.Check("um errro aqui");

        session.MisspelledAt(3)!.Value.Text.Should().Be("errro");
        session.MisspelledAt(8)!.Value.Text.Should().Be("errro");
        session.MisspelledAt(1).Should().BeNull();
        session.MisspelledAt(10).Should().BeNull();
    }

    [Fact]
    public void Invalidate_ForgetsTheCache()
    {
        var checker = new FakeSpellChecker("errro");
        var session = new SpellCheckSession(checker);
        session.Check("errro");

        checker.Ignore("errro");
        session.Check("errro").Should().ContainSingle("o cache ainda responde");

        session.Invalidate();
        session.Check("errro").Should().BeEmpty();
    }
}
