using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A aba "Worktrees" (ADR-065): tudo vem marcado, menos o que pede um segundo
/// olhar; a pergunta conta o que se perde; recusar não apaga nada.
/// </summary>
public class WorktreeCleanupViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeConfirmationDialog _confirmation = new();

    private static readonly WorktreeCleanupCandidate Orphan = new(
        @"C:\Projects\eco-core",
        @"C:\Projects\eco-core-experimento",
        "experimento",
        WorktreeCleanupReason.NoTask,
        null,
        null,
        null,
        new WorktreeSync(Guid.NewGuid(), WorktreeSyncState.Dirty, Changes: 3));

    private static readonly WorktreeCleanupCandidate Completed = new(
        @"C:\Projects\eco-core",
        @"C:\Projects\eco-core-feature-x",
        "feature/x",
        WorktreeCleanupReason.Completed,
        Guid.NewGuid(),
        "Corrigir cálculo",
        Guid.NewGuid(),
        new WorktreeSync(Guid.NewGuid(), WorktreeSyncState.Clean));

    private static readonly WorktreeCleanupCandidate Locked = Orphan with
    {
        WorktreePath = @"C:\Projects\eco-core-travado",
        Branch = "travado",
        IsLocked = true,
        Sync = new WorktreeSync(Guid.NewGuid(), WorktreeSyncState.Clean),
    };

    private static readonly WorktreeCleanupCandidate WithAgent = Completed with
    {
        WorktreePath = @"C:\Projects\eco-core-feature-y",
        Branch = "feature/y",
        HasActiveAgent = true,
    };

    private WorktreeCleanupViewModel Create(params WorktreeCleanupCandidate[] candidates)
    {
        _runner.ResultsByHandler[typeof(FindWorktreeCleanupHandler)] = new WorktreeCleanupScan(candidates, []);

        return new WorktreeCleanupViewModel(_runner, _confirmation, NullLogger<WorktreeCleanupViewModel>.Instance);
    }

    [Fact]
    public async Task Scanning_SelectsEverything_ButTheLockedAndTheOneWithAnAgent()
    {
        var viewModel = Create(Orphan, Completed, Locked, WithAgent);

        await viewModel.ScanAsync(Ct);

        viewModel.Items.Select(item => (item.Path, item.IsSelected)).Should().Equal(
            (Orphan.WorktreePath, true),
            (Completed.WorktreePath, true),
            (Locked.WorktreePath, false),
            (WithAgent.WorktreePath, false));
        viewModel.Items[3].CanSelect.Should().BeFalse();
        viewModel.CleanLabel.Should().Be("Limpar 2 worktrees");
        viewModel.CanClean.Should().BeTrue();
    }

    [Fact]
    public async Task TheLines_SayWhyAndWhatIsLost()
    {
        var viewModel = Create(Orphan, Completed);

        await viewModel.ScanAsync(Ct);

        viewModel.Items[0].ReasonLabel.Should().Be("Sem tarefa");
        viewModel.Items[0].Title.Should().Be("eco-core · experimento");
        viewModel.Items[0].IsDirty.Should().BeTrue();
        viewModel.Items[0].Warning.Should().Be("3 alterações não commitadas serão perdidas");
        viewModel.Items[1].ReasonLabel.Should().Be("Tarefa concluída: Corrigir cálculo");
        viewModel.Items[1].IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task SelectAll_AndNone_FollowTheCountAndNeverTouchTheBlockedOne()
    {
        var viewModel = Create(Orphan, Locked, WithAgent);
        await viewModel.ScanAsync(Ct);

        viewModel.SelectNone();
        viewModel.CanClean.Should().BeFalse();

        viewModel.SelectAll();
        viewModel.SelectedCount.Should().Be(2, "o do agente aberto não pode ser marcado");
        viewModel.CleanLabel.Should().Be("Limpar 2 worktrees");
    }

    [Fact]
    public async Task Refusing_RemovesNothing()
    {
        var viewModel = Create(Orphan, Completed);
        await viewModel.ScanAsync(Ct);

        await viewModel.CleanAsync(Ct);

        _confirmation.LastAsked!.IsIrreversible.Should().BeTrue();
        _runner.Invoked.Should().NotContain(typeof(CleanWorktreesHandler));
    }

    [Fact]
    public async Task ThePrompt_CountsTheWorktrees_AndWarnsAboutTheChanges()
    {
        var viewModel = Create(Orphan, Completed);
        await viewModel.ScanAsync(Ct);

        await viewModel.CleanAsync(Ct);

        var prompt = _confirmation.LastAsked!;
        prompt.Headline.Should().Be("Apagar 2 worktrees?");
        prompt.Message.Should().Contain("• eco-core · experimento — Sem tarefa");
        prompt.Message.Should().Contain("1 deles tem alterações não commitadas, que serão perdidas");
        prompt.Message.Should().Contain("As branches continuam no repositório");
        prompt.CancelLabel.Should().Be("Manter worktrees");
    }

    [Fact]
    public async Task Confirming_Cleans_RescansAndTellsTheList()
    {
        var viewModel = Create(Orphan, Completed);
        await viewModel.ScanAsync(Ct);
        _confirmation.Answer = true;
        _runner.ResultsByHandler[typeof(CleanWorktreesHandler)] = new WorktreeCleanupResult(2, []);
        var changed = 0;
        viewModel.WorktreesChanged += () => changed++;

        await viewModel.CleanAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(CleanWorktreesHandler));
        _runner.Invoked.Count(type => type == typeof(FindWorktreeCleanupHandler)).Should().Be(2, "a lista é varrida de novo");
        changed.Should().Be(1);
        viewModel.StatusMessage.Should().Be("2 worktrees removidos. As branches continuam no repositório.");
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task APartialCleanup_ShowsWhatStayedAndWhy()
    {
        var viewModel = Create(Orphan, Completed);
        await viewModel.ScanAsync(Ct);
        _confirmation.Answer = true;
        _runner.ResultsByHandler[typeof(CleanWorktreesHandler)] = new WorktreeCleanupResult(
            1,
            [new WorktreeCleanupFailure(Orphan.WorktreePath, "A pasta está aberta em pwsh (PID 42).")]);

        await viewModel.CleanAsync(Ct);

        viewModel.StatusMessage.Should().StartWith("1 worktree removido.");
        viewModel.ErrorMessage.Should().Be($"{Orphan.WorktreePath}: A pasta está aberta em pwsh (PID 42).");
    }

    [Fact]
    public async Task ARepositoryGitCouldNotRead_IsShown()
    {
        _runner.ResultsByHandler[typeof(FindWorktreeCleanupHandler)] =
            new WorktreeCleanupScan([], [@"C:\Projects\quebrado: fatal: not a git repository"]);
        var viewModel = new WorktreeCleanupViewModel(_runner, _confirmation, NullLogger<WorktreeCleanupViewModel>.Instance);

        await viewModel.ScanAsync(Ct);

        viewModel.ProblemsMessage.Should().Contain(@"C:\Projects\quebrado: fatal: not a git repository");
        viewModel.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task AFailedScan_ShowsTheMessage()
    {
        var viewModel = Create();
        _runner.FailuresByHandler[typeof(FindWorktreeCleanupHandler)] = new DomainException("O Git não está instalado.");

        await viewModel.ScanAsync(Ct);

        viewModel.ErrorMessage.Should().Be("O Git não está instalado.");
        viewModel.IsBusy.Should().BeFalse();
    }
}
