using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Presentation;
using MyTaskApp.Mobile.Core.Remote;
using MyTaskApp.Remote.Contracts;
using PairingState = MyTaskApp.Remote.Contracts.PairingStatus;

namespace MyTaskApp.Mobile.Core.ViewModels;

/// <summary>Uma máquina da conta que aceita controle.</summary>
public sealed record MachineLine(MachineView View, string Detail)
{
    public string Name => View.Nickname;

    public bool IsOnline => View.IsOnline;
}

/// <summary>Um pedido feito daqui, com o estado de agora.</summary>
public sealed record SentLine(SentOperation Operation, string Status);

/// <summary>Uma conversa do Claude numa máquina da conta, aberta daqui ou de outra tela (ADR-091).</summary>
public sealed record SessionLine(RemoteSessionItem Item, string Machine)
{
    public string Title => Item.Title;

    public string Detail => Item.Branch is { } branch ? $"{Machine} · {branch} · {Item.State}" : $"{Machine} · {Item.State}";
}

/// <summary>
/// A aba Remoto (E7): as aprovações esperando, as máquinas da conta, as
/// conversas do Claude delas e os pedidos feitos deste celular. Ligar o remoto aqui faz do celular só um
/// controlador: ele pede e aprova, e não executa nada.
/// </summary>
/// <remarks>
/// O celular se registra (a chave e o papel) a cada abertura da aba: é o que
/// o servidor confere no pareamento e nos pedidos assinados.
/// </remarks>
public sealed partial class RemoteViewModel : MobileViewModel, IDisposable
{
    /// <summary>De quanto em quanto tempo a tela pergunta se o código já foi digitado lá.</summary>
    public static readonly TimeSpan PairingPoll = TimeSpan.FromSeconds(2);

    /// <summary>As conversas na aba: as mais recentes; a máquina tem as outras.</summary>
    public const int MaxSessions = 10;

    private readonly IUseCaseRunner _runner;

    private readonly RemoteBackgroundCheck _background;

    private readonly ISyncAccount _account;

    private readonly ApprovalWatcher _watcher;

    private readonly ApprovalAnswers _answers;

    private readonly SentOperations _sent;

    private readonly INavigator _navigator;

    private readonly IUiDispatcher _dispatcher;

    private readonly TimeProvider _time;

    private readonly Dictionary<Guid, string> _names = [];

    /// <summary>As que já apareceram: só uma nova avisa.</summary>
    private readonly HashSet<Guid> _seen = [];

