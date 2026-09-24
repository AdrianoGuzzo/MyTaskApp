using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>Criar a branch e o worktree, e gravar na tarefa (ADR-027).</summary>
public class StartDevelopmentHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private const string Path = @"C:\Projects\ecossistema-core-feature-123-corrigir-animais";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeGitClient _git = new();
    private readonly FakeDirectoryProbe _disk = new();
    private readonly RecordingProgress _progress = new();
    private readonly TaskItem _task;

    public StartDevelopmentHandlerTests()
    {
        _task = TaskItem.Create("Corrigir cálculo de animais", Now);
        _tasks.Seed(_task);
    }

    private StartDevelopmentHandler Handler() =>
        new(_tasks, _tasks, _git, _disk, new FakeTimeProvider(Now), NullLogger<StartDevelopmentHandler>.Instance);

    private DevelopmentPlan Plan(WorktreeConflict? conflict = null) =>
        new(
            _task.Id,
            FakeGitClient.Repository,
            GitBranch.RemoteTracking("origin", "develop"),
            "feature/123-corrigir-animais",
            Path,
            [],
            conflict);

    private Task<TaskDevelopmentView> StartAsync(string path = Path, bool adopt = false) =>
        Handler().HandleAsync(new StartDevelopment(Plan(), path, adopt), _progress, Ct);

    [Fact]
    public async Task CreatesBranchAndWorktree_AndTheTaskIsReady()
    {
        var view = await StartAsync();

        _git.Calls.Should().Contain(
            $"worktree add --no-track -b feature/123-corrigir-animais {Path} refs/remotes/origin/develop");

        view.Status.Should().Be(TaskDevelopmentStatus.Ready);
        view.RepositoryPath.Should().Be(FakeGitClient.Repository);
        view.SourceBranch.Should().Be("origin/develop");
        view.Branch.Should().Be("feature/123-corrigir-animais");
        view.WorktreePath.Should().Be(Path);
        view.CreatedAt.Should().Be(Now);
        _task.Developments[0].Status.Should().Be(TaskDevelopmentStatus.Ready);
    }

    /// <summary>"Criando" vai para o banco antes do Git: um app que cai no meio deixa rastro.</summary>
    [Fact]
    public async Task SavesCreatingBeforeGit_AndReadyAfter()
    {
        await StartAsync();

        _tasks.SaveCount.Should().Be(2);
        _progress.Reports.Select(report => report.Step).Distinct().Should().Equal(
            DevelopmentStep.CreateWorktree,
            DevelopmentStep.ValidateWorktree,
            DevelopmentStep.SaveTask);
    }

    [Fact]
    public async Task AGitFailure_IsSavedOnTheTask_AndCarriesTheCommand()
    {
        _git.AddWorktreeFailure = FakeGitClient.Failed(
            "git worktree add",
            "fatal: a branch named 'feature/123-corrigir-animais' already exists");

        var failure = (await FluentActions.Awaiting(() => StartAsync())
            .Should().ThrowAsync<DevelopmentStepException>()).Which;

        failure.Step.Should().Be(DevelopmentStep.CreateWorktree);
        failure.Message.Should().Be("A branch feature/123-corrigir-animais já existe.");
        failure.Command!.StandardError.Should().Contain("already exists");
        _task.Developments[0].Status.Should().Be(TaskDevelopmentStatus.Error);
        _task.Developments[0].FailureReason.Should().Be(failure.Message);
        _tasks.SaveCount.Should().Be(2);
    }

    [Fact]
    public async Task APathThatAppearedMeanwhile_IsNotOverwritten()
    {
        _disk.Existing.Add(Path);

        var failure = (await FluentActions.Awaiting(() => StartAsync())
            .Should().ThrowAsync<DevelopmentStepException>()).Which;

        failure.Step.Should().Be(DevelopmentStep.PlanWorktreePath);
        _git.Calls.Should().NotContain(call => call.StartsWith("worktree add", StringComparison.Ordinal));
        _task.Developments.Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherPath_IsUsedAsChosen()
    {
        var view = await StartAsync(path: Path + "-2");

        view.WorktreePath.Should().Be(Path + "-2");
    }

    [Fact]
    public async Task AdoptingAnExistingWorktree_TakesItsBranch_AndCreatesNothing()
    {
        _git.Worktrees.Add(new GitWorktree(Path.Replace('\\', '/'), "refs/heads/feature/antiga"));

        var view = await StartAsync(adopt: true);

        view.Status.Should().Be(TaskDevelopmentStatus.Ready);
        view.Branch.Should().Be("feature/antiga");
        _git.Calls.Should().NotContain(call => call.StartsWith("worktree add", StringComparison.Ordinal));
        _tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task AdoptingAFolderThatIsNotAWorktree_IsRefused()
    {
        await FluentActions.Awaiting(() => StartAsync(adopt: true))
            .Should().ThrowAsync<DevelopmentStepException>();

        _task.Developments.Should().BeEmpty();
    }

    [Fact]
    public async Task AnArchivedTask_IsRefused()
    {
        _task.Archive(Now);

        await FluentActions.Awaiting(() => StartAsync()).Should().ThrowAsync<DomainException>();
        _git.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ARetryAfterAnError_ReusesTheSameRecord()
    {
        _git.AddWorktreeFailure = FakeGitClient.Failed("git worktree add", "fatal: boom");
        await FluentActions.Awaiting(() => StartAsync()).Should().ThrowAsync<DevelopmentStepException>();
        var first = _task.Developments[0].Id;

        _git.AddWorktreeFailure = null;
        await StartAsync();

        _task.Developments[0].Id.Should().Be(first);
        _task.Developments[0].Status.Should().Be(TaskDevelopmentStatus.Ready);
    }

    [Theory]
    [InlineData("fatal: 'x' already exists", "O caminho")]
    [InlineData("fatal: 'feature/x' is already checked out at 'C:/y'", "já está em uso")]
    [InlineData("fatal: invalid reference: origin/develop", "não foi encontrada")]
    [InlineData("fatal: something else", "O Git não conseguiu criar o worktree.")]
    public void GitErrors_AreTranslated(string error, string expected) =>
        StartDevelopmentHandler.CreateFailureMessage(FakeGitClient.Failed("git worktree add", error), Plan(), Path)
            .Should().Contain(expected);

    /// <summary>Outro repositório da tarefa é outro ambiente, ao lado do primeiro (ADR-031).</summary>
    [Fact]
    public async Task AnotherRepository_AddsASecondEnvironment()
    {
        var first = _task.BeginDevelopment(
            null, @"C:\Projects\ecossistema-api", "main", "feature/123-corrigir-animais", @"C:\Projects\ecossistema-api-feature-123", Now);
        _task.MarkDevelopmentReady(first.Id, Now);

        var view = await StartAsync();

        view.Id.Should().NotBe(first.Id);
        view.TaskId.Should().Be(_task.Id);
        _task.Developments.Select(development => development.Id).Should().Equal(first.Id, view.Id);
        _task.Developments.Should().AllSatisfy(development => development.Status.Should().Be(TaskDevelopmentStatus.Ready));
    }

    [Fact]
    public async Task ARetry_ReusesTheFailedEnvironment()
    {
        var failed = _task.BeginDevelopment(null, FakeGitClient.Repository, "main", "feature/x", Path, Now);
        _task.MarkDevelopmentFailed(failed.Id, "falhou", Now);

        var view = await Handler().HandleAsync(
            new StartDevelopment(Plan() with { DevelopmentId = failed.Id }, Path), _progress, Ct);

        view.Id.Should().Be(failed.Id);
        _task.Developments.Should().ContainSingle().Which.Status.Should().Be(TaskDevelopmentStatus.Ready);
    }
}
