using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mobile.Core.ViewModels;

/// <summary>Um worktree pronto na outra máquina, com o que dá para fazer nele.</summary>
public sealed record EnvironmentLine(RemoteEnvironmentView View)
{
    public string Title => View.TaskTitle ?? "Tarefa que ainda não chegou aqui";

    public string Detail => View.Branch is { } branch
        ? View.Commands.Count == 0 ? branch : $"{branch} · {View.Commands.Count} comando(s)"
        : $"{View.Commands.Count} comando(s)";

    public bool HasCommands => View.Commands.Count > 0;
}

/// <summary>
/// Uma máquina pareada, do celular (E7): os worktrees prontos lá, e em cada
/// um rodar um comando, abrir o Claude ou apagar. Tudo vai por id do catálogo
/// que ela publicou; o celular nunca manda texto de comando (ADR-088).
/// <para>
/// Com o terminal remoto aceito lá, também os terminais do Claude dela e, em
/// cada worktree, abrir o Claude no terminal do agente de lá (ADR-101).
/// </para>
/// </summary>
public sealed partial class RemoteMachineViewModel(
    IUseCaseRunner runner,
    RemoteSender sender,
    INavigator navigator,
    IDialogService dialogs,
    ILogger<RemoteMachineViewModel> logger) : MobileViewModel(dialogs, logger)
{
    private AgentCatalog? _catalog;

    /// <summary>A máquina aceita o terminal remoto: a lista dos terminais e "No terminal de lá" aparecem.</summary>
    [ObservableProperty]
    private bool _offersTerminal;

    [ObservableProperty]
    private string _title = "Máquina";

    [ObservableProperty]
    private string? _loadError;

    [ObservableProperty]
    private bool _isRefreshing;

    public Guid DeviceId { get; private set; }

    public ObservableCollection<EnvironmentLine> Environments { get; } = [];

    public bool HasEnvironments => Environments.Count > 0;

    /// <summary>O Claude no terminal do agente desta máquina, rodando agora.</summary>
    public ObservableCollection<TerminalLine> Terminals { get; } = [];

    public bool HasTerminals => Terminals.Count > 0;

    public bool CanStartAgent => _catalog is { AgentProfiles.Count: > 0 };

    /// <param name="nickname">Nulo: lido da lista de máquinas (a rota só leva o id).</param>
    public async Task LoadAsync(Guid deviceId, string? nickname = null)
    {
        DeviceId = deviceId;

        try
        {
            // A lista diz o nome e se a máquina aceita o terminal remoto.
            var machines = await runner.RunAsync<GetMachinesHandler, MachinesView>(
                (handler, token) => handler.HandleAsync(new GetMachines(), token));
            var machine = machines.Machines.FirstOrDefault(machine => machine.DeviceId == deviceId);
            nickname ??= machine?.Nickname;
            OffersTerminal = machine?.OffersTerminal == true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogInformation(exception, "MobileRemoteMachineNameUnavailable");
        }

        Title = nickname ?? Title;
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var view = await runner.RunAsync<GetRemoteControlHandler, RemoteControlView>(
                (handler, token) => handler.HandleAsync(new GetRemoteControl(DeviceId), token));
            _catalog = view.Catalog;
            LoadError = null;

            Environments.Clear();

            foreach (var environment in view.Environments)
            {
                Environments.Add(new EnvironmentLine(environment));
            }

            await LoadTerminalsAsync();
        }
        catch (SyncApiException exception)
        {
            Logger.LogInformation("MobileRemoteCatalogUnavailable {Kind}", exception.Kind);
            LoadError = exception.Kind == SyncFailureKind.NotFound
                ? "Esta máquina ainda não publicou o que pode ser pedido. Ligue o \"Aceitar controle\" nela."
                : "Não foi possível falar com o servidor agora.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogError(exception, "MobileRemoteCatalogFailed");
            LoadError = UnexpectedFailure;
        }
        finally
        {
            IsRefreshing = false;
            OnPropertyChanged(nameof(HasEnvironments));
            OnPropertyChanged(nameof(CanStartAgent));
            OnPropertyChanged(nameof(HasTerminals));
        }
    }

    [RelayCommand]
    public Task OpenTerminalAsync(TerminalLine? line) =>
        line is null ? Task.CompletedTask : navigator.OpenTerminalAsync(DeviceId, line.Item.Session.SessionId);

    /// <summary>
    /// Abre o Claude no terminal do agente de lá, no worktree. Os parâmetros, o
    /// modelo e o acompanhamento são os salvos lá; daqui vão só os ids (ADR-099).
    /// </summary>
    [RelayCommand]
    public async Task StartTerminalAsync(EnvironmentLine? line)
    {
        if (line is null || !OffersTerminal)
        {
            return;
        }

        if (await TryAsync(
                () => sender.SendAsync(
                    RemoteTerminalRequests.Start(DeviceId, line.View.TaskId, line.View.DevelopmentId),
                    $"Claude no terminal de {Title} · {line.Title}",
                    open: false),
                "MobileRemoteStartTerminalFailed"))
        {
            await Dialogs.ToastAsync("Pedido enviado. O Claude aparece em Terminais do Claude quando abrir lá.");
        }
    }

    private async Task LoadTerminalsAsync()
    {
        Terminals.Clear();

        if (!OffersTerminal)
        {
            return;
        }

        try
        {
            var list = await runner.RunAsync<ListRemoteTerminalsHandler, RemoteTerminalList>(
                (handler, token) => handler.HandleAsync(new ListRemoteTerminals(DeviceId), token));

            foreach (var item in list.Items.Where(item => item.IsRunning))
            {
                Terminals.Add(new TerminalLine(item, Title));
            }
        }
        catch (SyncApiException exception)
        {
            // A lista é um extra da tela: sem ela, os worktrees continuam.
            Logger.LogInformation("MobileRemoteMachineTerminalsUnavailable {Kind}", exception.Kind);
        }
    }

    /// <summary>Pede para a outra máquina olhar de novo: o Git, o Claude, o pg e os worktrees.</summary>
    [RelayCommand]
    public Task InspectAsync() =>
        TryAsync(() => sender.SendAsync(RemoteRequests.Inspect(DeviceId), $"Ver o ambiente de {Title}"), "MobileRemoteInspectFailed");

    [RelayCommand]
    public async Task RunCommandAsync(EnvironmentLine? line)
    {
        if (line is null || line.View.Commands.Count == 0)
        {
            return;
        }

        var commands = line.View.Commands;

        if (await Dialogs.ChooseAsync("Rodar qual comando?", [.. commands.Select(command => command.NeedsApproval ? $"{command.Name} (pede aprovação)" : command.Name)]) is not { } index)
        {
            return;
        }

        var command = commands[index];
        var values = new Dictionary<string, string>();

        foreach (var parameter in command.Parameters)
        {
            var value = await Dialogs.PromptAsync(
                command.Name,
                parameter.Required ? $"{parameter.Name} (obrigatório)" : $"{parameter.Name} (vazio: o padrão de lá)",
                parameter.DefaultValue ?? string.Empty,
                500);

            if (value is null)
            {
                return;
            }

            if (parameter.Required && string.IsNullOrWhiteSpace(value))
            {
                await Dialogs.AlertAsync(FailureTitle, $"Preencha {parameter.Name}.");
                return;
            }

            values[parameter.Name] = value;
        }

        await TryAsync(
            () => sender.SendAsync(RemoteRequests.RunCommand(DeviceId, line.View, command, values), $"{command.Name} · {line.Title}"),
            "MobileRemoteRunCommandFailed");
    }

    [RelayCommand]
    public async Task StartAgentAsync(EnvironmentLine? line)
    {
        if (line is null || _catalog is not { AgentProfiles.Count: > 0 } catalog)
        {
            return;
        }

        var profiles = catalog.AgentProfiles;
        var index = profiles.Count == 1 ? 0 : await Dialogs.ChooseAsync("Perfil do Claude", [.. profiles.Select(profile => profile.Name)]);

        if (index is null
            || await Dialogs.PromptAsync("Prompt para o Claude", line.Title, string.Empty, RemoteLimits.MaxPromptLength) is not { } prompt
            || string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        await TryAsync(
            () => sender.SendAsync(RemoteRequests.StartAgent(DeviceId, line.View, profiles[index.Value].Id, prompt), $"Claude · {line.Title}"),
            "MobileRemoteStartAgentFailed");
    }

    /// <summary>Lá, ainda pede aprovação; com alterações não commitadas, recusa.</summary>
    [RelayCommand]
    public async Task RemoveAsync(EnvironmentLine? line)
    {
        if (line is null
            || !await Dialogs.ConfirmAsync("Apagar o worktree?", $"{line.Title}: a outra máquina ainda pede aprovação antes de apagar.", "Pedir", "Cancelar"))
        {
            return;
        }

        await TryAsync(
            () => sender.SendAsync(RemoteRequests.RemoveWorktree(DeviceId, line.View), $"Apagar · {line.Title}"),
            "MobileRemoteRemoveFailed");
    }
}
