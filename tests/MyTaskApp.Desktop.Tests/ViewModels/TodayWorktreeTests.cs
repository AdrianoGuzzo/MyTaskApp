using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A bolinha de worktree da linha e a pergunta "remover o worktree?" ao
/// concluir a tarefa (ADR-034).
/// </summary>
public class TodayWorktreeTests
{
    private static readonly DateOnly Date = new(2026, 9, 24);

    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeUseCaseRunner _runner = new()
    {
        Result = new TodayBoard(Date, [], [], [], [], []),
    };

    private readonly FakeConfirmationDialog _confirmation = new();

    private TodayViewModel ViewModel() =>
        new(_runner, _confirmation, new FakeClipboardWriter(), TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

    private static TaskWorktree Worktree(string repository = "eco-core") =>
        new(Guid.CreateVersion7(), repository, "feature/x", "origin/main", $@"C:\Projects\{repository}-feature-x");

    private static TodayTask Listed(params TaskWorktree[] worktrees) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "Corrigir animais", TaskPriority.Normal, Date, null, false,
            Worktrees: worktrees.Length == 0 ? null : worktrees);

    private static WorktreeSync Sync(TaskWorktree worktree, WorktreeSyncState state, int changes = 0, int commits = 0, int unpushed = 0) =>
        new(worktree.DevelopmentId, state, changes, commits, unpushed, IsPublished: state == WorktreeSyncState.Pushed);

    private static TaskDevelopmentView Removed()
    {
        var task = TaskItem.Create("Corrigir animais", Now);
        var development = task.BeginDevelopment(null, @"C:\Projects\eco-core", "origin/main", "feature/x", @"C:\Projects\eco-core-feature-x", Now);
        return TaskDevelopmentView.From(development);
    }

    [Fact]
    public void ARowWithoutWorktree_HasNoDot()
    {
        var row = new TaskRowViewModel(Listed(), false);

        row.Worktree.HasWorktree.Should().BeFalse();
        row.Worktree.IsRetained.Should().BeFalse();
    }

    [Fact]
    public void BeforeGitAnswers_TheDotIsAnEmptyRing()
    {
        var row = new TaskRowViewModel(Listed(Worktree()), false);

        row.Worktree.HasWorktree.Should().BeTrue();
        row.Worktree.IsUnknown.Should().BeTrue();
        row.Worktree.Lines.Should().ContainSingle().Which.Detail.Should().Be("verificando…");
    }

    [Fact]
    public void WithSeveralRepositories_TheDotShowsTheWorst()
    {
        var core = Worktree("eco-core");
        var api = Worktree("eco-api");
        var row = new TaskRowViewModel(Listed(core, api), false);

        row.Worktree.Apply(new Dictionary<Guid, WorktreeSync>
        {
            [core.DevelopmentId] = Sync(core, WorktreeSyncState.Pushed, commits: 2),
            [api.DevelopmentId] = Sync(api, WorktreeSyncState.Unpushed, commits: 1, unpushed: 1),
        });

        row.Worktree.IsUnpushed.Should().BeTrue();
        row.Worktree.DotTip.Should().Be("2 worktrees com commits sem push");
        row.Worktree.Lines.Select(line => line.Detail).Should().Equal("2 commits, todos enviados", "1 commit sem push");
    }

    [Theory]
    [InlineData(WorktreeSyncState.Dirty, 3, 0, 0, "3 alterações não commitadas")]
    [InlineData(WorktreeSyncState.Dirty, 1, 2, 2, "1 alteração não commitada · 2 commits sem push")]
    [InlineData(WorktreeSyncState.Clean, 0, 0, 0, "sem commits ainda")]
    [InlineData(WorktreeSyncState.Unknown, 0, 0, 0, "não foi possível verificar")]
    public void TheTooltip_SaysItInWords(WorktreeSyncState state, int changes, int commits, int unpushed, string expected)
    {
        var worktree = Worktree();

        WorktreeLineViewModel.Describe(Sync(worktree, state, changes, commits, unpushed)).Should().Be(expected);
    }

    [Fact]
    public void ACompletedTaskThatKeptTheWorktree_IsHighlighted()
    {
        var row = new TaskRowViewModel(Listed(Worktree()), isCompleted: true);

        row.Worktree.IsRetained.Should().BeTrue();
        row.Worktree.RetainedLabel.Should().Be("● Worktree criado · eco-core · feature/x");
    }

