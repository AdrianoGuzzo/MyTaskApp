using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Domain.Tests.Commands;

/// <summary>As variáveis de contexto e a pasta relativa ao worktree (ADR-051).</summary>
public class CommandVariablesTests
{
    [Theory]
    [InlineData("worktree")]
    [InlineData("Worktree.Path")]
    [InlineData("task.title")]
    [InlineData("tag")]
    public void TheContextNames_AreKnown_IgnoringCase(string name) =>
        CommandVariables.IsContextName(name).Should().BeTrue();

    [Theory]
    [InlineData("project")]
    [InlineData("task")]
    [InlineData(null)]
    public void OtherNames_AreUserParameters(string? name) =>
        CommandVariables.IsContextName(name).Should().BeFalse();

    [Fact]
    public void Fill_IsSinglePass_AValueContainingAPlaceholderStaysLiteral()
    {
        var values = new Dictionary<string, string> { ["message"] = "{branch}", ["branch"] = "feature/x" };

        CommandVariables.Fill("git commit -m \"{message}\" # {branch}", name => values.GetValueOrDefault(name))
            .Should().Be("git commit -m \"{branch}\" # feature/x");
    }

    [Fact]
    public void Fill_LeavesUnknownNames_AndShellBraces_AsTyped()
    {
        CommandVariables.Fill("echo {task.id} {other.thing} ${env:X} { $_ }", name => name == "task.id" ? "42" : null)
            .Should().Be("echo 42 {other.thing} ${env:X} { $_ }");
    }

    [Fact]
    public void Used_ListsOnlyTheContextVariables()
    {
        CommandVariables.Used("dotnet run --project \"{worktree}/{project}\" -- {task.title} {Worktree}")
            .Should().Equal("worktree", "task.title");
    }

    [Fact]
    public void CommandParameters_StillListsContextShapedNames_ForOldAliases()
    {
        // Um global antigo com {branch} preenchido por "@x branch=…" continua sendo parâmetro.
        CommandParameters.Names("git checkout {branch} {task.id}").Should().Equal("branch");
    }

    [Theory]
    [InlineData("Validação & testes", true, '&')]
    [InlineData("100% pronto", true, '%')]
    [InlineData("C:\\Projetos\\eco core", true, null)]
    [InlineData("feature/$HOME", false, '$')]
    [InlineData("feature/$HOME", true, null)]
    [InlineData("linha\nquebrada", true, '\n')]
    public void UnsafeCharacter_DependsOnTheShell(string value, bool windows, char? expected) =>
        CommandVariables.UnsafeCharacter(value, windows).Should().Be(expected);

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData(".", ".")]
    [InlineData("{worktree}", ".")]
    [InlineData("./", ".")]
    [InlineData("src\\Eco.Web\\", "src/Eco.Web")]
    [InlineData("./src//Eco.Web", "src/Eco.Web")]
    [InlineData("{worktree}/src/Eco.Web", "src/Eco.Web")]
    [InlineData("\"src/Eco Web\"", "src/Eco Web")]
    public void WorkingDirectory_IsRelativeToTheWorktree(string? typed, string? expected) =>
        CommandWorkingDirectory.Normalize(typed).Should().Be(expected);

    [Theory]
    [InlineData("C:\\Projetos\\eco")]
    [InlineData("C:eco")]
    [InlineData("\\eco")]
    [InlineData("/eco")]
    [InlineData("../outro-repo")]
    [InlineData("src/../../fora")]
    [InlineData("src/{project}")]
    [InlineData("src/a|b")]
    public void WorkingDirectory_OutsideTheWorktree_OrNotAPath_IsRejected(string typed)
    {
        var normalize = () => CommandWorkingDirectory.Normalize(typed);

        normalize.Should().Throw<DomainException>();
    }

    [Fact]
    public void TheGlobalDefault_TreatsTheRootAsNoFolder()
    {
        CommandWorkingDirectory.NormalizeDefault(".").Should().BeNull();
        CommandWorkingDirectory.NormalizeDefault("src").Should().Be("src");
    }
}
