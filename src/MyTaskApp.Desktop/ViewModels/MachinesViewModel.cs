using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Domain;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma máquina da conta, como a lista mostra.</summary>
public sealed record MachineItem(MachineView View, string Presence, string Details, string Fingerprint, string Tools)
{
    public bool IsOnline => View.IsOnline;

    /// <summary>Outra máquina que aceita ser controlada: dá para pedir o pareamento, e depois controlar.</summary>
    public bool CanPair => View.AcceptsControl && !View.IsThis;
}

/// <summary>Um pedido para controlar esta máquina, com o código que a pessoa digita.</summary>
public sealed partial class OpenPairingItem(OpenPairingView view, string details) : ObservableObject
{
    public OpenPairingView View { get; } = view;

    public string Details { get; } = details;

    [ObservableProperty]
    private string _typedCode = string.Empty;
}

/// <summary>Um controlador em que esta máquina confia.</summary>
public sealed record TrustedControllerItem(TrustedControllerView View, string Details);

/// <summary>
/// A janela "Máquinas" (fase 2, T-06; ADR-087): as máquinas da conta, ligadas
/// ou não; o controle remoto e o "aceitar ser controlada", que só se ligam aqui;
/// o pareamento dos dois lados — o código que este computador mostra, e o
/// código que se digita aqui para confiar em outro.
/// </summary>
/// <remarks>
/// <b>Nada é aceito sem a pessoa.</b> Aceitar controle vem desligado e pede
/// confirmação; confiar num controlador exige digitar o código que ele mostra.
/// A janela nunca exibe o código de quem pede para controlar este computador.
/// </remarks>
public sealed partial class MachinesViewModel : ObservableObject
{
    private readonly IUseCaseRunner _runner;
    private readonly ISyncAccount _account;
    private readonly RemotePresenceScheduler _presence;
    private readonly IShellLauncher _shell;
    private readonly IConfirmationDialog _confirmation;
    private readonly TimeProvider _timeProvider;
    private readonly Func<RemoteControlViewModel> _control;
    private readonly Func<RemoteConversationsViewModel>? _conversations;
    private readonly ILogger<MachinesViewModel> _logger;

    private CancellationTokenSource? _authorization;

    private bool _loading;

    public MachinesViewModel(
        IUseCaseRunner runner,
        ISyncAccount account,
        RemotePresenceScheduler presence,
        IShellLauncher shell,
        IConfirmationDialog confirmation,
        TimeProvider timeProvider,
        Func<RemoteControlViewModel> control,
        ILogger<MachinesViewModel> logger,
        Func<RemoteConversationsViewModel>? conversations = null)
    {
        _runner = runner;
        _account = account;
        _presence = presence;
        _shell = shell;
        _confirmation = confirmation;
        _timeProvider = timeProvider;
        _control = control;
        _conversations = conversations;
        _logger = logger;
    }

    /// <summary>A janela "Conversas do Claude", de todas as máquinas, abre com este ViewModel.</summary>
    public event EventHandler<RemoteConversationsViewModel>? ConversationsOpened;

    /// <summary>A pessoa escolheu controlar uma máquina: a janela abre a tela "Operação" com este ViewModel.</summary>
    public event EventHandler<RemoteControlViewModel>? ControlOpened;

    /// <summary>A janela está à vista: só então o relógio dela recarrega a lista.</summary>
    public bool IsShown { get; set; }

    public bool HasServer => _account.IsAvailable;

    public bool HasNoServer => !HasServer;

    [ObservableProperty]
    private bool _remoteEnabled;

