using MyTaskApp.Application.Commands;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Commands;

/// <summary>
/// As variáveis de contexto no resolvedor de <c>@alias</c> (ADR-051), sem mudar
/// o comportamento de quem não passa contexto (ADR-028).
/// </summary>
public class CommandContextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    private const string Repository = @"C:\Projects\ecossistema-core";
    private const string Worktree = @"C:\Projects\ecossistema-core-feature-x";

    private static readonly Dictionary<string, string> Globals = new()
    {
        ["@open"] = "code \"{worktree}\"",
        ["@checkout"] = "git checkout {branch}",
        ["@sync"] = "eco-sync {nomebanco} --task {task.id}",
        ["@label"] = "echo {tag}",
    };

    private static CommandContext Context(string title = "Validar antimicrobianos", string? tag = "ECO CORE")
    {
        var task = TaskItem.Create(title, Now);
        task.BeginDevelopment(null, Repository, "origin/develop", "feature/validacao", Worktree, Now);

        return CommandContext.For(task, task.Developments[0], tag, windows: true);
    }

    [Fact]
    public void WithoutContext_NothingChanges_AndContextShapedNamesAreParameters()
    {
        var resolved = CommandAliasResolver.Resolve(["@checkout"], Globals);

        resolved[0].IsResolved.Should().BeFalse();
        resolved[0].MissingParameters.Should().Equal("branch");
    }

    [Fact]
    public void AContextVariable_IsFilledFromTheEnvironment()
    {
        var resolved = CommandAliasResolver.Resolve(["@open", "@checkout"], Globals, Context());

        resolved.Select(step => step.Command).Should().Equal(
            $"code \"{Worktree}\"",
            "git checkout feature/validacao");
    }

    [Fact]
    public void AnExplicitValue_WinsOverTheContext()
    {
        var resolved = CommandAliasResolver.Resolve(["@checkout branch=main"], Globals, Context());

        resolved[0].Command.Should().Be("git checkout main");
    }

    [Fact]
    public void UserParameters_AreStillRequired_AndDottedVariablesAreFilled()
    {
        var context = Context();

        CommandAliasResolver.Resolve(["@sync"], Globals, context)[0].MissingParameters.Should().Equal("nomebanco");

        var resolved = CommandAliasResolver.Resolve(["@sync nomebanco=Eco"], Globals, context);

        resolved[0].Command.Should().StartWith("eco-sync Eco --task ").And.NotContain("{task.id}");
    }

    [Fact]
    public void ADeferredContext_DoesNotReportVariablesAsMissing_AndKeepsThemInTheText()
    {
        var deferred = CommandContext.Deferred([CommandVariables.Worktree, CommandVariables.Branch]);

        var resolved = CommandAliasResolver.Resolve(["@open", "@checkout"], Globals, deferred);

        resolved.Should().OnlyContain(step => step.IsResolved);
        resolved[0].Command.Should().Be("code \"{worktree}\"");
    }

    [Fact]
    public void AVariableTheContextDoesNotKnow_IsMissing()
    {
        var resolved = CommandAliasResolver.Resolve(["@label"], Globals, Context(tag: null));

        resolved[0].MissingParameters.Should().Equal("tag");
    }

    [Fact]
    public void AnUnsafeValue_IsRefused_OnlyWhenTheCommandUsesIt()
    {
        var context = Context(title: "Validar & publicar", tag: "ECO & CORE");

        CommandAliasResolver.Resolve(["@open"], Globals, context)[0].IsResolved.Should().BeTrue();

        var label = CommandAliasResolver.Resolve(["@label"], Globals, context)[0];

        label.IsResolved.Should().BeFalse();
        label.HasUnsafeValue.Should().BeTrue();
        label.IsUnknownAlias.Should().BeFalse();
        label.Error.Should().Contain("{tag}").And.Contain("\"&\"");
    }

    [Fact]
    public void AnExplicitValue_GoesAsTyped_EvenWithShellCharacters()
    {
        var resolved = CommandAliasResolver.Resolve(["@checkout branch=\"a&b\""], Globals, Context());

        resolved[0].Command.Should().Be("git checkout \"a&b\"");
    }

    [Fact]
    public void Fill_AnOptionalEmptyValue_DisappearsFromTheLine()
    {
        var expansion = CommandAliasResolver.Fill(
            "dotnet run {extra}",
            new Dictionary<string, string> { ["extra"] = string.Empty },
            CommandContext.None);

        expansion.Command.Should().Be("dotnet run ");
    }

    [Fact]
    public void TheFolderContext_OnlyKnowsTheWorktree()
    {
        var context = CommandContext.ForFolder(@"C:\Temp\teste");

        context.ValueOf(CommandVariables.Worktree).Should().Be(@"C:\Temp\teste");
        context.ValueOf(CommandVariables.WorktreeName).Should().Be("teste");
        context.Provides(CommandVariables.Branch).Should().BeFalse();
    }

    [Fact]
    public void TheEnvironmentContext_KnowsEveryVariable()
    {
        var context = Context();

        context.ValueOf("worktree.name").Should().Be("ecossistema-core-feature-x");
        context.ValueOf("repository").Should().Be(Repository);
        context.ValueOf("repository.name").Should().Be("ecossistema-core");
        context.ValueOf("task.title").Should().Be("Validar antimicrobianos");
        context.ValueOf("tag").Should().Be("ECO CORE");
        context.ValueOf("project").Should().BeNull("não é variável de contexto");
    }
}
