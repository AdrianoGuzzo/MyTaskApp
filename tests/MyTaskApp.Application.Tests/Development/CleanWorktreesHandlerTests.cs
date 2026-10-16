using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>
/// A limpeza de worktrees (ADR-065): acha os sem tarefa e os de tarefa
/// encerrada, e apaga com força o que o usuário deixou marcado — nunca o
/// principal, nunca o de tarefa em andamento, nunca uma pasta aberta.
/// </summary>
public class CleanWorktreesHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    private const string Orphan = @"C:\Projects\ecossistema-core-experimento";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeTagQuery _tags = new();
    private readonly FakeGitClient _git = new();
    private readonly FakeDirectoryProbe _disk = new();
    private readonly FakeAgentSessionRepository _agents = new();
    private readonly FakeAgentProcessTracker _processes = new();
    private readonly FakeDirectoryRemover _remover = new();

    public CleanWorktreesHandlerTests()
    {
        _tags.MappedPaths.Add(FakeGitClient.Repository);
    }

    private FindWorktreeCleanupHandler Find() =>
        new(
            _tasks,
            _tags,
            _git,
            _disk,
            _agents,
            _processes,
            _tasks,
            new ProbeWorktreesHandler(_git, _disk, NullLogger<ProbeWorktreesHandler>.Instance),
            new FakeTimeProvider(Now),
            NullLogger<FindWorktreeCleanupHandler>.Instance);

    private CleanWorktreesHandler Clean() =>
        new(
            _tasks,
            _git,
            _disk,
            _remover,
            new RemoveWorktreeHandler(
                _tasks,
                _tasks,
                _git,
                _disk,
                _agents,
                _processes,
                _remover,
                new FakeTimeProvider(Now),
                NullLogger<RemoveWorktreeHandler>.Instance),
            NullLogger<CleanWorktreesHandler>.Instance);

    private void AddWorktree(string path, string branch, bool isLocked = false)
    {
        _git.Worktrees.Add(new GitWorktree(path.Replace('\\', '/'), GitBranch.LocalPrefix + branch, IsLocked: isLocked));
        _git.Branches.Add(GitBranch.Local(branch));
        _disk.Existing.Add(path);
    }

    /// <summary>Uma tarefa com worktree pronto, já registrado no Git e no disco.</summary>
    private TaskItem TaskWithWorktree(string title, string branch)
    {
        var path = $@"C:\Projects\ecossistema-core-{branch.Replace('/', '-')}";
        var task = TaskItem.Create(title, Now);

        task.BeginDevelopment(null, FakeGitClient.Repository, "origin/main", branch, path, Now);
        task.MarkDevelopmentReady(task.Developments[0].Id, Now);
        _tasks.Seed(task);
        AddWorktree(path, branch);

        return task;
    }

    private static void Complete(TaskItem task) => task.CompleteOccurrence(task.Occurrences[0].Id, Now);

    [Fact]
    public async Task AWorktreeWithoutTask_IsFound_ButNotTheMainOne_NorTheActiveTasks()
    {
        AddWorktree(Orphan, "experimento");
        TaskWithWorktree("Em andamento", "feature/ativa");

        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);

        var candidate = scan.Candidates.Should().ContainSingle().Which;
        candidate.WorktreePath.Should().Be(Orphan);
        candidate.Reason.Should().Be(WorktreeCleanupReason.NoTask);
        candidate.Branch.Should().Be("experimento");
        candidate.TaskId.Should().BeNull();
        candidate.RepositoryPath.Should().Be(FakeGitClient.Repository, "o Git remove a partir do worktree principal");
        scan.Problems.Should().BeEmpty();
    }

    [Fact]
    public async Task EachClosedTask_GivesItsReason()
    {
        var completed = TaskWithWorktree("Concluída", "feature/concluida");
        Complete(completed);

        var cancelled = TaskWithWorktree("Cancelada", "feature/cancelada");
        cancelled.CancelOccurrence(cancelled.Occurrences[0].Id);

        var archived = TaskWithWorktree("Arquivada", "feature/arquivada");
        Complete(archived);
        archived.Archive(Now);

        var trashed = TaskWithWorktree("Na lixeira", "feature/lixeira");
        trashed.MoveToTrash(Now, null);

        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);

        scan.Candidates.Select(candidate => (candidate.TaskTitle, candidate.Reason)).Should().BeEquivalentTo(
        [
            ("Concluída", WorktreeCleanupReason.Completed),
            ("Cancelada", WorktreeCleanupReason.Cancelled),
            ("Arquivada", WorktreeCleanupReason.Archived),
            ("Na lixeira", WorktreeCleanupReason.Trashed),
        ]);
    }

    [Fact]
    public async Task ARepositoryReachedTwice_ListsEachWorktreeOnce()
    {
        // O diretório mapeado e o repositório da tarefa são o mesmo repositório.
        _tags.MappedPaths.Add(FakeGitClient.Repository.ToUpperInvariant());
        AddWorktree(Orphan, "experimento");
        Complete(TaskWithWorktree("Concluída", "feature/x"));

        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);

        scan.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public async Task AWorktreeMappedAsATagDirectory_IsInUse_AndStays()
    {
        AddWorktree(Orphan, "experimento");
        _tags.MappedPaths.Add(Orphan);
        _git.Repositories[Orphan] = Orphan.Replace('\\', '/');

        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);

        scan.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task TheChanges_AreReported_SoTheScreenCanWarn()
    {
        AddWorktree(Orphan, "experimento");
        _git.BranchStatuses[Orphan] = new GitBranchStatus([" M src/App.cs", "?? novo.txt"]);

        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);

        var sync = scan.Candidates.Should().ContainSingle().Which.Sync;
        sync.State.Should().Be(WorktreeSyncState.Dirty);
        sync.Changes.Should().Be(2);
    }

    [Fact]
    public async Task ALockedWorktree_IsFound_AndSaysSo()
    {
        AddWorktree(Orphan, "experimento", isLocked: true);

        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);

        scan.Candidates.Should().ContainSingle().Which.IsLocked.Should().BeTrue();
    }

    [Fact]
    public async Task ARunningAgent_IsFlagged()
    {
        var task = TaskWithWorktree("Concluída", "feature/x");
        Complete(task);

        var session = AgentSession.Create(task.Id, task.Developments[0].Id, "claude-code", @"C:\claude.exe", task.Developments[0].WorktreePath, Now);
        session.MarkRunning(4242, Now);
        _agents.Seed(session);
        _processes.Run(4242, Now);

        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);

        scan.Candidates.Should().ContainSingle().Which.HasActiveAgent.Should().BeTrue();
    }

    [Fact]
    public async Task AClosedTaskWhoseWorktreeGitAlreadyForgot_IsStillFound()
    {
        var task = TaskWithWorktree("Concluída", "feature/x");
        Complete(task);
        _git.Worktrees.RemoveAll(worktree => worktree.BranchName == "feature/x");
        _disk.Existing.Remove(task.Developments[0].WorktreePath);

        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);

        var candidate = scan.Candidates.Should().ContainSingle().Which;
        candidate.TaskId.Should().Be(task.Id);
        candidate.Exists.Should().BeFalse();
    }

    [Fact]
    public async Task ARepositoryWhereGitFails_IsReported_AndTheScanGoesOn()
    {
        _git.Failures[nameof(IGitClient.ListWorktreesAsync)] =
            FakeGitClient.Failed("git worktree list", "fatal: not a git repository\nmais detalhes");

        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);

        scan.Candidates.Should().BeEmpty();
        scan.Problems.Should().ContainSingle().Which.Should().EndWith("fatal: not a git repository");
    }

    [Fact]
    public async Task Clean_RemovesAWorktreeWithChanges_WithForce_AndKeepsTheBranch()
    {
        AddWorktree(Orphan, "experimento");
        _git.Statuses[Orphan] = new GitStatus([" M src/App.cs"]);
        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);

        var result = await Clean().HandleAsync(new CleanWorktrees(scan.Candidates), Ct);

        result.Removed.Should().Be(1);
        result.Failures.Should().BeEmpty();
        _git.Calls.Should().Contain($"worktree remove --force {Orphan}");
        _git.Calls.Should().NotContain(call => call.Contains("branch", StringComparison.Ordinal));
        _remover.Calls.Should().ContainSingle().Which.Terminate.Should().BeEmpty("a limpeza nunca encerra processo");
    }

    [Fact]
    public async Task Clean_OfAClosedTask_MarksTheEnvironmentRemoved()
    {
        var task = TaskWithWorktree("Concluída", "feature/x");
        Complete(task);
        _git.Statuses[task.Developments[0].WorktreePath] = new GitStatus(["?? novo.txt"]);
        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);

        var result = await Clean().HandleAsync(new CleanWorktrees(scan.Candidates), Ct);

        result.Removed.Should().Be(1);
        task.Developments[0].Status.Should().Be(TaskDevelopmentStatus.Removed);
        _git.Calls.Should().Contain($"worktree remove --force {task.Developments[0].WorktreePath}");
    }

    [Fact]
    public async Task Clean_OfALockedWorktree_ForcesTwice()
    {
        AddWorktree(Orphan, "experimento", isLocked: true);
        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);

        await Clean().HandleAsync(new CleanWorktrees(scan.Candidates), Ct);

        _git.Calls.Should().Contain($"worktree remove --force --force {Orphan}");
    }

    [Fact]
    public async Task Clean_SkipsAFolderSomeoneHasOpen_AndGoesOnWithTheOthers()
    {
        const string other = @"C:\Projects\ecossistema-core-rascunho";
        AddWorktree(Orphan, "experimento");
        AddWorktree(other, "rascunho");
        _remover.Lockers[Orphan] = [new DirectoryLocker(4242, "pwsh", null, true)];
        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);

        var result = await Clean().HandleAsync(new CleanWorktrees(scan.Candidates), Ct);

        result.Removed.Should().Be(1);
        var failure = result.Failures.Should().ContainSingle().Which;
        failure.WorktreePath.Should().Be(Orphan);
        failure.Message.Should().Contain("pwsh (PID 4242)");
        _git.Calls.Should().NotContain(call => call.EndsWith(Orphan, StringComparison.Ordinal));
        _git.Calls.Should().Contain($"worktree remove --force {other}");
    }

    [Fact]
    public async Task Clean_OfATaskReopenedAfterTheScan_LeavesTheWorktree()
    {
        var task = TaskWithWorktree("Concluída", "feature/x");
        Complete(task);
        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);
        task.ReopenOccurrence(task.Occurrences[0].Id);

        var result = await Clean().HandleAsync(new CleanWorktrees(scan.Candidates), Ct);

        result.Removed.Should().Be(0);
        result.Failures.Should().ContainSingle().Which.Message.Should().Contain("em andamento");
        task.Developments[0].Status.Should().Be(TaskDevelopmentStatus.Ready);
        _git.Calls.Should().NotContain(call => call.StartsWith("worktree remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clean_OfATaskWithARunningAgent_LeavesTheWorktree()
    {
        var task = TaskWithWorktree("Concluída", "feature/x");
        Complete(task);
        var scan = await Find().HandleAsync(new FindWorktreeCleanup(), Ct);

        var session = AgentSession.Create(task.Id, task.Developments[0].Id, "claude-code", @"C:\claude.exe", task.Developments[0].WorktreePath, Now);
        session.MarkRunning(4242, Now);
        _agents.Seed(session);
        _processes.Run(4242, Now);

        var result = await Clean().HandleAsync(new CleanWorktrees(scan.Candidates), Ct);

        result.Failures.Should().ContainSingle().Which.Message.Should().Contain("agente");
        task.Developments[0].Status.Should().Be(TaskDevelopmentStatus.Ready);
    }

    [Fact]
    public async Task Clean_NeverRemovesTheMainWorktree()
    {
        var main = new WorktreeCleanupCandidate(
            FakeGitClient.Repository,
            FakeGitClient.Repository,
            "main",
            WorktreeCleanupReason.NoTask,
            null,
            null,
            null,
            WorktreeSync.Unknown(Guid.NewGuid()));

        var result = await Clean().HandleAsync(new CleanWorktrees([main]), Ct);

        result.Failures.Should().ContainSingle().Which.Message.Should().Contain("principal");
        _git.Calls.Should().NotContain(call => call.StartsWith("worktree remove", StringComparison.Ordinal));
        _remover.Calls.Should().BeEmpty();
    }
}