    [ObservableProperty]
    private bool _acceptControl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(NeedsRemoteLogin), nameof(StateText), nameof(HasProblem))]
    private RemoteAvailability _availability = RemoteAvailability.Off;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(HasProblem))]
    private string? _problem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivePairing), nameof(ActivePairingText))]
    private PairingCodeView? _activePairing;

    [ObservableProperty]
    private string? _activePairingStatus;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<MachineItem> Machines { get; } = [];

    public ObservableCollection<OpenPairingItem> OpenPairings { get; } = [];

    public ObservableCollection<TrustedControllerItem> TrustedControllers { get; } = [];

    public bool IsReady => Availability == RemoteAvailability.Ready;

    /// <summary>O login é de antes de ligar o remoto: conectar de novo traz o escopo.</summary>
    public bool NeedsRemoteLogin => Availability == RemoteAvailability.NeedsRemoteLogin;

    public bool HasProblem => Problem is not null && Availability != RemoteAvailability.Off;

    public bool HasMachines => Machines.Count > 0;

    public bool HasOpenPairings => OpenPairings.Count > 0;

    public bool HasTrustedControllers => TrustedControllers.Count > 0;

    public bool HasActivePairing => ActivePairing is not null;

    public string StateText => Availability switch
    {
        RemoteAvailability.Off => "Desligado. Este computador não aparece para os outros e não pode ser controlado.",
        RemoteAvailability.Ready => AcceptControl
            ? "Ligado. Este computador aparece para os outros e aceita controladores pareados."
            : "Ligado. Este computador aparece para os outros e pode controlar os que aceitam.",
        _ => Problem ?? "Sem resposta do servidor.",
    };

    public string? ActivePairingText => ActivePairing is { } pairing
        ? $"Digite este código em {MachineName(pairing.ExecutorDeviceId)} até as {pairing.ExpiresAt.ToLocalTime():HH:mm}:"
        : null;

    /// <summary>O código em dois grupos de três, como se lê em voz alta.</summary>
    public string? ActivePairingCode => ActivePairing is { Code: { Length: PairingCode.Digits } code } ? $"{code[..3]} {code[3..]}" : null;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var view = await _runner.RunAsync<GetMachinesHandler, MachinesView>(
                (handler, token) => handler.HandleAsync(new GetMachines(), token),
                cancellationToken);

            Apply(view);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "MachinesLoadFailed");
            ErrorMessage = "Não foi possível carregar as máquinas.";
        }
    }

    /// <summary>O relógio da janela: a lista (quem está ligado) e o pareamento que este computador pediu.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);

        if (ActivePairing is not { } pairing)
        {
            return;
        }

        try
        {
            var status = await _runner.RunAsync<GetPairingStatusHandler, PairingStatus>(
                (handler, token) => handler.HandleAsync(new GetPairingStatus(pairing.PairingId), token),
                cancellationToken);

            if (status == PairingStatus.Approved)
            {
                StatusMessage = $"{MachineName(pairing.ExecutorDeviceId)} confirmou o código e confia neste computador.";
                ActivePairing = null;
            }
            else if (status == PairingStatus.Expired)
            {
                ActivePairingStatus = "O código expirou sem ser digitado. Peça de novo.";
            }
        }
        catch (SyncApiException exception)
        {
            _logger.LogInformation("PairingStatusUnavailable {Kind}", exception.Kind);
        }
    }

    partial void OnRemoteEnabledChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        if (!value && AcceptControl)
        {
            _loading = true;
            AcceptControl = false;
            _loading = false;
        }

        _ = SaveSettingsAsync(value ? "Controle remoto ligado." : "Controle remoto desligado.");
    }

    partial void OnAcceptControlChanged(bool value)
    {
        if (!_loading)
        {
            _ = AcceptControlAsync(value);
        }
    }

    private async Task AcceptControlAsync(bool value)
    {
        if (value)
        {
            var confirmed = await _confirmation.AskAsync(new ConfirmationRequest(
                "Aceitar controle remoto neste computador?",
                "Só os controladores que você parear aqui, digitando o código que eles mostram, poderão pedir operações a este computador. "
                + "Você pode desligar ou revogar um controlador a qualquer momento.",
                "Aceitar"));

            if (!confirmed)
            {
                _loading = true;
                AcceptControl = false;
                _loading = false;
                return;
            }

            if (!RemoteEnabled)
            {
                _loading = true;
                RemoteEnabled = true;
                _loading = false;
            }
        }

        await SaveSettingsAsync(value ? "Este computador aceita controladores pareados." : "Este computador não aceita mais ser controlado.");
    }

    private Task SaveSettingsAsync(string success) =>
        RunAsync(
            async () =>
            {
                await _runner.RunAsync<UpdateRemoteSettingsHandler, RemoteSettings>(
                    (handler, token) => handler.HandleAsync(new UpdateRemoteSettings(RemoteEnabled, AcceptControl), token),
                    CancellationToken.None);

                // Os papéis mudaram: registra agora, sem esperar o minuto do sinal de vida.
                await _presence.TickAsync(CancellationToken.None);
            },
            success,
            "Não foi possível salvar.");

    /// <summary>Conecta de novo pela tela de sempre; com o remoto ligado, o login pede o escopo dele.</summary>
    [RelayCommand]
    public async Task ReconnectAsync()
    {
        _authorization?.Cancel();
        _authorization = new CancellationTokenSource();
        var cancellation = _authorization.Token;

        ErrorMessage = null;
        IsConnecting = true;

        try
        {
            var authorization = await _account.BeginConnectAsync(cancellation);
            StatusMessage = "Conclua o login no navegador. O MyTaskApp espera aqui.";

            if (!await _shell.OpenUriAsync(authorization.AuthorizeUrl))
            {
                StatusMessage = null;
                ErrorMessage = "Não foi possível abrir o navegador. Tente de novo.";
                await _authorization.CancelAsync();
                return;
            }

            await authorization.Completion.WaitAsync(cancellation);
            await _presence.TickAsync(CancellationToken.None);
            StatusMessage = "Conectado com o controle remoto.";
        }
        catch (Exception exception) when (exception is DomainException or SyncApiException)
        {
            StatusMessage = null;
            ErrorMessage = exception.Message;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = null;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "MachinesReconnectFailed");
            StatusMessage = null;
            ErrorMessage = "Não foi possível conectar. Tente de novo.";
        }
        finally
        {
            IsConnecting = false;
            await LoadAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    public void CancelReconnect() => _authorization?.Cancel();

    /// <summary>Este computador pede para controlar a máquina, e mostra o código para digitar lá.</summary>
    [RelayCommand]
    public Task PairAsync(MachineItem machine) =>
        RunAsync(
            async () =>
            {
                ActivePairing = await _runner.RunAsync<RequestPairingHandler, PairingCodeView>(
                    (handler, token) => handler.HandleAsync(new RequestPairing(machine.View.DeviceId), token),
                    CancellationToken.None);
                ActivePairingStatus = "Esperando o código ser digitado lá…";
                OnPropertyChanged(nameof(ActivePairingCode));
            },
            null,
            "Não foi possível pedir o pareamento.");

    /// <summary>A tela "Operação" para a máquina: o que dá para pedir a ela, e o que já foi pedido.</summary>
    [RelayCommand]
    public async Task ControlAsync(MachineItem machine)
    {
        var control = _control();
        ControlOpened?.Invoke(this, control);
        await control.OpenAsync(machine.View, CancellationToken.None);
    }

    /// <summary>As conversas do Claude de todas as máquinas da conta, sem escolher uma antes.</summary>
    [RelayCommand]
    public async Task OpenConversationsAsync()
    {
        if (_conversations is null)
        {
            return;
        }

        var conversations = _conversations();
        ConversationsOpened?.Invoke(this, conversations);
        await conversations.OpenAsync(CancellationToken.None);
    }

    [RelayCommand]
    public void ClosePairing()
    {
        ActivePairing = null;
        ActivePairingStatus = null;
        OnPropertyChanged(nameof(ActivePairingCode));
    }

    /// <summary>A pessoa digitou aqui o código que o controlador mostra.</summary>
    [RelayCommand]
    public Task ConfirmPairingAsync(OpenPairingItem pairing) =>
        RunAsync(
            () => _runner.RunAsync<ConfirmPairingHandler>(
                (handler, token) => handler.HandleAsync(new ConfirmPairing(pairing.View.PairingId, pairing.TypedCode), token),
                CancellationToken.None),
            $"Pronto: este computador confia em {pairing.View.ControllerNickname}.",
            "Não foi possível confirmar o pareamento.");

    [RelayCommand]
    public async Task RevokeTrustedAsync(TrustedControllerItem controller)
    {
        var confirmed = await _confirmation.AskAsync(new ConfirmationRequest(
            $"Deixar de confiar em {controller.View.Nickname}?",
            "Ele não poderá mais pedir nada a este computador. Para voltar, é preciso parear de novo, com um código novo.",
            "Deixar de confiar"));

        if (confirmed)
        {
            await RunAsync(
                () => _runner.RunAsync<RevokeTrustedControllerHandler>(
                    (handler, token) => handler.HandleAsync(new RevokeTrustedController(controller.View.Id), token),
                    CancellationToken.None),
                $"Este computador não confia mais em {controller.View.Nickname}.",
                "Não foi possível revogar.");
        }
    }

    private void Apply(MachinesView view)
    {
        _loading = true;

        try
        {
            RemoteEnabled = view.Settings.Enabled;
            AcceptControl = view.Settings.AcceptControl;
            Problem = view.Problem;
            Availability = view.Availability;
        }
        finally
        {
            _loading = false;
        }

        var now = _timeProvider.GetUtcNow();

        Replace(Machines, view.Machines.Select(machine => ToItem(machine, now)));

        // O código digitado pela metade sobrevive ao relógio: só sai o pedido que sumiu.
        var typed = OpenPairings.ToDictionary(item => item.View.PairingId, item => item.TypedCode);
        Replace(OpenPairings, view.OpenPairings.Select(pairing => new OpenPairingItem(
            pairing,
            $"{pairing.ControllerNickname} quer controlar este computador. O pedido vale até as {pairing.ExpiresAt.ToLocalTime():HH:mm}.")
        {
            TypedCode = typed.GetValueOrDefault(pairing.PairingId, string.Empty),
        }));

        Replace(TrustedControllers, view.TrustedControllers.Select(controller => new TrustedControllerItem(
            controller,
            $"desde {controller.ApprovedAt.ToLocalTime():dd/MM/yyyy} · chave {ShortFingerprint(controller.Fingerprint)}")));

        OnPropertyChanged(nameof(HasMachines));
        OnPropertyChanged(nameof(HasOpenPairings));
        OnPropertyChanged(nameof(HasTrustedControllers));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ActivePairingText));
    }

    private static MachineItem ToItem(MachineView machine, DateTimeOffset now) =>
        new(
            machine,
            machine.IsOnline ? "ligado"
                : machine.LastSeenAt is { } seen ? $"visto {Ago(now - seen)}"
                : "nunca ligou o controle remoto",
            string.Join(" · ", new[]
            {
                machine.Platform,
                machine.AppVersion.Length > 0 ? $"versão {machine.AppVersion}" : null,
                machine.AcceptsControl ? "aceita ser controlado" : null,
                machine.IsThis ? "este computador" : null,
            }.Where(part => !string.IsNullOrEmpty(part))),
            machine.KeyFingerprint is { } fingerprint ? $"chave {ShortFingerprint(fingerprint)}" : string.Empty,
            string.Join(", ", machine.Tools));

    /// <summary>Os 16 primeiros dígitos, em grupos de 4: o bastante para conferir de olho.</summary>
    internal static string ShortFingerprint(string fingerprint) =>
        string.Join(' ', Enumerable.Range(0, Math.Min(4, fingerprint.Length / 4)).Select(group => fingerprint.Substring(group * 4, 4)));

    private static string Ago(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => "agora há pouco",
        { TotalHours: < 1 } => $"há {(int)elapsed.TotalMinutes} min",
        { TotalDays: < 1 } => $"há {(int)elapsed.TotalHours} h",
        { TotalDays: < 2 } => "há 1 dia",
        _ => $"há {(int)elapsed.TotalDays} dias",
    };

    private string MachineName(Guid deviceId) =>
        Machines.FirstOrDefault(machine => machine.View.DeviceId == deviceId)?.View.Nickname ?? "a outra máquina";

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();

        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private async Task RunAsync(Func<Task> action, string? success, string failure)
    {
        ErrorMessage = null;
        StatusMessage = null;
        IsBusy = true;

        try
        {
            await action();
            StatusMessage = success;
        }
        catch (Exception exception) when (exception is DomainException or SyncApiException)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "MachinesActionFailed");
            ErrorMessage = failure;
        }
        finally
        {
            IsBusy = false;
            await LoadAsync(CancellationToken.None);
        }
    }
}
