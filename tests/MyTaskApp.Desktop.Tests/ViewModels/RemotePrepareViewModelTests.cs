using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// "Preparar em…" na aba Desenvolvimento (T-25): para quem dá para pedir, os
/// repositórios de lá, o pedido e a tela "Operação" acompanhando.
/// </summary>
public class RemotePrepareViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly MachineView This = Machine("Desktop", isThis: true);

    private static readonly MachineView Notebook = Machine("Notebook");

    private static readonly CatalogDirectory Eco = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "ECO · Núcleo", MappedHere: true, IsGitRepository: true);

    private static readonly Guid TaskId = Guid.CreateVersion7();

    private static readonly CatalogDatabase Production = new(Guid.CreateVersion7(), "ECO Produção", "Production", CanBeSource: true, CanBeTarget: false);

    private static readonly CatalogDatabase Local = new(Guid.CreateVersion7(), "Local", "Development", CanBeSource: true, CanBeTarget: true);

    private static readonly CatalogCopyProfile Lgpd = new(Guid.CreateVersion7(), "Produção LGPD", Production.ConnectionId, Local.ConnectionId, Anonymizes: true);

    private readonly FakeUseCaseRunner _runner = new();

    private readonly ISyncAccount _account = Substitute.For<ISyncAccount>();

    private readonly List<RemoteControlViewModel> _opened = [];

    public RemotePrepareViewModelTests()
    {
        _account.IsAvailable.Returns(true);
        _runner.ResultsByHandler[typeof(GetMachinesHandler)] = Machines(This, Notebook, Machine("Desligado", online: false), Machine("Fechado", accepts: false));
        _runner.ResultsByHandler[typeof(GetRemoteControlHandler)] = new RemoteControlView(
            new AgentCatalog(
                "h",
                [Eco, Eco with { TagDirectoryId = Guid.CreateVersion7(), Label = "Sem pasta", MappedHere = false }],
                [],
                [Production, Local],
                [new CatalogAgentProfile("read", "Ler", "Só lê o repositório."), new CatalogAgentProfile("edit", "Editar", "Edita os arquivos.")])
            {
                CopyProfiles = [Lgpd],
            },
            []);
        _runner.ResultsByHandler[typeof(GetPendingApprovalsHandler)] = (IReadOnlyList<ApprovalView>)[];
        _runner.ResultsByHandler[typeof(SendRemoteOperationHandler)] = new RemoteOperationView(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Notebook.DeviceId, Guid.CreateVersion7(), RemoteOperationKinds.WorktreeProvision, TaskId,
            RemoteOperationStatus.Queued, Now, Now.AddMinutes(5), Now, null, null, null, 0);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RemotePrepareViewModel ViewModel()
    {
        var viewModel = new RemotePrepareViewModel(
            _runner,
            _account,
            () => new RemoteControlViewModel(_runner, NullLogger<RemoteControlViewModel>.Instance),
            NullLogger<RemotePrepareViewModel>.Instance);
        viewModel.ControlOpened += (_, control) => _opened.Add(control);
        viewModel.Load(TaskId, isReadOnly: false);
        return viewModel;
    }

    [AvaloniaFact]
    public async Task OnlyAnotherMachineOnlineThatAcceptsControl_IsOffered()
    {
        var viewModel = ViewModel();

        await viewModel.RefreshAsync(Ct);

        viewModel.IsAvailable.Should().BeTrue();
        viewModel.Machines.Select(machine => machine.Name).Should().Equal("Notebook");
    }

    [AvaloniaFact]
    public async Task WithoutTheAccount_OrReadOnly_OrNobodyThere_TheButtonDoesNotShow()
    {
        _account.IsAvailable.Returns(false);
        var offline = ViewModel();
        await offline.RefreshAsync(Ct);
        offline.IsAvailable.Should().BeFalse();

        _account.IsAvailable.Returns(true);
        _runner.ResultsByHandler[typeof(GetMachinesHandler)] = Machines(This);
        var alone = ViewModel();
        await alone.RefreshAsync(Ct);
        alone.IsAvailable.Should().BeFalse();

        var readOnly = ViewModel();
        readOnly.Load(TaskId, isReadOnly: true);
        await readOnly.RefreshAsync(Ct);
        readOnly.IsAvailable.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Opening_ListsTheRepositoriesMappedThere_AndPicksTheOnlyOne()
    {
        var viewModel = ViewModel();
        await viewModel.RefreshAsync(Ct);

        viewModel.Open();
        await Until(() => viewModel.Repositories.Count > 0);

        viewModel.SelectedMachine!.Name.Should().Be("Notebook");
        viewModel.Repositories.Select(repository => repository.Name).Should().Equal("ECO · Núcleo");
        viewModel.SelectedRepository.Should().Be(viewModel.Repositories[0]);
        viewModel.HasNoRepositories.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Preparing_SyncsFirst_SendsTheRequest_AndOpensTheOperationScreen()
    {
        var viewModel = ViewModel();
        await viewModel.RefreshAsync(Ct);
        viewModel.Open();
        await Until(() => viewModel.SelectedRepository is not null);
        viewModel.BranchName = "feature/ECO-12";

        await viewModel.PrepareCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.Should().BeNull();
        _account.Received(1).SyncNow();
        _runner.Invoked.Should().Contain(typeof(SendRemoteOperationHandler));
        var control = _opened.Should().ContainSingle().Subject;
        control.OperationTitle.Should().Be("Preparar o worktree em Notebook · ECO · Núcleo");
        control.HasOperation.Should().BeTrue();
        viewModel.IsOpen.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task AnInvalidBranch_OrNoRepository_IsNotSent()
    {
        var viewModel = ViewModel();
        await viewModel.RefreshAsync(Ct);
        viewModel.Open();
        await Until(() => viewModel.SelectedRepository is not null);

        viewModel.BranchName = "nome com espaço";
        viewModel.BranchError.Should().NotBeNull();
        await viewModel.PrepareCommand.ExecuteAsync(null);
        viewModel.ErrorMessage.Should().Be(viewModel.BranchError);

        viewModel.BranchName = string.Empty;
        viewModel.SelectedRepository = null;
        await viewModel.PrepareCommand.ExecuteAsync(null);
        viewModel.ErrorMessage.Should().Be("Escolha o repositório.");

        _runner.Invoked.Should().NotContain(typeof(SendRemoteOperationHandler));
        _opened.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task PreparingEverything_SendsTheCopyProfileAndTheFirstPrompt()
    {
        var viewModel = ViewModel();
        await viewModel.RefreshAsync(Ct);
        viewModel.Open();
        await Until(() => viewModel.SelectedRepository is not null);

        viewModel.HasCopyProfiles.Should().BeTrue();
        viewModel.CopyProfiles.Single().Name.Should().Be("Produção LGPD (ECO Produção → Local, produção: pede aprovação)");
        viewModel.SelectedCopyProfile.Should().Be(viewModel.CopyProfiles[0], "o único perfil já vem escolhido");
        viewModel.SelectedAgentProfile!.Id.Should().Be("read");

        viewModel.CopyDatabase = true;
        viewModel.StartAgent = true;
        viewModel.SelectedAgentProfile = viewModel.AgentProfiles[1];
        viewModel.AgentPrompt = "  Leia o README.  ";

        var request = viewModel.CreateRequest(viewModel.SelectedRepository!)!;

        request.Database.Should().Be(new DatabaseCopyRequest(Production.ConnectionId, null, Local.ConnectionId, null, null, Lgpd.CopyProfileId));
        request.StartAgent!.ProfileId.Should().Be("edit");
        request.StartAgent.Prompt.Should().Be(new PromptRequest(null, "Leia o README."));
        request.RunPostWorktreeCommands.Should().BeTrue();

        await viewModel.PrepareCommand.ExecuteAsync(null);
        _opened.Should().ContainSingle().Which.OperationTitle.Should().Be("Preparar tudo em Notebook · ECO · Núcleo");
    }

    [AvaloniaFact]
    public async Task ClaudeWithoutAPrompt_OrACopyWithoutAProfile_IsNotSent()
    {
        var viewModel = ViewModel();
        await viewModel.RefreshAsync(Ct);
        viewModel.Open();
        await Until(() => viewModel.SelectedRepository is not null);

        viewModel.StartAgent = true;
        await viewModel.PrepareCommand.ExecuteAsync(null);
        viewModel.ErrorMessage.Should().Be("Escreva o primeiro prompt do Claude.");

        viewModel.StartAgent = false;
        viewModel.CopyDatabase = true;
        viewModel.SelectedCopyProfile = null;
        await viewModel.PrepareCommand.ExecuteAsync(null);
        viewModel.ErrorMessage.Should().Be("Escolha o perfil de cópia do banco.");

        _runner.Invoked.Should().NotContain(typeof(SendRemoteOperationHandler));
    }

    [AvaloniaFact]
    public async Task AMachineThatNeverPublished_SaysSo()
    {
        _runner.FailuresByHandler[typeof(GetRemoteControlHandler)] = new SyncApiException(SyncFailureKind.NotFound, "404");
        var viewModel = ViewModel();
        await viewModel.RefreshAsync(Ct);

        viewModel.Open();
        await Until(() => viewModel.ErrorMessage is not null);

        viewModel.ErrorMessage.Should().Be("Notebook ainda não publicou os repositórios dela.");
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10, Ct);
        }

        condition().Should().BeTrue();
    }

    private static MachinesView Machines(params MachineView[] machines) =>
        new(new RemoteSettings(true, true), RemoteAvailability.Ready, null, machines, [], []);

    private static MachineView Machine(string name, bool isThis = false, bool online = true, bool accepts = true) =>
        new(Guid.CreateVersion7(), name, "windows", "1.25.0", isThis, online, Now, accepts, null, []);
}