    private CancellationTokenSource? _pairing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOff), nameof(IsReady), nameof(NeedsLogin), nameof(HasNoMachines))]
    private RemoteAvailability _availability = RemoteAvailability.Off;

    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPairing))]
    private string? _pairingCode;

    [ObservableProperty]
    private string? _pairingStatus;

    /// <summary>O servidor não guarda conversas (é anterior a isso): o aviso no lugar da lista.</summary>
    [ObservableProperty]
    private string? _sessionsProblem;

    public RemoteViewModel(
        IUseCaseRunner runner,
        ISyncAccount account,
        ApprovalWatcher watcher,
        ApprovalAnswers answers,
        SentOperations sent,
        INavigator navigator,
        IUiDispatcher dispatcher,
        IDialogService dialogs,
        TimeProvider time,
        ILogger<RemoteViewModel> logger,
        RemoteBackgroundCheck background)
        : base(dialogs, logger)
    {
        _background = background;
        _runner = runner;
        _account = account;
        _watcher = watcher;
        _answers = answers;
        _sent = sent;
        _navigator = navigator;
        _dispatcher = dispatcher;
        _time = time;
        _watcher.Changed += OnApprovalsChanged;
    }

    public ObservableCollection<ApprovalLine> Approvals { get; } = [];

    public ObservableCollection<MachineLine> Machines { get; } = [];

    public ObservableCollection<SentLine> Recent { get; } = [];

    public ObservableCollection<SessionLine> Sessions { get; } = [];

    public bool HasSessions => Sessions.Count > 0;

    public bool IsAvailable => _account.IsAvailable;

    public bool IsOff => Availability == RemoteAvailability.Off;

    public bool IsReady => Availability == RemoteAvailability.Ready;

    /// <summary>O login é de antes de ligar o remoto, ou venceu: entrar de novo inclui o escopo.</summary>
    public bool NeedsLogin => Availability is RemoteAvailability.NotConnected or RemoteAvailability.NeedsRemoteLogin;

    public bool HasApprovals => Approvals.Count > 0;

    public bool HasMachines => Machines.Count > 0;

    /// <summary>Ligado, e nenhum computador aceita controle: a tela diz onde ligar.</summary>
    public bool HasNoMachines => IsReady && !HasMachines;

    public bool HasRecent => Recent.Count > 0;

    public bool IsPairing => PairingCode is not null;

    /// <summary>"Remoto (2)": quantas esperam resposta.</summary>
    public string TabTitle => Approvals.Count == 0 ? "Remoto" : $"Remoto ({Approvals.Count})";

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            if (!_account.IsAvailable)
            {
                Availability = RemoteAvailability.Off;
                return;
            }

            // Registrar primeiro: o pareamento e os pedidos assinados conferem a chave registrada.
            Availability = await _runner.RunAsync<RegisterThisControllerHandler, RemoteAvailability>(
                (handler, token) => handler.HandleAsync(new RegisterThisController(), token));

            var view = await _runner.RunAsync<GetMachinesHandler, MachinesView>(
                (handler, token) => handler.HandleAsync(new GetMachines(), token));
            Availability = view.Availability;
            Problem = view.Problem;

            Machines.Clear();
            _names.Clear();

            foreach (var machine in view.Machines.Where(machine => !machine.IsThis))
            {
                _names[machine.DeviceId] = machine.Nickname;
            }

            var sessions = IsReady ? await LoadSessionsAsync() : null;

            foreach (var machine in view.Machines.Where(machine => !machine.IsThis && machine.AcceptsControl))
            {
                Machines.Add(new MachineLine(machine, Detail(machine, sessions?.AgentOf(machine.DeviceId))));
            }

            OnPropertyChanged(nameof(HasMachines));
            OnPropertyChanged(nameof(HasNoMachines));

            if (IsReady)
            {
                // O aviso com o app fechado (ADR-096): a permissão é pedida aqui, com a tela.
                await _background.RequestPermissionAsync();
            }

            await _watcher.RefreshAsync();
            ShowApprovals();
            await LoadRecentAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogError(exception, "MobileRemoteLoadFailed");
            Problem = UnexpectedFailure;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>Liga o remoto só como controlador; o login seguinte pede o escopo do controle remoto.</summary>
    [RelayCommand]
    public async Task EnableAsync()
    {
        if (await TryAsync(
                () => _runner.RunAsync<UpdateRemoteSettingsHandler, RemoteSettings>(
                    (handler, token) => handler.HandleAsync(new UpdateRemoteSettings(Enabled: true, AcceptControl: false), token)),
                "MobileRemoteEnableFailed"))
        {
            await TryAsync(() => _background.ApplyAsync(), "MobileRemoteBackgroundApplyFailed");
            await LoadAsync();
        }
    }

    [RelayCommand]
    public Task ConnectAsync() => _navigator.OpenConnectAsync();

    /// <summary>Pede para controlar a máquina e mostra o código, para digitar lá, na janela Máquinas.</summary>
    [RelayCommand]
    public async Task PairAsync(MachineLine? machine)
    {
        if (machine is null)
        {
            return;
        }

        PairingView? code = null;

        if (!await TryAsync(
                async () => code = new PairingView(await _runner.RunAsync<RequestPairingHandler, PairingCodeView>(
                    (handler, token) => handler.HandleAsync(new RequestPairing(machine.View.DeviceId), token)), machine.Name),
                "MobileRemotePairFailed"))
        {
            return;
        }

        _pairing?.Cancel();
        _pairing = new CancellationTokenSource();
        PairingCode = code!.Code.Code;
        PairingStatus = $"Em {machine.Name}, abra Máquinas e digite este código.";
        _ = WaitForPairingAsync(code, _pairing.Token);
    }

    [RelayCommand]
    public void ClosePairing()
    {
        _pairing?.Cancel();
        PairingCode = null;
        PairingStatus = null;
    }

    [RelayCommand]
    public Task OpenMachineAsync(MachineLine? machine) =>
        machine is null ? Task.CompletedTask : _navigator.OpenMachineAsync(machine.View.DeviceId);

    [RelayCommand]
    public Task OpenSessionAsync(SessionLine? line) =>
        line is null ? Task.CompletedTask : _navigator.OpenSessionAsync(line.Item.Session.SessionId, line.Item.Session.DeviceId);

    [RelayCommand]
    public Task OpenOperationAsync(SentLine? line) =>
        line is null ? Task.CompletedTask : _navigator.OpenOperationAsync(line.Operation.OperationId, line.Operation.DeviceId);

    [RelayCommand]
    public Task AllowAsync(ApprovalLine? line) => AnswerAsync(line, allow: true);

    [RelayCommand]
    public Task DenyAsync(ApprovalLine? line) => AnswerAsync(line, allow: false);

    /// <summary>Permite e deixa o agente lembrar o mesmo pedido até o fim da conversa.</summary>
    [RelayCommand]
    public Task AllowForConversationAsync(ApprovalLine? line) => AnswerAsync(line, allow: true, remember: true);

    public void Dispose()
    {
        _watcher.Changed -= OnApprovalsChanged;
        _pairing?.Cancel();
        _pairing?.Dispose();
    }

    private async Task AnswerAsync(ApprovalLine? line, bool allow, bool remember = false)
    {
        if (line is not null && await TryAsync(() => _answers.AnswerAsync(line.View, allow, remember), "MobileRemoteAnswerFailed"))
        {
            ShowApprovals();
        }
    }

    private async Task WaitForPairingAsync(PairingView pairing, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PairingPoll, _time, cancellationToken);
                var status = await _runner.RunAsync<GetPairingStatusHandler, PairingState>(
                    (handler, token) => handler.HandleAsync(new GetPairingStatus(pairing.Code.PairingId), token),
                    cancellationToken);

                if (status == PairingState.Pending && _time.GetUtcNow() < pairing.Code.ExpiresAt)
                {
                    continue;
                }

                _dispatcher.Post(() =>
                {
                    PairingCode = null;
                    PairingStatus = status == PairingState.Approved
                        ? $"Pronto: {pairing.Machine} confia neste celular."
                        : "O código venceu sem ser digitado. Peça outro.";
                });
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "MobileRemotePairingStatusFailed");
            _dispatcher.Post(() => PairingStatus = "Não foi possível saber se o código foi digitado. Feche e tente de novo.");
        }
    }

    /// <summary>As conversas mais recentes das máquinas da conta; o estado de cada agente vem junto.</summary>
    private async Task<RemoteSessionList?> LoadSessionsAsync()
    {
        RemoteSessionList? list = null;

        try
        {
            list = await _runner.RunAsync<ListRemoteSessionsHandler, RemoteSessionList>(
                (handler, token) => handler.HandleAsync(new ListRemoteSessions(), token));
            SessionsProblem = list.Unavailable;
        }
        catch (SyncApiException exception)
        {
            Logger.LogInformation("MobileRemoteSessionsUnavailable {Kind}", exception.Kind);
            SessionsProblem = "Sem notícias do servidor agora";
        }

        Sessions.Clear();

        foreach (var item in list?.Items.Take(MaxSessions) ?? [])
        {
            Sessions.Add(new SessionLine(item, _names.GetValueOrDefault(item.Session.DeviceId, "Outra máquina")));
        }

        OnPropertyChanged(nameof(HasSessions));
        return list;
    }

    private async Task LoadRecentAsync()
    {
        var lines = new List<SentLine>();

        foreach (var sent in _sent.List().Take(10))
        {
            try
            {
                var progress = await _runner.RunAsync<GetRemoteOperationProgressHandler, RemoteOperationProgress>(
                    (handler, token) => handler.HandleAsync(new GetRemoteOperationProgress(sent.OperationId, long.MaxValue), token));
                lines.Add(new SentLine(sent, RemoteOperationTexts.Status(progress.Operation)));
            }
            catch (SyncApiException)
            {
                // Apagada pela retenção ou sem servidor agora: a linha fica, sem estado.
                lines.Add(new SentLine(sent, "Sem notícias do servidor"));
            }
        }

        Recent.Clear();

        foreach (var line in lines)
        {
            Recent.Add(line);
        }

        OnPropertyChanged(nameof(HasRecent));
    }

    private void OnApprovalsChanged(object? sender, EventArgs e) => _dispatcher.Post(ShowApprovals);

    /// <summary>
    /// A lista da aba, e um aviso curto quando chega uma nova: a aprovação
    /// aparece em qualquer tela do app aberto, não só nesta aba.
    /// </summary>
    private void ShowApprovals()
    {
        Approvals.Clear();
        ApprovalLine? arrived = null;

        foreach (var view in _watcher.Pending)
        {
            var line = _answers.Line(view, _names.GetValueOrDefault(view.TargetDeviceId, "Outra máquina"));
            Approvals.Add(line);

            if (_seen.Add(view.Request.ApprovalId))
            {
                arrived ??= line;
            }
        }

        OnPropertyChanged(nameof(HasApprovals));
        OnPropertyChanged(nameof(TabTitle));

        if (arrived is not null)
        {
            _ = Dialogs.ToastAsync($"Aprovação pedida: {arrived.Title}. Veja na aba Remoto.");
        }
    }

    /// <summary>
    /// "ligada" é o desktop de lá; o agente, que é quem roda os pedidos, vem à
    /// parte quando relata (<paramref name="agent"/>): ligada com o agente parado
    /// não é ligada para os pedidos.
    /// </summary>
    private string Detail(MachineView machine, string? agent)
    {
        var seen = machine.IsOnline
            ? "ligada"
            : machine.LastSeenAt is { } at ? "vista " + Labels.Ago(at, _time) : "nunca ligada";
        seen = agent is null ? seen : $"{seen}, {agent}";

        return machine.Tools.Count == 0 ? seen : $"{seen} · {string.Join(", ", machine.Tools)}";
    }

    private sealed record PairingView(PairingCodeView Code, string Machine);
}
