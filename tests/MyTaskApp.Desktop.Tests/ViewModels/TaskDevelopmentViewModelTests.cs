using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A aba Desenvolvimento no ViewModel (ADR-027): o que ela mostra em cada
/// estado e quais casos de uso pede. O que cada caso de uso faz com o Git está
/// coberto em Application e Infrastructure.
/// </summary>
public class TaskDevelopmentViewModelTests
{
    private const string Repository = @"C:\Projects\ecossistema-core";
    private const string Worktree = @"C:\Projects\ecossistema-core-feature-corrigir-calculo-de-animais";

    private static readonly Guid TaskId = Guid.CreateVersion7();

    private static readonly GitInstallation Installed = new(true, "2.51.0", "git");

    private static readonly IReadOnlyList<GitBranch> Branches =
    [
        GitBranch.Local("main", "refs/remotes/origin/main", isHead: true),
        GitBranch.Local("develop", "refs/remotes/origin/develop"),
        GitBranch.RemoteTracking("origin", "main"),
        GitBranch.RemoteTracking("origin", "develop"),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeClipboardWriter _clipboard = new();
    private readonly FakeShellLauncher _shell = new();
    private readonly FakeConfirmationDialog _confirmation = new();

    /// <summary>Parado: o atraso da digitação nunca vence sozinho, e o teste chama a inspeção.</summary>
    private readonly FakeTimeProvider _time = new();

    private static TaskDevelopmentView View(TaskDevelopmentStatus status, string? failure = null) =>
        new(Repository, "origin/develop", "feature/x", Worktree, status, DateTimeOffset.UnixEpoch, failure);

    private async Task<TaskDevelopmentViewModel> ActivatedAsync(
        GitInstallation? git = null,
        TaskDevelopmentView? development = null,
        bool isReadOnly = false)
    {
        _runner.Enqueue<GetTaskDevelopmentHandler>(development);

        if (!_runner.QueuedResults.ContainsKey(typeof(DetectGitHandler)))
        {
            _runner.ResultsByHandler[typeof(DetectGitHandler)] = git ?? Installed;
        }

        _runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = new DirectoryInspection(true, true, Repository);
        _runner.ResultsByHandler[typeof(ListBranchesHandler)] = new BranchList(Branches, Branches[0]);

        var viewModel = TestDevelopment.For(_runner, _clipboard, _shell, _confirmation, _time);
        viewModel.Load(TaskId, "Corrigir cálculo de animais", isReadOnly);
        await viewModel.ActivateAsync(Ct);

        return viewModel;
    }

    private static async Task ChooseRepositoryAsync(TaskDevelopmentViewModel viewModel)
    {
        viewModel.DirectoryText = Repository;
        await viewModel.InspectDirectoryAsync(Ct);
    }

    private static DevelopmentPlan Plan(WorktreeConflict? conflict = null) =>
        new(TaskId, Repository, Branches[0], "feature/corrigir-calculo-de-animais", Worktree, [], conflict);

    // --- Git -----------------------------------------------------------------

    [Fact]
    public async Task GitMissing_ShowsTheInstallGuide()
    {
        var viewModel = await ActivatedAsync(git: GitInstallation.Missing);

        viewModel.State.Should().Be(DevelopmentPanelState.GitMissing);
        viewModel.IsGitMissing.Should().BeTrue();
        viewModel.ShowForm.Should().BeFalse();
        viewModel.GitStatusText.Should().Be("✗ Git CLI não encontrado");
        viewModel.Instructions.Commands.Should().NotBeEmpty();
        viewModel.CanStart.Should().BeFalse();
    }

    [Fact]
    public async Task GitInstalled_ShowsTheVersion_AndTheForm()
    {
        var viewModel = await ActivatedAsync();

        viewModel.State.Should().Be(DevelopmentPanelState.Setup);
        viewModel.GitStatusText.Should().Be("✓ Git encontrado — versão 2.51.0");
        viewModel.ShowForm.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAgain_AfterInstalling_UnlocksTheForm()
    {
        _runner.Enqueue<DetectGitHandler>(GitInstallation.Missing, Installed);
        var viewModel = await ActivatedAsync();
        viewModel.State.Should().Be(DevelopmentPanelState.GitMissing);

        await viewModel.RecheckGitAsync(Ct);

        viewModel.State.Should().Be(DevelopmentPanelState.Setup);
        _runner.Invoked.Count(type => type == typeof(DetectGitHandler)).Should().Be(2);
    }

    [Fact]
    public async Task CopyingACommand_PutsItOnTheClipboard()
    {
        var viewModel = await ActivatedAsync(git: GitInstallation.Missing);

        await viewModel.CopyAsync(viewModel.Instructions.Commands[0].Command);

        _clipboard.LastWritten.Should().Be(viewModel.Instructions.Commands[0].Command);
        viewModel.Message.Should().Contain("Copiado");
    }

    [Fact]
    public async Task HowToInstall_OpensTheOfficialPage()
    {
        var viewModel = await ActivatedAsync(git: GitInstallation.Missing);

        await viewModel.OpenInstallPageAsync();

        _shell.OpenedUris.Should().ContainSingle().Which.Host.Should().Be("git-scm.com");
    }

    // --- Diretório e branches -------------------------------------------------

    [Fact]
    public async Task ARepository_IsValidated_AndItsBranchesAreLoadedGrouped()
    {
        var viewModel = await ActivatedAsync();

        await ChooseRepositoryAsync(viewModel);

        viewModel.DirectoryStatusText.Should().Be("✓ Diretório encontrado");
        viewModel.RepositoryStatusText.Should().Be("✓ Repositório Git válido");
        viewModel.BranchOptions.Select(option => option.Label).Should().Equal(
            "Branches locais", "main", "develop", "Branches remotas — origin", "origin/main", "origin/develop");
        viewModel.BranchOptions.Where(option => option.IsHeader).Should().HaveCount(2);
        viewModel.SelectedBranchOption!.Branch.Should().Be(Branches[0]);
    }

    [Fact]
    public async Task AFolderThatIsNotARepository_SaysSo_AndLoadsNoBranches()
    {
        var viewModel = await ActivatedAsync();
        _runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = new DirectoryInspection(true, false, null);

        await ChooseRepositoryAsync(viewModel);

        viewModel.RepositoryStatusText.Should().Be("✗ Não é um repositório Git");
        viewModel.BranchOptions.Should().BeEmpty();
        viewModel.CanStart.Should().BeFalse();
        _runner.Invoked.Should().NotContain(typeof(ListBranchesHandler));
    }

    [Fact]
    public async Task AMissingFolder_SaysSo()
    {
        var viewModel = await ActivatedAsync();
        _runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = DirectoryInspection.Missing;

        await ChooseRepositoryAsync(viewModel);

        viewModel.DirectoryStatusText.Should().Be("✗ Diretório não encontrado");
        viewModel.HasRepositoryStatus.Should().BeFalse();
    }

    [Fact]
    public async Task AnAliasBeingTyped_IsNotInspected()
    {
        var viewModel = await ActivatedAsync();

        viewModel.DirectoryText = "@eco";
        await viewModel.InspectDirectoryAsync(Ct);

        _runner.Invoked.Should().NotContain(typeof(InspectDirectoryHandler));
    }

    [Fact]
    public async Task TheDirectoryField_OffersTheAliasesOfTheTaskTags()
    {
        var viewModel = await ActivatedAsync();
        viewModel.DirectoryCompletion.SetDirectories(TaskNotesAliasTests.Directories, new Dictionary<Guid, bool>());

        viewModel.DirectoryCompletion.UpdateCompletion("@ecos", 5);

        viewModel.DirectoryCompletion.Suggestions.Select(item => item.Path)
            .Should().Equal(@"C:\Projects\ecossistema-core", @"C:\Projects\ecossistema-web");
    }

    [Fact]
    public async Task ChoosingAGroupHeader_KeepsThePreviousBranch()
    {
        var viewModel = await ActivatedAsync();
        await ChooseRepositoryAsync(viewModel);
        var develop = viewModel.BranchOptions.Single(option => option.Label == "develop");
        viewModel.SelectedBranchOption = develop;

        viewModel.SelectedBranchOption = viewModel.BranchOptions[0];

        viewModel.SelectedBranchOption.Should().BeSameAs(develop);
    }

    [Fact]
    public async Task TheNewBranch_IsSuggestedFromTheTitle_AndCanBeEdited()
    {
        var viewModel = await ActivatedAsync();
        await ChooseRepositoryAsync(viewModel);

        viewModel.NewBranchName.Should().Be("feature/corrigir-calculo-de-animais");
        viewModel.WorktreePreview.Should().Be(Worktree);

        viewModel.NewBranchName = "bugfix/456-correcao-vacinacao";

        viewModel.WorktreePreview.Should().Be(@"C:\Projects\ecossistema-core-bugfix-456-correcao-vacinacao");
        viewModel.CanStart.Should().BeTrue();
    }

    [Fact]
    public async Task AnInvalidNewBranch_ShowsWhy_AndBlocksStart()
    {
        var viewModel = await ActivatedAsync();
        await ChooseRepositoryAsync(viewModel);

        viewModel.NewBranchName = "feature com espaço";

        viewModel.HasBranchNameError.Should().BeTrue();
        viewModel.BranchNameError.Should().Contain("espaços");
        viewModel.WorktreePreview.Should().BeNull();
        viewModel.StartCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task AReadOnlyTask_CannotStart()
    {
        var viewModel = await ActivatedAsync(isReadOnly: true);
        await ChooseRepositoryAsync(viewModel);

        viewModel.CanStart.Should().BeFalse();
        viewModel.IsFormEnabled.Should().BeFalse();
    }

    // --- Iniciar implementação ------------------------------------------------

    [Fact]
    public async Task Start_PreparesThenCreates_AndEndsReady()
    {
        var viewModel = await ActivatedAsync();
        await ChooseRepositoryAsync(viewModel);
        _runner.ResultsByHandler[typeof(PrepareDevelopmentHandler)] = Plan();
        _runner.ResultsByHandler[typeof(StartDevelopmentHandler)] = View(TaskDevelopmentStatus.Ready);

        await viewModel.StartAsync();

        _runner.Invoked.Should().ContainInOrder(typeof(PrepareDevelopmentHandler), typeof(StartDevelopmentHandler));
        viewModel.State.Should().Be(DevelopmentPanelState.Ready);
        viewModel.IsReady.Should().BeTrue();
        viewModel.Development!.WorktreePath.Should().Be(Worktree);
        viewModel.Message.Should().Contain("Implementação iniciada");
    }

    [Fact]
    public async Task Start_ListsEveryStep_BeforeRunning()
    {
        var viewModel = await ActivatedAsync();
        await ChooseRepositoryAsync(viewModel);
        _runner.FailuresByHandler[typeof(PrepareDevelopmentHandler)] =
            new DevelopmentStepException(DevelopmentStep.CheckGit, "sem git");

        await viewModel.StartAsync();

        viewModel.Steps.Select(step => step.Step).Should().StartWith(
            [DevelopmentStep.CheckGit, DevelopmentStep.ValidateDirectory, DevelopmentStep.ValidateRepository]);
        viewModel.Steps.Last().Step.Should().Be(DevelopmentStep.SaveTask);
    }

    [Fact]
    public async Task AFailedStep_IsMarked_WithTheReasonAndTheGitDetails()
    {
        var viewModel = await ActivatedAsync();
        await ChooseRepositoryAsync(viewModel);
        var command = new GitCommandResult("git fetch --all --prune", 128, "", "fatal: Could not resolve host");
        _runner.FailuresByHandler[typeof(PrepareDevelopmentHandler)] =
            new DevelopmentStepException(DevelopmentStep.Fetch, "Não foi possível atualizar as referências remotas.", command);

        await viewModel.StartAsync();

        viewModel.State.Should().Be(DevelopmentPanelState.Failed);
        viewModel.ShowFailure.Should().BeTrue();
        viewModel.Failure!.StepTitle.Should().Be("Atualizando referências remotas");
        viewModel.Failure.Reason.Should().Be("Não foi possível atualizar as referências remotas.");
        viewModel.Failure.CommandLine.Should().Be("git fetch --all --prune");
        viewModel.Failure.ExitCode.Should().Be("128");
        viewModel.Failure.StandardError.Should().Contain("Could not resolve host");
        viewModel.Failure.StandardOutput.Should().Be("(vazio)");
        viewModel.Steps.Single(step => step.Step == DevelopmentStep.Fetch).IsFailed.Should().BeTrue();
        viewModel.CanStart.Should().BeTrue("dá para corrigir e tentar de novo");
        _runner.Invoked.Should().NotContain(typeof(StartDevelopmentHandler));
    }

    [Fact]
    public async Task LocalChangesThatBlock_AreListed()
    {
        var viewModel = await ActivatedAsync();
        await ChooseRepositoryAsync(viewModel);
        _runner.FailuresByHandler[typeof(PrepareDevelopmentHandler)] = new DevelopmentStepException(
            DevelopmentStep.UpdateSource, "A branch main está aberta com alterações.", changes: [" M src/App.cs"]);

        await viewModel.StartAsync();

        viewModel.Failure!.HasChanges.Should().BeTrue();
        viewModel.Failure.ChangesText.Should().Be(" M src/App.cs");

        viewModel.OpenRepositoryTerminal();
        _shell.OpenedTerminals.Should().Equal(Repository);
    }

    [Fact]
    public async Task Cancelling_EndsWithoutCreatingAnything()
    {
        var viewModel = await ActivatedAsync();
        await ChooseRepositoryAsync(viewModel);
        _runner.FailuresByHandler[typeof(PrepareDevelopmentHandler)] = new OperationCanceledException();

        await viewModel.StartAsync();

        viewModel.State.Should().Be(DevelopmentPanelState.Failed);
        viewModel.Failure!.Reason.Should().Contain("cancelada");
        _runner.Invoked.Should().NotContain(typeof(StartDevelopmentHandler));
    }

    [Fact]
    public async Task AnOccupiedPath_AsksWhatToDo_WithAFreeAlternative()
    {
        var viewModel = await ActivatedAsync();
        await ChooseRepositoryAsync(viewModel);
        var registered = new GitWorktree(Worktree.Replace('\\', '/'), "refs/heads/feature/antiga");
        _runner.ResultsByHandler[typeof(PrepareDevelopmentHandler)] =
            Plan(new WorktreeConflict(Worktree, registered, Worktree + "-2"));

        await viewModel.StartAsync();

        viewModel.State.Should().Be(DevelopmentPanelState.Conflict);
        viewModel.ConflictMessage.Should().Contain("já existe").And.Contain(Worktree);
        viewModel.CanAdopt.Should().BeTrue();
        viewModel.AdoptLabel.Should().Contain("feature/antiga");
        viewModel.AlternativePath.Should().Be(Worktree + "-2");
        _runner.Invoked.Should().NotContain(typeof(StartDevelopmentHandler));
    }

    [Fact]
    public async Task Conflict_UseExisting_AdoptsIt()
    {
        var viewModel = await ActivatedAsync();
        await ChooseRepositoryAsync(viewModel);
        var registered = new GitWorktree(Worktree.Replace('\\', '/'), "refs/heads/feature/antiga");
        _runner.ResultsByHandler[typeof(PrepareDevelopmentHandler)] =
            Plan(new WorktreeConflict(Worktree, registered, Worktree + "-2"));
        _runner.ResultsByHandler[typeof(StartDevelopmentHandler)] = View(TaskDevelopmentStatus.Ready);
        await viewModel.StartAsync();

        await viewModel.UseExistingAsync();

        viewModel.State.Should().Be(DevelopmentPanelState.Ready);
    }

    [Fact]
    public async Task Conflict_NotAWorktree_CannotBeAdopted_ButAnotherPathCan()
    {
        var viewModel = await ActivatedAsync();
        await ChooseRepositoryAsync(viewModel);
        _runner.ResultsByHandler[typeof(PrepareDevelopmentHandler)] =
            Plan(new WorktreeConflict(Worktree, null, Worktree + "-2"));
        _runner.ResultsByHandler[typeof(StartDevelopmentHandler)] = View(TaskDevelopmentStatus.Ready);
        await viewModel.StartAsync();

        viewModel.CanAdopt.Should().BeFalse();

        viewModel.ChooseAnotherPath();
        viewModel.IsChoosingAlternative.Should().BeTrue();
        await viewModel.UseAlternativePathAsync();

        viewModel.State.Should().Be(DevelopmentPanelState.Ready);
    }

    [Fact]
    public async Task Conflict_Cancel_GoesBackWithoutCreating()
    {
        var viewModel = await ActivatedAsync();
        await ChooseRepositoryAsync(viewModel);
        _runner.ResultsByHandler[typeof(PrepareDevelopmentHandler)] =
            Plan(new WorktreeConflict(Worktree, null, Worktree + "-2"));
        await viewModel.StartAsync();

        viewModel.CancelConflict();

        viewModel.State.Should().Be(DevelopmentPanelState.Setup);
        _runner.Invoked.Should().NotContain(typeof(StartDevelopmentHandler));
    }

    // --- Pronto ------------------------------------------------------------------

    [Fact]
    public async Task AReadyTask_OpensStraightOnTheEnvironment()
    {
        var viewModel = await ActivatedAsync(development: View(TaskDevelopmentStatus.Ready));

        viewModel.State.Should().Be(DevelopmentPanelState.Ready);
        _runner.Invoked.Should().NotContain(typeof(DetectGitHandler));
    }

    [Fact]
    public async Task Ready_OpenFolderTerminalAndCopyPath_UseTheWorktree()
    {
        var viewModel = await ActivatedAsync(development: View(TaskDevelopmentStatus.Ready));

        await viewModel.OpenFolderAsync();
        viewModel.OpenTerminal();
        await viewModel.CopyPathAsync();

        _shell.OpenedFolders.Should().Equal(Worktree);
        _shell.OpenedTerminals.Should().Equal(Worktree);
        _clipboard.LastWritten.Should().Be(Worktree);
    }

    [Fact]
    public async Task Ready_AFolderThatWillNotOpen_SaysSo()
    {
        _shell.Succeeds = false;
        var viewModel = await ActivatedAsync(development: View(TaskDevelopmentStatus.Ready));

        await viewModel.OpenFolderAsync();

        viewModel.Message.Should().Contain(Worktree);
    }

    [Fact]
    public async Task Remove_WithChanges_RemovesNothing_AndShowsThem()
    {
        var viewModel = await ActivatedAsync(development: View(TaskDevelopmentStatus.Ready));
        _runner.ResultsByHandler[typeof(InspectWorktreeHandler)] = new WorktreeInspection(true, [" M src/App.cs"]);

        await viewModel.RemoveWorktreeAsync();

        viewModel.IsRemoveBlocked.Should().BeTrue();
        _confirmation.Asked.Should().BeEmpty();
        _runner.Invoked.Should().NotContain(typeof(RemoveWorktreeHandler));

        viewModel.ShowRemoveChanges();
        viewModel.RemoveChangesText.Should().Be(" M src/App.cs");

        viewModel.CancelRemove();
        viewModel.IsRemoveBlocked.Should().BeFalse();
        viewModel.State.Should().Be(DevelopmentPanelState.Ready);
    }

    [Fact]
    public async Task Remove_Clean_AsksFirst_ThenRemoves()
    {
        var viewModel = await ActivatedAsync(development: View(TaskDevelopmentStatus.Ready));
        _runner.ResultsByHandler[typeof(InspectWorktreeHandler)] = new WorktreeInspection(true, []);
        _runner.ResultsByHandler[typeof(RemoveWorktreeHandler)] = View(TaskDevelopmentStatus.Removed);
        _confirmation.Answer = true;

        await viewModel.RemoveWorktreeAsync();

        _confirmation.LastAsked!.Headline.Should().Be("Deseja remover este Worktree?");
        _confirmation.LastAsked.IsIrreversible.Should().BeTrue();
        _runner.Invoked.Should().Contain(typeof(RemoveWorktreeHandler));
        viewModel.State.Should().Be(DevelopmentPanelState.Setup, "o formulário volta para começar de novo");
        viewModel.DirectoryText.Should().Be(Repository);
        viewModel.PreviousAttempt.Should().Contain("foi removido").And.Contain("feature/x");
    }

    [Fact]
    public async Task Remove_Declined_RemovesNothing()
    {
        var viewModel = await ActivatedAsync(development: View(TaskDevelopmentStatus.Ready));
        _runner.ResultsByHandler[typeof(InspectWorktreeHandler)] = new WorktreeInspection(true, []);

        await viewModel.RemoveWorktreeAsync();

        _runner.Invoked.Should().NotContain(typeof(RemoveWorktreeHandler));
        viewModel.State.Should().Be(DevelopmentPanelState.Ready);
    }

    private static DevelopmentStepException Locked(params DirectoryLocker[] lockers) =>
        new(DevelopmentStep.RemoveWorktree, $"O Git removeu o worktree, mas a pasta {Worktree} está sendo usada.", lockers: lockers)
        {
            IsDirectoryLocked = true,
        };

    /// <summary>ADR-029: a pasta presa mostra quem a segura, e só encerra depois do clique.</summary>
    [Fact]
    public async Task Remove_AHeldFolder_ShowsTheLockers_ThenForcingRemoves()
    {
        var terminal = new DirectoryLocker(4242, "pwsh", @"C:\Program Files\PowerShell\7\pwsh.exe", true);
        var viewModel = await ActivatedAsync(development: View(TaskDevelopmentStatus.Ready));
        _runner.ResultsByHandler[typeof(InspectWorktreeHandler)] = new WorktreeInspection(true, []);
        _runner.Enqueue<RemoveWorktreeHandler>(Locked(terminal), View(TaskDevelopmentStatus.Removed));
        _confirmation.Answer = true;

        await viewModel.RemoveWorktreeAsync();

        viewModel.IsRemoveLocked.Should().BeTrue();
        viewModel.CanForceRemove.Should().BeTrue();
        viewModel.RemoveLockedMessage.Should().Contain(Worktree);
        viewModel.RemoveLockersText.Should().Contain("pwsh (PID 4242)").And.Contain("pwsh.exe");
        viewModel.State.Should().Be(DevelopmentPanelState.Ready);

        await viewModel.ForceRemoveWorktreeAsync();

        _runner.Invoked.Count(type => type == typeof(RemoveWorktreeHandler)).Should().Be(2);
        viewModel.IsRemoveLocked.Should().BeFalse();
        viewModel.State.Should().Be(DevelopmentPanelState.Setup);
        viewModel.Message.Should().Contain("encerrados");
    }

    [Fact]
    public async Task Remove_AFolderHeldOnlyByTheApp_OffersNoForce()
    {
        var viewModel = await ActivatedAsync(development: View(TaskDevelopmentStatus.Ready));
        _runner.ResultsByHandler[typeof(InspectWorktreeHandler)] = new WorktreeInspection(true, []);
        _runner.Enqueue<RemoveWorktreeHandler>(Locked(new DirectoryLocker(1, "explorer", null, false)));
        _confirmation.Answer = true;

        await viewModel.RemoveWorktreeAsync();

        viewModel.IsRemoveLocked.Should().BeTrue();
        viewModel.CanForceRemove.Should().BeFalse();
        viewModel.RemoveLockersText.Should().Contain("não será encerrado");

        viewModel.CancelRemove();

        viewModel.IsRemoveLocked.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_TheLeftoverFolder_SaysGitAlreadyForgotIt()
    {
        var viewModel = await ActivatedAsync(development: View(TaskDevelopmentStatus.Ready));
        _runner.ResultsByHandler[typeof(InspectWorktreeHandler)] = new WorktreeInspection(true, [], IsLeftover: true);

        await viewModel.RemoveWorktreeAsync();

        _confirmation.LastAsked!.Message.Should().Contain("já esqueceu");
    }

    // --- Tentativa anterior -------------------------------------------------------

    [Fact]
    public async Task AFailedAttempt_IsExplained_AndTheFormComesFilled()
    {
        var viewModel = await ActivatedAsync(development: View(TaskDevelopmentStatus.Error, "A branch feature/x já existe."));

        viewModel.State.Should().Be(DevelopmentPanelState.Setup);
        viewModel.PreviousAttempt.Should().Contain("A branch feature/x já existe.");
        viewModel.DirectoryText.Should().Be(Repository);
        viewModel.NewBranchName.Should().Be("feature/x");
        viewModel.RepositoryValid.Should().BeTrue("a pasta da tentativa anterior é conferida na hora");
        viewModel.SelectedBranchOption!.Label.Should().Be("origin/develop");
    }

    [Fact]
    public async Task AnInterruptedCreation_IsExplained()
    {
        var viewModel = await ActivatedAsync(development: View(TaskDevelopmentStatus.Creating));

        viewModel.PreviousAttempt.Should().Contain("interrompida");
    }
}
