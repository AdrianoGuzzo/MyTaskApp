using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>
/// Os arquivos de um ambiente, para o <c>@</c> no texto do agente (ADR-039):
/// só de ambiente pronto, só da própria tarefa, e com teto.
/// </summary>
public class ListEnvironmentFilesHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

    private const string Worktree = @"C:\Projects\ecossistema-core-feature-x";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeGitClient _git = new();
    private readonly FakeDirectoryProbe _disk = new();
    private readonly TaskItem _task;

    public ListEnvironmentFilesHandlerTests()
    {
        _task = TaskItem.Create("Corrigir", Now);
        _task.BeginDevelopment(null, FakeGitClient.Repository, "origin/main", "feature/x", Worktree, Now);
        _task.MarkDevelopmentReady(_task.Developments[0].Id, Now);
        _tasks.Seed(_task);
        _disk.Existing.Add(Worktree);
        _git.Files[Worktree] = ["README.md", "src/App.cs"];
    }

    private ListEnvironmentFilesHandler Handler() => new(_tasks, _git, _disk);

    private Task<EnvironmentFiles> ListAsync() =>
        Handler().HandleAsync(new ListEnvironmentFiles(_task.Id, _task.Developments[0].Id), Ct);

    [Fact]
    public async Task AReadyEnvironment_ListsWhatGitKnows()
    {
        var files = await ListAsync();

        files.Files.Should().Equal("README.md", "src/App.cs");
        files.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task AWorktreeThatIsGone_ListsNothing_WithoutAskingGit()
    {
        _disk.Existing.Remove(Worktree);
        _git.Failures[nameof(IGitClient.ListFilesAsync)] = FakeGitClient.Failed("git ls-files", "não devia rodar");

        (await ListAsync()).Should().Be(EnvironmentFiles.Empty);
    }

    [Fact]
    public async Task AnEnvironmentThatIsNotReady_ListsNothing()
    {
        _task.MarkDevelopmentRemoved(_task.Developments[0].Id, Now);

        (await ListAsync()).Files.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEnvironmentOfAnotherTask_IsRefused()
    {
        var other = TaskItem.Create("Outra", Now);
        _tasks.Seed(other);

        await FluentActions.Awaiting(() => Handler().HandleAsync(
                new ListEnvironmentFiles(other.Id, _task.Developments[0].Id), Ct))
            .Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task AHugeRepository_StopsAtTheCeiling()
    {
        _git.Files[Worktree] = [.. Enumerable.Range(0, ListEnvironmentFilesHandler.MaxFiles + 10).Select(i => $"f{i}.cs")];

        var files = await ListAsync();

        files.Files.Should().HaveCount(ListEnvironmentFilesHandler.MaxFiles);
        files.IsTruncated.Should().BeTrue();
    }
}
