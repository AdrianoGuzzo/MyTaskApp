using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Commands;

/// <summary>Comandos globais e a lista pós-Worktree da tarefa (ADR-028).</summary>
public class DevelopmentCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private const string Repository = @"C:\Projects\ecossistema-core";
    private const string Worktree = @"C:\Projects\ecossistema-core-feature-x";

    [Theory]
    [InlineData("restore", "@restore")]
    [InlineData(" @npm-install ", "@npm-install")]
    [InlineData("@docker.up_2", "@docker.up_2")]
    public void TheAlias_GetsItsAt_AndIsTrimmed(string typed, string expected)
    {
        DevelopmentCommand.Create(typed, "x", null, Now).Alias.Should().Be(expected);
    }

    [Fact]
    public void TheCommand_IsKeptAsTyped_ExceptForTheEdges()
    {
        var command = DevelopmentCommand.Create("@prepare", "  dotnet restore && dotnet build \"a b\"  ", "  ", Now);

        command.Command.Should().Be("dotnet restore && dotnet build \"a b\"");
        command.Description.Should().BeNull();
        command.CreatedAt.Should().Be(Now);
        command.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public void AnInvalidUpdate_ChangesNothing()
    {
        var command = DevelopmentCommand.Create("@build", "dotnet build", "Compila", Now);

        var update = () => command.Update("@novo", "", "Outra", Now.AddHours(1));

        update.Should().Throw<DomainException>();
        command.Alias.Should().Be("@build");
        command.Description.Should().Be("Compila");
        command.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public void TheTaskList_KeepsTheOrder_AndDropsBlankLines()
    {
        var task = TaskItem.Create("Feature X", Now);
        var development = task.BeginDevelopment(null, Repository, "origin/develop", "feature/x", Worktree, Now);

        task.SetDevelopmentCommands(development.Id, ["@restore", "", "  @npm-install ", null, "@build"], Now);

        development.Commands.Select(command => (command.Command, command.Order))
            .Should().Equal(("@restore", 0), ("@npm-install", 1), ("@build", 2));
    }

    [Fact]
    public void Reordering_ReusesTheRows_AndRenumbers()
    {
        var task = TaskItem.Create("Feature X", Now);
        var development = task.BeginDevelopment(null, Repository, "origin/develop", "feature/x", Worktree, Now);
        task.SetDevelopmentCommands(development.Id, ["@restore", "@build", "@test"], Now);
        var ids = development.Commands.Select(command => command.Id).ToList();

        task.SetDevelopmentCommands(development.Id, ["@build", "@restore"], Now);

        development.Commands.Select(command => command.Command).Should().Equal("@build", "@restore");
        development.Commands.Select(command => command.Id).Should().Equal(ids[0], ids[1]);
    }

    [Fact]
    public void AMultiLineCommand_IsRefused()
    {
        var task = TaskItem.Create("Feature X", Now);
        var development = task.BeginDevelopment(null, Repository, "origin/develop", "feature/x", Worktree, Now);

        var set = () => task.SetDevelopmentCommands(development.Id, ["npm install\nnpm test"], Now);

        set.Should().Throw<DomainException>().WithMessage("*uma linha só*");
    }

    [Fact]
    public void AnArchivedTask_DoesNotChangeItsCommands()
    {
        var task = TaskItem.Create("Feature X", Now);
        var development = task.BeginDevelopment(null, Repository, "origin/develop", "feature/x", Worktree, Now);
        task.Archive(Now);

        var set = () => task.SetDevelopmentCommands(development.Id, ["@restore"], Now);

        set.Should().Throw<DomainException>();
    }
}
