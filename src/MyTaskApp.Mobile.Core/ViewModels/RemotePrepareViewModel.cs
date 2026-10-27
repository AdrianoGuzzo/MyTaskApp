using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mobile.Core.ViewModels;

/// <summary>Uma opção de lista com o nome que a tela mostra.</summary>
public sealed record RemoteOption<T>(T Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// O "Preparar em…" do celular (E4, E6): a tarefa, numa máquina pareada, com o
/// repositório, os comandos depois do worktree, a cópia do banco e o Claude.
/// Os mesmos pedidos do desktop; quem confere e decide é a outra máquina.
/// </summary>
public sealed partial class RemotePrepareViewModel(
    IUseCaseRunner runner,
    RemoteSender sender,
    IDialogService dialogs,
    ILogger<RemotePrepareViewModel> logger) : MobileViewModel(dialogs, logger)
{
    private Guid _taskId;

    [ObservableProperty]
    private RemoteOption<MachineView>? _selectedMachine;

    [ObservableProperty]
    private RemoteOption<CatalogDirectory>? _selectedRepository;

    [ObservableProperty]
    private string _branchName = string.Empty;

    [ObservableProperty]
    private bool _runCommands = true;

    [ObservableProperty]
    private bool _copyDatabase;

    [ObservableProperty]
    private RemoteOption<CatalogCopyProfile>? _selectedCopyProfile;

    [ObservableProperty]
    private bool _startAgent;

    [ObservableProperty]
    private RemoteOption<CatalogAgentProfile>? _selectedAgentProfile;

    [ObservableProperty]
    private string _agentPrompt = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _problem;

    public string TaskTitle { get; private set; } = string.Empty;

    public ObservableCollection<RemoteOption<MachineView>> Machines { get; } = [];

    public ObservableCollection<RemoteOption<CatalogDirectory>> Repositories { get; } = [];

    public ObservableCollection<RemoteOption<CatalogCopyProfile>> CopyProfiles { get; } = [];

    public ObservableCollection<RemoteOption<CatalogAgentProfile>> AgentProfiles { get; } = [];

    public bool HasCopyProfiles => CopyProfiles.Count > 0;

    public bool HasAgentProfiles => AgentProfiles.Count > 0;

    public async Task LoadAsync(Guid taskId)
    {
        _taskId = taskId;

        try
        {
            TaskTitle = (await runner.RunAsync<GetTaskDetailsHandler, TaskListItem>(
                (handler, token) => handler.HandleAsync(new GetTaskDetails(taskId), token))).Task.Title;
            OnPropertyChanged(nameof(TaskTitle));

            var view = await runner.RunAsync<GetMachinesHandler, MachinesView>(
                (handler, token) => handler.HandleAsync(new GetMachines(), token));
            Problem = view.Availability == RemoteAvailability.Ready ? null : view.Problem ?? "Ligue o controle remoto na aba Remoto.";

            Machines.Clear();

            foreach (var machine in view.Machines.Where(machine => !machine.IsThis && machine.AcceptsControl))
            {
                Machines.Add(new RemoteOption<MachineView>(machine, machine.IsOnline ? machine.Nickname : $"{machine.Nickname} (desligada)"));
            }

            SelectedMachine = Machines.Count == 1 ? Machines[0] : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogError(exception, "MobileRemotePrepareLoadFailed");
            Problem = UnexpectedFailure;
        }
    }

    partial void OnSelectedMachineChanged(RemoteOption<MachineView>? value)
    {
        Repositories.Clear();
        CopyProfiles.Clear();
        AgentProfiles.Clear();
        SelectedRepository = null;
        SelectedCopyProfile = null;
        SelectedAgentProfile = null;

        if (value is not null)
        {
            _ = LoadCatalogAsync(value);
        }
    }

    internal async Task LoadCatalogAsync(RemoteOption<MachineView> machine)
    {
        try
        {
            var view = await runner.RunAsync<GetRemoteControlHandler, RemoteControlView>(
                (handler, token) => handler.HandleAsync(new GetRemoteControl(machine.Value.DeviceId), token));

            if (SelectedMachine != machine)
            {
                return;
            }

            ErrorMessage = null;
            Fill(view.Catalog);
        }
        catch (SyncApiException exception) when (exception.Kind == SyncFailureKind.NotFound)
        {
            ErrorMessage = $"{machine.Value.Nickname} ainda não publicou o que dá para pedir a ela. Ela precisa estar aceitando controle.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogError(exception, "MobileRemotePrepareCatalogFailed");
            ErrorMessage = UnexpectedFailure;
        }
    }

    /// <summary>Monta o pedido, ou diz o que falta; nulo = falta algo.</summary>
    public EnvironmentProvisioningRequest? CreateRequest()
    {
        if (SelectedRepository is not { } repository)
        {
            ErrorMessage = "Escolha o repositório.";
            return null;
        }

        DatabaseCopyRequest? database = null;
        ClaudeCodeSessionRequest? agent = null;

        if (CopyDatabase)
        {
            if (SelectedCopyProfile?.Value is not { } profile)
            {
                ErrorMessage = "Escolha o perfil de cópia do banco.";
                return null;
            }

            database = new DatabaseCopyRequest(profile.SourceConnectionId, null, profile.TargetConnectionId, null, null, profile.CopyProfileId);
        }

        if (StartAgent)
        {
            if (SelectedAgentProfile?.Value is not { } agentProfile)
            {
                ErrorMessage = "Escolha o perfil do Claude.";
                return null;
            }

            if (AgentPrompt.Trim() is not { Length: > 0 } prompt)
            {
                ErrorMessage = "Escreva o primeiro prompt do Claude.";
                return null;
            }

            // O ambiente ainda não existe: lá, vale o que a preparação criar.
            agent = new ClaudeCodeSessionRequest(Guid.Empty, agentProfile.Id, null, null, new PromptRequest(null, prompt));
        }

        ErrorMessage = null;
        return new EnvironmentProvisioningRequest(
            _taskId,
            repository.Value.TagDirectoryId,
            null,
            null,
            BranchName.Trim() is { Length: > 0 } branch ? branch : null,
            WorktreeConflictPolicy.Fail,
            RunCommands,
            database,
            agent);
    }

    [RelayCommand]
    public async Task PrepareAsync()
    {
        if (SelectedMachine is not { } machine)
        {
            ErrorMessage = "Escolha a máquina.";
            return;
        }

        if (CreateRequest() is not { } request)
        {
            return;
        }

        var what = request.Database is not null || request.StartAgent is not null ? "Preparar tudo" : "Preparar";

        await TryAsync(
            () => sender.SendAsync(RemoteRequests.Provision(machine.Value.DeviceId, request), $"{what} · {TaskTitle}"),
            "MobileRemotePrepareFailed");
    }

    private void Fill(AgentCatalog catalog)
    {
        foreach (var directory in catalog.Directories
            .Where(directory => directory.MappedHere && directory.IsGitRepository)
            .OrderBy(directory => directory.Label, StringComparer.CurrentCultureIgnoreCase))
        {
            Repositories.Add(new RemoteOption<CatalogDirectory>(directory, directory.Label));
        }

        SelectedRepository = Repositories.Count == 1 ? Repositories[0] : null;

        var databases = catalog.Databases.ToDictionary(database => database.ConnectionId);

        foreach (var profile in catalog.CopyProfiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            CopyProfiles.Add(new RemoteOption<CatalogCopyProfile>(profile, RemoteOperationTexts.CopyProfile(profile, databases)));
        }

        SelectedCopyProfile = CopyProfiles.Count == 1 ? CopyProfiles[0] : null;

        foreach (var profile in catalog.AgentProfiles)
        {
            AgentProfiles.Add(new RemoteOption<CatalogAgentProfile>(profile, profile.Name));
        }

        SelectedAgentProfile = AgentProfiles.Count == 1 ? AgentProfiles[0] : null;
        OnPropertyChanged(nameof(HasCopyProfiles));
        OnPropertyChanged(nameof(HasAgentProfiles));
    }
}
