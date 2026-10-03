using MyTaskApp.Domain.External;

namespace MyTaskApp.Domain.Tests.External;

public class IssueKeyTests
{
    [Theory]
    [InlineData("GAECO-1234", "GAECO", 1234)]
    [InlineData("ECO-981", "ECO", 981)]
    [InlineData("PROJ-42", "PROJ", 42)]
    [InlineData("A1_B-7", "A1_B", 7)]
    [InlineData("  gaeco-1234  ", "GAECO", 1234)]
    public void TryParse_RecognisesAnIssueKey(string text, string project, long number)
    {
        IssueKey.TryParse(text, out var key).Should().BeTrue();

        key.Project.Should().Be(project);
        key.Number.Should().Be(number);
        key.ToString().Should().Be($"{project}-{number}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corrigir erro")]
    [InlineData("GAECO")]
    [InlineData("GAECO-")]
    [InlineData("-1234")]
    [InlineData("1GAECO-12")]
    [InlineData("GAECO-0")]
    [InlineData("GAECO-012")]
    [InlineData("GAECO-12a")]
    [InlineData("GAECO 1234")]
    [InlineData("GAECO-1234 corrigir")]
    [InlineData("GAECO-12345678901")]
    public void TryParse_RefusesWhatIsNotAKey(string? text)
    {
        IssueKey.TryParse(text, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("GAECO-1234 Corrigir erro", "GAECO-1234", "Corrigir erro")]
    [InlineData("GAECO-1234 - Corrigir erro", "GAECO-1234", "Corrigir erro")]
    [InlineData("GAECO-1234: Corrigir erro", "GAECO-1234", "Corrigir erro")]
    [InlineData("GAECO-1234", "GAECO-1234", "")]
    [InlineData("  gaeco-1234   Corrigir  ", "GAECO-1234", "Corrigir")]
    public void TryParsePrefix_SplitsTheKeyFromTheTitle(string line, string key, string rest)
    {
        IssueKey.TryParsePrefix(line, out var parsed, out var title).Should().BeTrue();

        parsed.ToString().Should().Be(key);
        title.Should().Be(rest);
    }

    [Theory]
    [InlineData("Corrigir GAECO-1234")]
    [InlineData("GAECO-1234x Corrigir")]
    [InlineData("comprar pão")]
    public void TryParsePrefix_OnlyLooksAtTheStartOfTheLine(string line)
    {
        IssueKey.TryParsePrefix(line, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void KeysCompareIgnoringCase()
    {
        IssueKey.TryParse("gaeco-1", out var lower);
        IssueKey.TryParse("GAECO-1", out var upper);

        lower.Should().Be(upper);
    }
}
