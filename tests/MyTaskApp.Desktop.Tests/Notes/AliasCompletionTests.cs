using MyTaskApp.Desktop.Notes;

namespace MyTaskApp.Desktop.Tests.Notes;

/// <summary>
/// A aritmética do autocomplete de diretórios (ADR-026): quando o "@" abre a
/// lista, o que o filtro acha e o texto que sobra depois de aceitar.
/// </summary>
public class AliasCompletionTests
{
    private static readonly string[] Aliases =
        ["@ecossistema-core", "@ecossistema-web", "@mytaskapp", "@scripts-eco"];

    private static AliasToken? Find(string textWithCaret)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        return AliasCompletion.FindToken(textWithCaret.Remove(caret, 1), caret);
    }

    private static IReadOnlyList<string> Filter(string query) =>
        AliasCompletion.Filter(Aliases, query, alias => alias, _ => null);

    [Theory]
    [InlineData("@|", 0, "")]
    [InlineData("@eco|", 0, "eco")]
    [InlineData("Verificar o código no @eco|", 22, "eco")]
    [InlineData("linha 1\n@eco|", 8, "eco")]
    [InlineData("(@eco|", 1, "eco")]
    [InlineData("ver @eco|-core", 4, "eco")]
    public void TheAtOpensAtTheStartOrAfterASpace(string text, int start, string query) =>
        Find(text).Should().Be(new AliasToken(start, query));

    [Theory]
    [InlineData("fulano@empresa|")]
    [InlineData("sem arroba|")]
    [InlineData("@eco core|")]
    [InlineData("@@eco|")]
    [InlineData("|@eco")]
    public void NoAtBeforeTheCaret_OrAnEmailsAt_OpensNothing(string text) =>
        Find(text).Should().BeNull();

    [Fact]
    public void AnEmptyQuery_ListsEverything() =>
        Filter("").Should().Equal(Aliases);

    /// <summary>"@eco" acha "@scripts-eco" também: o filtro é por trecho.</summary>
    [Fact]
    public void TheFilter_MatchesAnywhere_WithPrefixMatchesFirst() =>
        Filter("eco").Should().Equal("@ecossistema-core", "@ecossistema-web", "@scripts-eco");

    [Fact]
    public void TheFilter_IgnoresCase() =>
        Filter("MYTASK").Should().Equal("@mytaskapp");

    [Fact]
    public void TheFilter_AlsoLooksAtTheName()
    {
        var found = AliasCompletion.Filter(
            [("@core", "Ecossistema"), ("@web", (string?)null)],
            "ecossis",
            item => item.Item1,
            item => item.Item2);

        found.Should().ContainSingle().Which.Item1.Should().Be("@core");
    }

    [Fact]
    public void Accepting_ReplacesTheAliasWithTheRealPath()
    {
        const string text = "Verificar o código no @eco";

        var edit = AliasCompletion.Accept(text, new AliasToken(22, "eco"), text.Length, @"C:\Projects\ecossistema-core");

        edit.Text.Should().Be(@"Verificar o código no C:\Projects\ecossistema-core");
        edit.Text.Should().NotContain("@");
        edit.CaretIndex.Should().Be(edit.Text.Length);
    }

    [Fact]
    public void Accepting_InTheMiddleOfAnAlias_TakesTheRestOfItAlong()
    {
        const string text = "ver @eco-co e depois";

        var edit = AliasCompletion.Accept(text, new AliasToken(4, "eco"), 8, @"C:\eco");

        edit.Text.Should().Be(@"ver C:\eco e depois");
        edit.CaretIndex.Should().Be(10);
    }
}
