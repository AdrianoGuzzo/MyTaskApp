using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>Remover o worktree sem nunca perder trabalho não commitado (ADR-027).</summary>
public class RemoveWorktreeHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private const string Path = @"C:\Projects\ecossistema-core-feature-x";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeGitClient _git = new();
    private readonly FakeDirectoryProbe _disk = new();
    private readonly FakeAgentSessionRepository _agents = new();
    private readonly FakeAgentProcessTracker _processes = new();
    private readonly FakeDirectoryRemover _remover = new();
    private readonly TaskItem _task;

    public RemoveWorktreeHandlerTests()
    {
        _task = TaskItem.Create("Corrigir", Now);
        _task.BeginDevelopment(FakeGitClient.Repository, "origin/main", "feature/x", Path, Now);
        _task.MarkDevelopmentReady(Now);
        _tasks.Seed(_task);
        _disk.Existing.Add(Path);
        _git.Worktrees.Add(new GitWorktree(Path.Replace('\\', '/'), "refs/heads/feature/x"));
    }

    private RemoveWorktreeHandler Remove() =>
        new(
            _tasks,
            _tasks,
            _git,
            _disk,
            _agents,
            _processes,
            _remover,
            new FakeTimeProvider(Now),
            NullLogger<RemoveWorktreeHandler>.Instance);

    private InspectWorktreeHandler Inspect() => new(_tasks, _git, _disk);

    [Fact]
    public async Task ACleanWorktree_IsRemoved_AndTheBranchStays()
    {
        var view = await Remove().HandleAsync(new RemoveWorktree(_task.Id), Ct);

        view.Status.Should().Be(TaskDevelopmentStatus.Removed);
        _git.Calls.Should().Contain($"worktree remove {Path}");
        _git.Calls.Should().NotContain(call => call.Contains("branch", StringComparison.Ordinal));
        _remover.Calls.Should().ContainSingle().Which.Path.Should().Be(Path, "o que o Git deixou na pasta também sai");
        _tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task AWorktreeWithChanges_IsNotRemoved()
    {
        _git.Statuses[Path] = new GitStatus([" M src/App.cs"]);

        var failure = (await FluentActions.Awaiting(() => Remove().HandleAsync(new RemoveWorktree(_task.Id), Ct))
            .Should().ThrowAsync<DevelopmentStepException>()).Which;

        failure.Changes.Should().Equal(" M src/App.cs");
        _git.Calls.Should().NotContain(call => call.StartsWith("worktree remove", StringComparison.Ordinal));
        _task.Development!.Status.Should().Be(TaskDevelopmentStatus.Ready);
        _tasks.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Inspect_ReportsTheChanges()
    {
        _git.Statuses[Path] = new GitStatus(["?? novo.txt"]);

        var inspection = await Inspect().HandleAsync(new InspectWorktree(_task.Id), Ct);

        inspection.Exists.Should().BeTrue();
        inspection.IsClean.Should().BeFalse();
        inspection.Changes.Should().Equal("?? novo.txt");
    }

    [Fact]
    public async Task AFolderThatIsGone_IsPruned_AndMarkedRemoved()
    {
        _disk.Existing.Remove(Path);

        var inspection = await Inspect().HandleAsync(new InspectWorktree(_task.Id), Ct);
        var view = await Remove().HandleAsync(new RemoveWorktree(_task.Id), Ct);

        inspection.Exists.Should().BeFalse();
        view.Status.Should().Be(TaskDevelopmentStatus.Removed);
        _git.Calls.Should().Contain("worktree prune");
        _git.Calls.Should().NotContain(call => call.StartsWith("worktree remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GitRefusingTheRemoval_KeepsTheTaskReady()
    {
        _git.RemoveResult = FakeGitClient.Failed("git worktree remove", "fatal: 'x' is locked");

        var failure = (await FluentActions.Awaiting(() => Remove().HandleAsync(new RemoveWorktree(_task.Id), Ct))
            .Should().ThrowAsync<DevelopmentStepException>()).Which;

        failure.Command!.StandardError.Should().Contain("locked");
        failure.IsDirectoryLocked.Should().BeFalse();
        _remover.Calls.Should().BeEmpty("o worktree continua registrado: a pasta não é sobra");
        _task.Development!.Status.Should().Be(TaskDevelopmentStatus.Ready);
    }

    private static readonly DirectoryLocker Terminal = new(4242, "pwsh", @"C:\Program Files\PowerShell\7\pwsh.exe", true);

    /// <summary>
    /// Um terminal aberto na pasta: o Git esquece o worktree e sai com erro. Não é
    /// "o Git não removeu" — é a pasta que ficou, e a tela precisa saber quem a segura.
    /// </summary>
    [Fact]
    public async Task AHeldFolder_ReportsWhoHoldsIt_AndKeepsTheTaskReady()
    {
        _git.RemoveResult = FakeGitClient.Failed("git worktree remove", "error: failed to delete 'x': Permission denied", 255);
        _git.RemoveForgetsOnFailure = true;
        _remover.Results.Enqueue(new DirectoryRemoval(false, [Terminal], [], "Access denied"));

        var failure = (await FluentActions.Awaiting(() => Remove().HandleAsync(new RemoveWorktree(_task.Id), Ct))
            .Should().ThrowAsync<DevelopmentStepException>()).Which;

        failure.IsDirectoryLocked.Should().BeTrue();
        failure.Lockers.Should().Equal(Terminal);
        failure.Message.Should().Contain("1 processo");
        _remover.Calls.Single().Terminate.Should().BeEmpty("a primeira tentativa não encerra ninguém");
        _task.Development!.Status.Should().Be(TaskDevelopmentStatus.Ready);
        _tasks.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task TheLeftoverFolder_IsDeleted_WithoutAskingGitAgain()
    {
        // O Git já esqueceu numa tentativa anterior; sobrou a pasta.
        _git.Worktrees.RemoveAll(worktree => WorktreePathPlanner.SamePath(worktree.Path, Path));

        var inspection = await Inspect().HandleAsync(new InspectWorktree(_task.Id), Ct);
        var view = await Remove().HandleAsync(new RemoveWorktree(_task.Id, [Terminal]), Ct);

        inspection.IsLeftover.Should().BeTrue();
        inspection.IsClean.Should().BeTrue();
        view.Status.Should().Be(TaskDevelopmentStatus.Removed);
        _git.Calls.Should().NotContain(call => call.StartsWith("worktree remove", StringComparison.Ordinal));
        _remover.Calls.Should().ContainSingle().Which.Terminate.Should().Equal(Terminal);
    }

    [Fact]
    public async Task ALeftoverThatBecameARepository_IsNotDeleted()
    {
        _git.Worktrees.RemoveAll(worktree => WorktreePathPlanner.SamePath(worktree.Path, Path));
        _git.Repositories[Path] = Path.Replace('\\', '/');

        await FluentActions.Awaiting(() => Remove().HandleAsync(new RemoveWorktree(_task.Id), Ct))
            .Should().ThrowAsync<DevelopmentStepException>().WithMessage("*outro repositório*");

        _remover.Calls.Should().BeEmpty();
        _task.Development!.Status.Should().Be(TaskDevelopmentStatus.Ready);
    }

    /// <summary>Arquivar a tarefa não pode prender um worktree no disco para sempre.</summary>
    [Fact]
    public async Task AnArchivedTask_CanStillRemoveItsWorktree()
    {
        _task.Archive(Now);

        var view = await Remove().HandleAsync(new RemoveWorktree(_task.Id), Ct);

        view.Status.Should().Be(TaskDevelopmentStatus.Removed);
    }

    [Fact]
    public async Task ATaskWithoutDevelopment_IsRefused()
    {
        var plain = TaskItem.Create("Sem ambiente", Now);
        _tasks.Seed(plain);

        await FluentActions.Awaiting(() => Remove().HandleAsync(new RemoveWorktree(plain.Id), Ct))
            .Should().ThrowAsync<DomainException>();
    }

    /// <summary>O agente está com a pasta aberta: nada é removido (ADR-030).</summary>
    [Fact]
    public async Task ARunningAgent_BlocksTheRemoval()
    {
        var session = AgentSession.Create(_task.Id, "claude-code", @"C:\claude.exe", Path, Now);
        session.MarkRunning(4242, Now);
        _agents.Seed(session);
        _processes.Run(4242, Now);

        await FluentActions.Awaiting(() => Remove().HandleAsync(new RemoveWorktree(_task.Id), Ct))
            .Should().ThrowAsync<DomainException>()
            .WithMessage("*agente*");

        _git.Calls.Should().NotContain(call => call.StartsWith("worktree remove", StringComparison.Ordinal));
        _task.Development!.Status.Should().Be(TaskDevelopmentStatus.Ready);
    }

    /// <summary>Sessão gravada como ativa, mas o processo já saiu: não trava nada.</summary>
    [Fact]
    public async Task AnAgentWhoseProcessIsGone_IsEnded_AndTheRemovalGoesOn()
    {
        var session = AgentSession.Create(_task.Id, "claude-code", @"C:\claude.exe", Path, Now);
        session.MarkRunning(4242, Now);
        _agents.Seed(session);

        var view = await Remove().HandleAsync(new RemoveWorktree(_task.Id), Ct);

        view.Status.Should().Be(TaskDevelopmentStatus.Removed);
        session.Status.Should().Be(AgentSessionStatus.Exited);
    }
}
