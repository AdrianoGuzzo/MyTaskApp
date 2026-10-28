using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma máquina que dá para pedir: ligada, aceita controle e não é esta.</summary>
public sealed record RemoteMachineOption(MachineView View)
{
    public string Name => View.Nickname;
}

/// <summary>Um repositório da outra máquina, pelo diretório da etiqueta: o nome, nunca o caminho.</summary>
public sealed record RemoteRepositoryOption(CatalogDirectory Directory)
{
    public string Name => Directory.Label;
}

/// <summary>Um perfil de cópia de lá, com as conexões pelo nome e o aviso de produção.</summary>
public sealed record RemoteCopyProfileOption(CatalogCopyProfile Profile, string Name);

/// <summary>
/// "Preparar em…" na aba Desenvolvimento (T-25): a mesma preparação do
/// "Iniciar implementação", só que em outra máquina, com o app dela fechado.
/// Escolhe-se a máquina e o repositório; a origem e a branch, se ficarem
/// vazias, são escolhidas lá como a aba escolheria. O andamento abre na tela
/// "Operação", que mostra a saída e as aprovações.
/// </summary>
/// <remarks>
/// Com o banco e o Claude marcados, é o "Preparar tudo" (T-33): o mesmo pedido,
/// que lá valida a cópia (e espera a aprovação da produção) antes do Git, copia
/// depois do worktree, roda os comandos e abre a conversa.
/// </remarks>
public sealed partial class RemotePrepareViewModel(
    IUseCaseRunner runner,
    ISyncAccount account,
    Func<RemoteControlViewModel> control,
    ILogger<RemotePrepareViewModel> logger) : ObservableObject
{
    private Guid _taskId;

    private bool _isReadOnly;

    public ObservableCollection<RemoteMachineOption> Machines { get; } = [];

    public ObservableCollection<RemoteRepositoryOption> Repositories { get; } = [];

    /// <summary>Os perfis de cópia que a outra máquina liberou: as opções e a anonimização vêm dele.</summary>
    public ObservableCollection<RemoteCopyProfileOption> CopyProfiles { get; } = [];

    public ObservableCollection<CatalogAgentProfile> AgentProfiles { get; } = [];

    /// <summary>A tela "Operação" com o pedido em andamento: a janela abre.</summary>
    public event EventHandler<RemoteControlViewModel>? ControlOpened;

    /// <summary>Há para onde pedir: o botão aparece.</summary>
    [ObservableProperty]
    private bool _isAvailable;

    /// <summary>
    /// Esta máquina aceita controle: o agente dela pode rodar a sessão
    /// gerenciada (ADR-092), e o "Abrir Claude gerenciado" aparece.
    /// </summary>
    [ObservableProperty]
    private bool _canManage;

    private MachineView? _here;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private RemoteMachineOption? _selectedMachine;

    [ObservableProperty]
    private RemoteRepositoryOption? _selectedRepository;

    [ObservableProperty]
    private string _sourceBranch = string.Empty;

    [ObservableProperty]
    private string _branchName = string.Empty;

    [ObservableProperty]
    private bool _runCommands = true;

    [ObservableProperty]
    private bool _copyDatabase;

    [ObservableProperty]
    private RemoteCopyProfileOption? _selectedCopyProfile;

    [ObservableProperty]
    private bool _startAgent;

    [ObservableProperty]
    private CatalogAgentProfile? _selectedAgentProfile;

    [ObservableProperty]
    private string _agentPrompt = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasNoRepositories => SelectedMachine is not null && Repositories.Count == 0 && !IsBusy;

    public bool HasCopyProfiles => CopyProfiles.Count > 0;

    public bool HasAgentProfiles => AgentProfiles.Count > 0;

    public string? BranchError => BranchName.Trim() is { Length: > 0 } branch ? GitBranchName.Validate(branch) : null;

    public void Load(Guid taskId, bool isReadOnly)
    {
        _taskId = taskId;
        _isReadOnly = isReadOnly;
        IsOpen = false;
        IsAvailable = false;
        CanManage = false;
        _here = null;
    }

    /// <summary>Quem está ligado agora. Sem o remoto pronto, ou sem ninguém, o botão some.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_isReadOnly || !account.IsAvailable)
        {
            IsAvailable = false;
            CanManage = false;
            return;
        }

        try
        {
            var view = await runner.RunAsync<GetMachinesHandler, MachinesView>(
                (handler, token) => handler.HandleAsync(new GetMachines(), token),
                cancellationToken);

            var selected = SelectedMachine?.View.DeviceId;
            Machines.Clear();

            if (view.Availability == RemoteAvailability.Ready)
            {
                foreach (var machine in view.Machines.Where(machine => machine.IsOnline && machine.AcceptsControl && !machine.IsThis))
                {
                    Machines.Add(new RemoteMachineOption(machine));
                }
            }

            SelectedMachine = Machines.FirstOrDefault(machine => machine.View.DeviceId == selected) ?? (IsOpen ? Machines.FirstOrDefault() : null);
            IsAvailable = Machines.Count > 0;
            _here = view.Availability == RemoteAvailability.Ready && view.Settings.AcceptControl
                ? view.Machines.FirstOrDefault(machine => machine.IsThis && machine.AcceptsControl)
                : null;
            CanManage = _here is not null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogInformation(exception, "RemotePrepareMachinesUnavailable");
            IsAvailable = false;
            CanManage = false;
        }
    }

    /// <summary>
    /// "Abrir Claude gerenciado" (ADR-092): a tela "Operação" desta própria
    /// máquina, no ambiente da tarefa. A conversa roda pelo agente daqui, sem
    /// terminal, e aparece no celular e nos outros computadores.
    /// </summary>
    public async Task OpenManagedAsync(Guid developmentId)
    {
        if (_here is not { } here)
        {
            return;
        }

        var opened = control();
        ControlOpened?.Invoke(this, opened);
        await opened.OpenAsync(here, CancellationToken.None, developmentId);
    }

    [RelayCommand]
    public void Open()
    {
        ErrorMessage = null;
        IsOpen = true;
        SelectedMachine ??= Machines.FirstOrDefault();
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    partial void OnSelectedMachineChanged(RemoteMachineOption? value)
    {
        Repositories.Clear();
        SelectedRepository = null;
        CopyProfiles.Clear();
        SelectedCopyProfile = null;
        AgentProfiles.Clear();
        SelectedAgentProfile = null;
        OnPropertyChanged(nameof(HasNoRepositories));
        OnPropertyChanged(nameof(HasCopyProfiles));
        OnPropertyChanged(nameof(HasAgentProfiles));

        if (value is not null && IsOpen)
        {
            _ = LoadRepositoriesAsync(value);
        }
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value && SelectedMachine is { } machine && Repositories.Count == 0)
        {
            _ = LoadRepositoriesAsync(machine);
        }
    }

    partial void OnBranchNameChanged(string value) => OnPropertyChanged(nameof(BranchError));

    /// <summary>O catálogo da outra máquina: só os diretórios que têm pasta e são repositório lá.</summary>
    private async Task LoadRepositoriesAsync(RemoteMachineOption machine)
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var view = await runner.RunAsync<GetRemoteControlHandler, RemoteControlView>(
                (handler, token) => handler.HandleAsync(new GetRemoteControl(machine.View.DeviceId), token),
                CancellationToken.None);

            if (SelectedMachine != machine)
            {
                return;
            }

            Repositories.Clear();

            foreach (var directory in view.Catalog.Directories
                .Where(directory => directory.MappedHere && directory.IsGitRepository)
                .OrderBy(directory => directory.Label, StringComparer.CurrentCultureIgnoreCase))
            {
                Repositories.Add(new RemoteRepositoryOption(directory));
            }

            SelectedRepository = Repositories.Count == 1 ? Repositories[0] : null;

            var databases = view.Catalog.Databases.ToDictionary(database => database.ConnectionId);
            CopyProfiles.Clear();

            foreach (var profile in view.Catalog.CopyProfiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                CopyProfiles.Add(new RemoteCopyProfileOption(profile, CopyProfileName(profile, databases)));
            }

            SelectedCopyProfile = CopyProfiles.Count == 1 ? CopyProfiles[0] : null;
            AgentProfiles.Clear();

            foreach (var profile in view.Catalog.AgentProfiles)
            {
                AgentProfiles.Add(profile);
            }

            SelectedAgentProfile = AgentProfiles.FirstOrDefault();
        }
        catch (SyncApiException exception) when (exception.Kind == SyncFailureKind.NotFound)
        {
            ErrorMessage = $"{machine.Name} ainda não publicou os repositórios dela.";
        }
        catch (Exception exception) when (exception is DomainException or SyncApiException)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "RemotePrepareCatalogFailed");
            ErrorMessage = "Não foi possível ler os repositórios da outra máquina.";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasNoRepositories));
            OnPropertyChanged(nameof(HasCopyProfiles));
            OnPropertyChanged(nameof(HasAgentProfiles));
        }
    }

    /// <summary>"Produção LGPD (ECO Produção → Local, produção: pede aprovação)": quem pede vê antes o que vai acontecer lá.</summary>
    private static string CopyProfileName(CatalogCopyProfile profile, Dictionary<Guid, CatalogDatabase> databases) =>
        RemoteOperationTexts.CopyProfile(profile, databases);

    /// <summary>O pedido do formulário; nulo, com o motivo em <see cref="ErrorMessage"/>, quando falta algo.</summary>
    internal EnvironmentProvisioningRequest? CreateRequest(RemoteRepositoryOption repository)
    {
        DatabaseCopyRequest? database = null;
        ClaudeCodeSessionRequest? agent = null;

        if (CopyDatabase)
        {
            if (SelectedCopyProfile?.Profile is not { } profile)
            {
                ErrorMessage = "Escolha o perfil de cópia do banco.";
                return null;
            }

            database = new DatabaseCopyRequest(profile.SourceConnectionId, null, profile.TargetConnectionId, null, null, profile.CopyProfileId);
        }

        if (StartAgent)
        {
            if (SelectedAgentProfile is not { } agentProfile)
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

        return new EnvironmentProvisioningRequest(
            _taskId,
            repository.Directory.TagDirectoryId,
            SourceBranch.Trim() is { Length: > 0 } source ? source : null,
            null,
            BranchName.Trim() is { Length: > 0 } branch ? branch : null,
            WorktreeConflictPolicy.Fail,
            RunCommands,
            database,
            agent);
    }

    /// <summary>
    /// Pede, e abre a tela "Operação" acompanhando. A tarefa sobe pelo sync
    /// agora: a outra máquina espera ela chegar antes de começar.
    /// </summary>
    [RelayCommand]
    public async Task PrepareAsync()
    {
        if (SelectedMachine is not { } machine)
        {
            return;
        }

        if (SelectedRepository is not { } repository)
        {
            ErrorMessage = "Escolha o repositório.";
            return;
        }

        if (BranchError is { } problem)
        {
            ErrorMessage = problem;
            return;
        }

        ErrorMessage = null;

        if (CreateRequest(repository) is not { } request)
        {
            return;
        }

        IsBusy = true;

        try
        {
            account.SyncNow();

            var operation = await runner.RunAsync<SendRemoteOperationHandler, RemoteOperationView>(
                (handler, token) => handler.HandleAsync(RemoteRequests.Provision(machine.View.DeviceId, request), token),
                CancellationToken.None);

            var screen = control();
            ControlOpened?.Invoke(this, screen);
            await screen.OpenAsync(machine.View, CancellationToken.None);
            var what = request.Database is null && request.StartAgent is null ? "o worktree" : "tudo";
            screen.Watch(operation, $"Preparar {what} em {machine.Name} · {repository.Name}");
            IsOpen = false;
        }
        catch (Exception exception) when (exception is DomainException or SyncApiException)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "RemotePrepareFailed {TaskId}", _taskId);
            ErrorMessage = "Não foi possível enviar o pedido.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
