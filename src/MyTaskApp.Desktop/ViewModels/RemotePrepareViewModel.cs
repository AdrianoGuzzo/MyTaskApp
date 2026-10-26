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

/// <summary>
/// "Preparar em…" na aba Desenvolvimento (T-25): a mesma preparação do
/// "Iniciar implementação", só que em outra máquina, com o app dela fechado.
/// Escolhe-se a máquina e o repositório; a origem e a branch, se ficarem
/// vazias, são escolhidas lá como a aba escolheria. O andamento abre na tela
/// "Operação", que mostra a saída e as aprovações.
/// </summary>
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

    /// <summary>A tela "Operação" com o pedido em andamento: a janela abre.</summary>
    public event EventHandler<RemoteControlViewModel>? ControlOpened;

    /// <summary>Há para onde pedir: o botão aparece.</summary>
    [ObservableProperty]
    private bool _isAvailable;

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
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasNoRepositories => SelectedMachine is not null && Repositories.Count == 0 && !IsBusy;

    public string? BranchError => BranchName.Trim() is { Length: > 0 } branch ? GitBranchName.Validate(branch) : null;

    public void Load(Guid taskId, bool isReadOnly)
    {
        _taskId = taskId;
        _isReadOnly = isReadOnly;
        IsOpen = false;
        IsAvailable = false;
    }

    /// <summary>Quem está ligado agora. Sem o remoto pronto, ou sem ninguém, o botão some.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_isReadOnly || !account.IsAvailable)
        {
            IsAvailable = false;
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
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogInformation(exception, "RemotePrepareMachinesUnavailable");
            IsAvailable = false;
        }
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
        OnPropertyChanged(nameof(HasNoRepositories));

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
        }
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
        IsBusy = true;

        try
        {
            account.SyncNow();

            var request = new EnvironmentProvisioningRequest(
                _taskId,
                repository.Directory.TagDirectoryId,
                SourceBranch.Trim() is { Length: > 0 } source ? source : null,
                null,
                BranchName.Trim() is { Length: > 0 } branch ? branch : null,
                WorktreeConflictPolicy.Fail,
                RunCommands);

            var operation = await runner.RunAsync<SendRemoteOperationHandler, RemoteOperationView>(
                (handler, token) => handler.HandleAsync(RemoteRequests.Provision(machine.View.DeviceId, request), token),
                CancellationToken.None);

            var screen = control();
            ControlOpened?.Invoke(this, screen);
            await screen.OpenAsync(machine.View, CancellationToken.None);
            screen.Watch(operation, $"Preparar o worktree em {machine.Name} · {repository.Name}");
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
