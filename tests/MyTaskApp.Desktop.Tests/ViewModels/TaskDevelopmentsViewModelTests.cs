using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Vários repositórios na aba Desenvolvimento (ADR-031): uma aba por ambiente
/// da tarefa, o painel da escolhida, e um repositório novo sugerido a partir
/// dos outros.
/// </summary>
public class TaskDevelopmentsViewModelTests
{
    private const string Core = @"C:\Projects\ecossistema-core";
    private const string Api = @"C:\Projects\ecossistema-api";

    private static readonly Guid TaskId = Guid.CreateVersion7();

    private static readonly IReadOnlyList<GitBranch> Branches =
    [
        GitBranch.Local("main", "refs/remotes/origin/main", isHead: true),
        GitBranch.RemoteTracking("origin", "main"),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeConfirmationDialog _confirmation = new();

    public TaskDevelopmentsViewModelTests()
    {
        _runner.ResultsByHandler[typeof(DetectGitHandler)] = new GitInstallation(true, "2.51.0", "git");
        _runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = new DirectoryInspection(true, true, Core);
        _runner.ResultsByHandler[typeof(ListBranchesHandler)] = new BranchList(Branches, Branches[0]);
    }

    private async Task<TaskDevelopmentsViewModel> ActivatedAsync(params IReadOnlyList<TaskDevelopmentView>[] lists)
    {
        _runner.Enqueue<GetTaskDevelopmentsHandler>([.. lists]);

        var viewModel = TestDevelopment.For(
            _runner, confirmation: _confirmation, timeProvider: new FakeTimeProvider());
        viewModel.Load(TaskId, "Fluxo de cadastro de animais", isReadOnly: false);
        await viewModel.ActivateAsync(Ct);

        return viewModel;
    }

    private static TaskDevelopmentView View(
        TaskDevelopmentStatus status,
        string repository = Core,
        string branch = "feature/fluxo-de-cadastro") =>
        TestDevelopment.View(TaskId, status, repository, branch);

    [Fact]
    public async Task WithoutEnvironments_ThereIsOnlyTheForm_AndNoTabs()
    {
        var viewModel = await ActivatedAsync(TestDevelopment.List());

        viewModel.Items.Should().ContainSingle().Which.IsDraft.Should().BeTrue();
        viewModel.Selected.Should().BeSameAs(viewModel.Items[0]);
        viewModel.Selected!.State.Should().Be(DevelopmentPanelState.Setup);
        viewModel.ShowStrip.Should().BeFalse();
    }

    [Fact]
    public async Task EachEnvironment_IsATab_AndTheReadyOneComesToTheFront()
    {
        var failed = View(TaskDevelopmentStatus.Error, Core) with { FailureReason = "falhou" };
        var ready = View(TaskDevelopmentStatus.Ready, Api);

        var viewModel = await ActivatedAsync(TestDevelopment.List(failed, ready));

        viewModel.ShowStrip.Should().BeTrue();
        viewModel.CanAddRepository.Should().BeTrue();
        viewModel.Items.Select(item => (item.ChipTitle, item.ChipBranch, item.ChipGlyph)).Should().Equal(
            ("ecossistema-core", "feature/fluxo-de-cadastro", "⚠"),
            ("ecossistema-api", "feature/fluxo-de-cadastro", "✓"));
        viewModel.Selected!.DevelopmentId.Should().Be(ready.Id);
        viewModel.Selected.State.Should().Be(DevelopmentPanelState.Ready);
    }

    /// <summary>O fluxo que atravessa repositórios costuma usar a mesma branch em todos.</summary>
    [Fact]
    public async Task AddingARepository_SuggestsTheBranchOfTheOthers_AndLeavesTheFolderEmpty()
    {
        var viewModel = await ActivatedAsync(TestDevelopment.List(View(TaskDevelopmentStatus.Ready)));

        viewModel.AddRepository();
        await Task.Yield();

        var draft = viewModel.Selected!;
        draft.IsDraft.Should().BeTrue();
        draft.NewBranchName.Should().Be("feature/fluxo-de-cadastro");
        draft.DirectoryText.Should().BeEmpty();
        draft.ChipTitle.Should().Be("Novo repositório");
        viewModel.Items.Should().HaveCount(2);
        viewModel.CanAddRepository.Should().BeFalse("um rascunho de cada vez");
    }

    [Fact]
    public async Task AddingASecondTime_GoesBackToTheSameDraft()
    {
        var viewModel = await ActivatedAsync(TestDevelopment.List(View(TaskDevelopmentStatus.Ready)));

        viewModel.AddRepository();
        var draft = viewModel.Selected;
        viewModel.Selected = viewModel.Items[0];
        viewModel.AddRepository();

        viewModel.Selected.Should().BeSameAs(draft);
        viewModel.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task AnEnvironmentThatLeftTheList_LosesItsTab()
    {
        var core = View(TaskDevelopmentStatus.Ready, Core);
        var api = View(TaskDevelopmentStatus.Removed, Api);

        var viewModel = await ActivatedAsync(TestDevelopment.List(core, api), TestDevelopment.List(core));
        viewModel.Items.Should().HaveCount(2);

        await viewModel.ActivateAsync(Ct);

        viewModel.Items.Select(item => item.DevelopmentId).Should().Equal(core.Id);
    }

    [Fact]
    public async Task Forgetting_AskFirst_ThenTheTabGoes_AndAnotherComesToTheFront()
    {
        var core = View(TaskDevelopmentStatus.Ready, Core);
        var api = View(TaskDevelopmentStatus.Removed, Api);
        var viewModel = await ActivatedAsync(TestDevelopment.List(core, api), TestDevelopment.List(core));
        _confirmation.Answer = true;

        viewModel.Selected = viewModel.Items[1];
        await Task.Yield();
        viewModel.Selected.CanForget.Should().BeTrue();

        await viewModel.Selected.ForgetAsync();

        _confirmation.LastAsked!.Headline.Should().Contain("Remover este repositório");
        _runner.Invoked.Should().Contain(typeof(ForgetDevelopmentHandler));
        viewModel.Items.Select(item => item.DevelopmentId).Should().Equal(core.Id);
        viewModel.Selected!.DevelopmentId.Should().Be(core.Id);
    }

    /// <summary>
    /// A criação falhou e ficou gravada: o rascunho vira aquele ambiente, com a
    /// falha na tela — e não ganha uma aba gêmea.
    /// </summary>
    [Fact]
    public async Task ADraftThatFailsToCreate_BecomesTheRecordedEnvironment()
    {
        var recorded = View(TaskDevelopmentStatus.Error) with { FailureReason = "A branch já existe." };
        var viewModel = await ActivatedAsync(TestDevelopment.List(), TestDevelopment.List(recorded));
        var draft = viewModel.Selected!;

        draft.DirectoryText = Core;
        await draft.InspectDirectoryAsync(Ct);
        _runner.ResultsByHandler[typeof(PrepareDevelopmentHandler)] =
            new DevelopmentPlan(TaskId, Core, Branches[0], "feature/fluxo-de-cadastro", Core + "-feature", [], null);
        _runner.FailuresByHandler[typeof(StartDevelopmentHandler)] =
            new DevelopmentStepException(DevelopmentStep.CreateWorktree, "A branch já existe.");

        await draft.StartAsync();

        viewModel.Items.Should().ContainSingle().Which.Should().BeSameAs(draft);
        draft.DevelopmentId.Should().Be(recorded.Id);
        draft.State.Should().Be(DevelopmentPanelState.Failed);
        viewModel.ShowStrip.Should().BeTrue();
    }

    [Fact]
    public async Task AReadyEnvironment_CannotBeForgotten()
    {
        var viewModel = await ActivatedAsync(TestDevelopment.List(View(TaskDevelopmentStatus.Ready)));

        viewModel.Selected!.CanForget.Should().BeFalse();
    }

    [Fact]
    public async Task ACompletedTask_DoesNotOfferAnotherRepository()
    {
        _runner.Enqueue<GetTaskDevelopmentsHandler>(TestDevelopment.List(View(TaskDevelopmentStatus.Ready)));
        var viewModel = TestDevelopment.For(_runner, timeProvider: new FakeTimeProvider());
        viewModel.Load(TaskId, "Fluxo", isReadOnly: true);
        await viewModel.ActivateAsync(Ct);

        viewModel.CanAddRepository.Should().BeFalse();
        viewModel.AddRepository();
        viewModel.Items.Should().ContainSingle();
    }

    /// <summary>A lista não veio: o formulário aparece com o recado, e não uma aba vazia.</summary>
    [Fact]
    public async Task WhenTheListFailsToLoad_TheFormSaysSo()
    {
        _runner.FailuresByHandler[typeof(GetTaskDevelopmentsHandler)] = new InvalidOperationException("banco");
        var viewModel = TestDevelopment.For(_runner, timeProvider: new FakeTimeProvider());
        viewModel.Load(TaskId, "Fluxo", isReadOnly: false);

        await viewModel.ActivateAsync(Ct);

        viewModel.Selected!.Message.Should().Contain("Não foi possível carregar");
        viewModel.Selected.State.Should().Be(DevelopmentPanelState.Setup);
    }

    // --- O repositório único das etiquetas --------------------------------------

    private static TagDirectoryRow TagDirectory(string path, string tag = "ECO CORE") =>
        new(Guid.NewGuid(), Guid.NewGuid(), tag, "#22C55E", "@eco", path, null, null);

    private static void Directories(TaskDevelopmentsViewModel viewModel, params TagDirectoryRow[] directories) =>
        viewModel.DirectoryCompletion.SetDirectories(directories, new Dictionary<Guid, bool>());

    /// <summary>Os diretórios chegam na ativação da janela, antes de a aba aparecer.</summary>
    [Fact]
    public async Task OneDirectoryInTheTags_ComesFilledIn_AndTheBranchesLoad()
    {
        _runner.Enqueue<GetTaskDevelopmentsHandler>(TestDevelopment.List());
        var viewModel = TestDevelopment.For(_runner, timeProvider: new FakeTimeProvider());
        viewModel.Load(TaskId, "Fluxo de cadastro de animais", isReadOnly: false);
        Directories(viewModel, TagDirectory(Core));

        await viewModel.ActivateAsync(Ct);

        var draft = viewModel.Selected!;
        draft.DirectoryText.Should().Be(Core);
        draft.RepositoryPath.Should().Be(Core);
        draft.SelectedBranchOption!.Branch.Should().Be(Branches[0]);
    }

    [Fact]
    public async Task DirectoriesArrivingAfterTheTab_StillFillTheEmptyForm()
    {
        var viewModel = await ActivatedAsync(TestDevelopment.List());

        Directories(viewModel, TagDirectory(Core));

        viewModel.Selected!.DirectoryText.Should().Be(Core);
    }

    [Fact]
    public async Task TwoRepositoriesInTheTags_LeaveTheChoiceToTheUser()
    {
        var viewModel = await ActivatedAsync(TestDevelopment.List());

        Directories(viewModel, TagDirectory(Core), TagDirectory(Api));

        viewModel.Selected!.DirectoryText.Should().BeEmpty();
    }

    [Fact]
    public async Task TheSameFolderInTwoTags_CountsOnce()
    {
        var viewModel = await ActivatedAsync(TestDevelopment.List());

        Directories(viewModel, TagDirectory(Core), TagDirectory(Core + @"\", "ECO API"));

        viewModel.Selected!.DirectoryText.Should().Be(Core);
    }

    [Fact]
    public async Task AnotherRepository_DoesNotGetTheOneAlreadyInTheTask()
    {
        var viewModel = await ActivatedAsync(TestDevelopment.List(View(TaskDevelopmentStatus.Ready, Core)));
        Directories(viewModel, TagDirectory(Core));

        viewModel.AddRepository();

        viewModel.Selected!.IsDraft.Should().BeTrue();
        viewModel.Selected.DirectoryText.Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherRepository_GetsTheTagOne_WhenTheTaskIsInAnother()
    {
        var viewModel = await ActivatedAsync(TestDevelopment.List(View(TaskDevelopmentStatus.Ready, Api)));
        Directories(viewModel, TagDirectory(Core));

        viewModel.AddRepository();

        viewModel.Selected!.DirectoryText.Should().Be(Core);
    }

    [Fact]
    public async Task APreviousAttempt_KeepsItsOwnRepository()
    {
        var failed = View(TaskDevelopmentStatus.Error, Api) with { FailureReason = "falhou" };
        var viewModel = await ActivatedAsync(TestDevelopment.List(failed));

        Directories(viewModel, TagDirectory(Core));

        viewModel.Selected!.DirectoryText.Should().Be(Api);
    }

    /// <summary>A cada ativação da janela os diretórios chegam de novo; o apagado fica apagado.</summary>
    [Fact]
    public async Task AFieldTheUserCleared_StaysEmpty()
    {
        var viewModel = await ActivatedAsync(TestDevelopment.List());
        Directories(viewModel, TagDirectory(Core));

        viewModel.Selected!.DirectoryText = string.Empty;
        Directories(viewModel, TagDirectory(Core));

        viewModel.Selected.DirectoryText.Should().BeEmpty();
    }

    [Fact]
    public async Task ACompletedTask_DoesNotFillTheDirectory()
    {
        _runner.Enqueue<GetTaskDevelopmentsHandler>(TestDevelopment.List());
        var viewModel = TestDevelopment.For(_runner, timeProvider: new FakeTimeProvider());
        viewModel.Load(TaskId, "Fluxo", isReadOnly: true);
        await viewModel.ActivateAsync(Ct);

        Directories(viewModel, TagDirectory(Core));

        viewModel.Selected!.DirectoryText.Should().BeEmpty();
    }
}