    [Fact]
    public async Task TheColors_ComeFromGit_AfterTheBoardLoads()
    {
        var worktree = Worktree();
        _runner.Result = new TodayBoard(Date, [], [], [Listed(worktree)], [], []);
        _runner.ResultsByHandler[typeof(ProbeWorktreesHandler)] =
            new List<WorktreeSync> { Sync(worktree, WorktreeSyncState.Dirty, changes: 1) };
        var viewModel = ViewModel();

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        viewModel.Sections.Single().Items.Single().Worktree.IsDirty.Should().BeTrue();
        _runner.Invoked.Should().Contain(typeof(ProbeWorktreesHandler));
    }

    [Fact]
    public async Task GitFailing_NeverReachesTheErrorBanner()
    {
        var worktree = Worktree();
        _runner.Result = new TodayBoard(Date, [], [], [Listed(worktree)], [], []);
        _runner.FailuresByHandler[typeof(ProbeWorktreesHandler)] = new InvalidOperationException("git");
        var viewModel = ViewModel();

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        viewModel.ErrorMessage.Should().BeNull();
        viewModel.Sections.Single().Items.Single().Worktree.IsUnknown.Should().BeTrue();
    }

    [Fact]
    public async Task CompletingATaskWithoutWorktree_AsksNothing()
    {
        await ViewModel().ToggleAsync(new TaskRowViewModel(Listed(), false), TestContext.Current.CancellationToken);

        _confirmation.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task ReopeningATaskWithWorktree_AsksNothing()
    {
        await ViewModel().ToggleAsync(new TaskRowViewModel(Listed(Worktree()), true), TestContext.Current.CancellationToken);

        _runner.Invoked.Should().Contain(typeof(ReopenOccurrenceHandler));
        _confirmation.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task CompletingATaskWithWorktree_AsksAfterCompleting_AndKeepingRemovesNothing()
    {
        var worktree = Worktree();
        _runner.ResultsByHandler[typeof(ProbeWorktreesHandler)] =
            new List<WorktreeSync> { Sync(worktree, WorktreeSyncState.Unpushed, commits: 2, unpushed: 2) };
        _confirmation.Answer = false;

        await ViewModel().ToggleAsync(new TaskRowViewModel(Listed(worktree), false), TestContext.Current.CancellationToken);

        _runner.Invoked.Should().Contain(typeof(CompleteOccurrenceHandler));
        _runner.Invoked.Should().NotContain(typeof(RemoveWorktreeHandler));

        var asked = _confirmation.Asked.Should().ContainSingle().Subject;
        asked.Headline.Should().Be("Remover o worktree desta tarefa?");
        asked.CancelLabel.Should().Be("Manter worktree");
        asked.IsIrreversible.Should().BeTrue();
        asked.Message.Should().Contain("2 commits ainda não foram enviados (push)");
    }

    [Fact]
    public async Task AnsweringRemove_RemovesTheWorktree()
    {
        _runner.ResultsByHandler[typeof(RemoveWorktreeHandler)] = Removed();
        _confirmation.Answer = true;
        var viewModel = ViewModel();

        await viewModel.ToggleAsync(new TaskRowViewModel(Listed(Worktree()), false), TestContext.Current.CancellationToken);

        _runner.Invoked.Should().Contain(typeof(RemoveWorktreeHandler));
        viewModel.StatusMessage.Should().Be("Worktree removido.");
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task ARefusedRemoval_IsExplained_AndTheTaskStaysCompleted()
    {
        _runner.FailuresByHandler[typeof(RemoveWorktreeHandler)] =
            new DomainException("O worktree tem 1 alteração não commitada. Nada foi removido.");
        _confirmation.Answer = true;
        var viewModel = ViewModel();

        await viewModel.ToggleAsync(new TaskRowViewModel(Listed(Worktree()), false), TestContext.Current.CancellationToken);

        _runner.Invoked.Should().Contain(typeof(CompleteOccurrenceHandler));
        _runner.Invoked.Should().NotContain(typeof(ReopenOccurrenceHandler));
        viewModel.ErrorMessage.Should().Be(
            "eco-core: O worktree tem 1 alteração não commitada. Nada foi removido. "
            + "Abra a tarefa na aba Desenvolvimento para resolver.");
    }
}
