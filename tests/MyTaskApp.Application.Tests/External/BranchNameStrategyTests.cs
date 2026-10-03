using MyTaskApp.Application.External;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Tests.External;

public class BranchNameStrategyTests
{
    private static string Branch(string? type, string id = "GAECO-1234", BranchConventions? conventions = null) =>
        new ConventionBranchNameStrategy(conventions ?? BranchConventions.Default)
            .GenerateBranchName(FakeExternalTasks.Issue(id, "Corrigir erro de sincronização", type!));

    [Theory]
    [InlineData("Bug", "bug/GAECO-1234")]
    [InlineData("Story", "feature/GAECO-1234")]
    [InlineData("Task", "task/GAECO-1234")]
    [InlineData("Improvement", "improvement/GAECO-1234")]
    [InlineData("Hotfix", "hotfix/GAECO-1234")]
    public void TheDefaultMapping_FollowsTheIssueType(string type, string expected)
    {
        Branch(type).Should().Be(expected);
    }

    [Theory]
    [InlineData("História", "feature/GAECO-1234")]
    [InlineData("Historia", "feature/GAECO-1234")]
    [InlineData("Tarefa", "task/GAECO-1234")]
    [InlineData("Subtarefa", "task/GAECO-1234")]
    [InlineData("Sub-task", "task/GAECO-1234")]
    [InlineData("Melhoria", "improvement/GAECO-1234")]
    [InlineData("Epic", "feature/GAECO-1234")]
    [InlineData("Épico", "feature/GAECO-1234")]
    public void AJiraInPortuguese_MapsToTheSameConventions(string type, string expected)
    {
        // A API devolve o nome do tipo na língua do usuário do Jira.
        Branch(type).Should().Be(expected);
    }

    [Fact]
    public void TheTypeIsMatchedIgnoringCase()
    {
        Branch("BUG").Should().Be("bug/GAECO-1234");
    }

    [Fact]
    public void AnUnknownType_BecomesItsOwnPrefix()
    {
        Branch("Spike de Pesquisa").Should().Be("spike-de-pesquisa/GAECO-1234");
    }

    [Fact]
    public void WithoutAType_TheIssueIsATask()
    {
        Branch(null).Should().Be("task/GAECO-1234");
    }

    [Fact]
    public void CustomConventions_Win()
    {
        var conventions = BranchConventions.Parse("Bug = fix/{id}\nStory = feat/{id}");

        Branch("Bug", conventions: conventions).Should().Be("fix/GAECO-1234");
        Branch("História", conventions: conventions).Should().Be("feat/GAECO-1234");
        Branch("Task", conventions: conventions).Should().Be("task/GAECO-1234");
    }

    [Fact]
    public void AnExplicitLocalizedType_WinsOverTheAlias()
    {
        var conventions = BranchConventions.Parse("História = historia/{id}");

        Branch("História", conventions: conventions).Should().Be("historia/GAECO-1234");
    }

    [Fact]
    public void TheSlugPlaceholder_AddsTheTitle()
    {
        var conventions = BranchConventions.Parse("Bug = bug/{id}-{slug}");

        Branch("Bug", conventions: conventions).Should().Be("bug/GAECO-1234-corrigir-erro-de-sincronizacao");
    }

    [Fact]
    public void Parse_IgnoresBlankLinesAndComments()
    {
        var conventions = BranchConventions.Parse("\n# meu time\n  Bug   =   fix/{id}  \n\n");

        conventions.Patterns.Should().ContainKey("Bug").WhoseValue.Should().Be("fix/{id}");
    }

    [Theory]
    [InlineData("Bug fix/{id}", "linha 1")]
    [InlineData("Bug = fix/", "{id}")]
    [InlineData(" = fix/{id}", "linha 1")]
    [InlineData("Bug = fix/{id}\nBug = bug/{id}", "Bug")]
    [InlineData("Bug = fix me/{id}", "espaços")]
    [InlineData("Bug = fix/{id}/{nada}", "{nada}")]
    public void Parse_RefusesAConventionThatCannotBecomeABranch(string text, string mentions)
    {
        var parse = () => BranchConventions.Parse(text);

        parse.Should().Throw<DomainException>().WithMessage($"*{mentions}*");
    }

    [Fact]
    public void ToText_RoundTrips()
    {
        var text = BranchConventions.Default.ToText();

        BranchConventions.Parse(text).Patterns.Should().BeEquivalentTo(BranchConventions.Default.Patterns);
    }
}
