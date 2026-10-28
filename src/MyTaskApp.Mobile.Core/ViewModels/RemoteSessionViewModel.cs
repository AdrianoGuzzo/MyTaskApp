using System.Collections.ObjectModel;
using System.Text;
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

/// <summary>
/// Uma conversa do Claude de uma máquina (ADR-091): a linha do tempo de todos
/// os prompts, de qualquer tela que os mandou, as aprovações dela, o próximo
/// prompt e o parar. Os mesmos handlers da janela Controlar do desktop.
/// </summary>
public sealed partial class RemoteSessionViewModel : MobileViewModel, IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.5);

    private readonly IUseCaseRunner _runner;

    private readonly RemoteSender _sender;

    private readonly ApprovalWatcher _watcher;

    private readonly ApprovalAnswers _answers;

    private readonly IUiDispatcher _dispatcher;

    private readonly RemotePace _pace;

    private readonly StringBuilder _lines = new();

    private readonly HashSet<Guid> _operations = [];

    /// <summary>As operações da conversa, para o aviso do hub, que chega de outra thread.</summary>
    private volatile IReadOnlySet<Guid> _known = new HashSet<Guid>();

    private RemoteSessionCursor _cursor = RemoteSessionCursor.Start;

    private CancellationTokenSource? _loop;

    /// <summary>Uma operação da conversa ainda pode trazer linhas, ou a conversa ainda roda.</summary>
    private volatile bool _live = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPrompt), nameof(CanStop))]
    private RemoteSessionItem? _session;

    [ObservableProperty]
    private string _title = "Conversa";

    [ObservableProperty]
    private string _status = "Carregando…";

    [ObservableProperty]
    private string _output = string.Empty;

    public RemoteSessionViewModel(
        IUseCaseRunner runner,
        RemoteSender sender,
        ApprovalWatcher watcher,
        ApprovalAnswers answers,
        IUiDispatcher dispatcher,
        IDialogService dialogs,
        TimeProvider time,
        ILogger<RemoteSessionViewModel> logger,
        IRemoteWatch? watch = null)
        : base(dialogs, logger)
    {
        _runner = runner;
        _sender = sender;
        _watcher = watcher;
        _answers = answers;
        _dispatcher = dispatcher;
        _pace = new RemotePace(watch ?? new NoRemoteWatch(), time, Concerns);
        _watcher.Changed += OnApprovalsChanged;
    }

    public Guid SessionId { get; private set; }

    public Guid DeviceId { get; private set; }

    public ObservableCollection<ApprovalLine> Approvals { get; } = [];

    public bool HasApprovals => Approvals.Count > 0;

    public bool CanPrompt => Session?.CanPrompt == true;

    public bool CanStop => Session?.CanStop == true;

    /// <summary>Começa a acompanhar; trocar de conversa recomeça a linha do tempo.</summary>
    public async Task OpenAsync(Guid sessionId, Guid deviceId)
    {
        if (sessionId != SessionId)
        {
            SessionId = sessionId;
            DeviceId = deviceId;
            _cursor = RemoteSessionCursor.Start;
            _operations.Clear();
            _known = new HashSet<Guid>();
            _lines.Clear();
            Output = string.Empty;
            Session = null;
            Title = "Conversa";
            Status = "Carregando…";
        }

        _live = true;
        await PollAsync(CancellationToken.None);
        ShowApprovals();
        Start();
    }

    /// <summary>A tela saiu: para de ler, e a conexão do hub cai se nenhuma outra tela a usa.</summary>
    public void Close()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
        _ = _pace.DisposeAsync().AsTask();
    }

    [RelayCommand]
    public async Task ContinueAsync()
    {
        if (Session is not { } session
            || await Dialogs.PromptAsync("Mais um prompt", "Na mesma conversa do Claude", string.Empty, RemoteLimits.MaxPromptLength) is not { } prompt
            || string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        if (await TryAsync(
                () => _sender.SendAsync(RemoteRequests.PromptAgent(DeviceId, session.Session.TaskId, SessionId, prompt), "Claude · mais um prompt", open: false),
                "MobileRemoteSessionPromptFailed"))
        {
            Resume();
        }
    }

    [RelayCommand]
    public async Task StopAsync()
    {
        if (Session is { } session
            && await Dialogs.ConfirmAsync("Parar o Claude?", "A conversa fecha; para continuar depois, abra outra.", "Parar", "Voltar")
            && await TryAsync(
                () => _sender.SendAsync(RemoteRequests.StopAgent(DeviceId, session.Session.TaskId, SessionId), "Parar o Claude", open: false),
                "MobileRemoteSessionStopFailed"))
        {
            Resume();
        }
    }

    [RelayCommand]
    public Task AllowAsync(ApprovalLine? line) => AnswerAsync(line, allow: true);

    [RelayCommand]
    public Task DenyAsync(ApprovalLine? line) => AnswerAsync(line, allow: false);

    [RelayCommand]
    public Task AllowForConversationAsync(ApprovalLine? line) => AnswerAsync(line, allow: true, remember: true);

    public void Dispose()
    {
        _watcher.Changed -= OnApprovalsChanged;
        Close();
    }

    /// <summary>Um pedido novo entra na conversa no servidor: a leitura volta.</summary>
    private void Resume()
    {
        _live = true;
        Start();
    }

    private void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = new CancellationTokenSource();
        _ = RunAsync(_loop.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _pace.StartAsync(cancellationToken);

            // Parada a conversa e lido tudo, a leitura descansa; um prompt novo, daqui ou de outra tela, a acorda.
            while (!cancellationToken.IsCancellationRequested)
            {
                await _pace.WaitAsync(Interval, cancellationToken);

                if (_live)
                {
                    await PollAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// O aviso é desta conversa: ela mudou, ou uma operação dela andou. A
    /// conversa que descansava volta a ser lida: o prompt pode ter vindo de
    /// outra tela (ADR-094).
    /// </summary>
    private bool Concerns(RemoteNotice notice)
    {
        var concerns = notice.Kind == RemoteNoticeKind.Session ? notice.Id == SessionId : _known.Contains(notice.Id);

        if (concerns)
        {
            _live = true;
        }

        return concerns;
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            var timeline = await _runner.RunAsync<GetRemoteSessionTimelineHandler, RemoteSessionTimeline>(
                (handler, token) => handler.HandleAsync(new GetRemoteSessionTimeline(SessionId, _cursor), token),
                cancellationToken);

            _dispatcher.Post(() => Show(timeline));
        }
        catch (SyncApiException exception)
        {
            Logger.LogInformation("MobileRemoteSessionUnavailable {Kind}", exception.Kind);
            _dispatcher.Post(() => Status = exception.Kind == SyncFailureKind.NotFound && exception.Code is null
                ? RemoteSessionTexts.ServerTooOld
                : "Sem notícias do servidor agora");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(exception, "MobileRemoteSessionFailed");
        }
    }

    private void Show(RemoteSessionTimeline timeline)
    {
        if (timeline.Session.Session.SessionId != SessionId)
        {
            return;
        }

        _cursor = timeline.Cursor;
        _live = timeline.IsLive || timeline.Session.Session.Status is RemoteAgentSessionStatus.Starting or RemoteAgentSessionStatus.Running;

        foreach (var line in timeline.Lines)
        {
            _lines.Append(line).Append('\n');
        }

        if (_lines.Length > RemoteOperationViewModel.MaxOutputLength)
        {
            _lines.Remove(0, _lines.Length - RemoteOperationViewModel.MaxOutputLength);
        }

        if (timeline.Lines.Count > 0)
        {
            Output = _lines.ToString();
        }

        _operations.UnionWith(timeline.Operations.Select(operation => operation.OperationId));
        _known = new HashSet<Guid>(_operations);
        Session = timeline.Session;
        Title = timeline.Session.Title;
        Status = timeline.Session.Branch is { } branch ? $"{branch} · {timeline.Session.State}" : timeline.Session.State;
        ShowApprovals();
    }

    private async Task AnswerAsync(ApprovalLine? line, bool allow, bool remember = false)
    {
        if (line is not null && await TryAsync(() => _answers.AnswerAsync(line.View, allow, remember), "MobileRemoteAnswerFailed"))
        {
            ShowApprovals();
        }
    }

    private void OnApprovalsChanged(object? sender, EventArgs e) => _dispatcher.Post(ShowApprovals);

    private void ShowApprovals()
    {
        Approvals.Clear();

        foreach (var view in _watcher.Pending.Where(view => _operations.Contains(view.Request.OperationId)))
        {
            Approvals.Add(_answers.Line(view, "Esta conversa"));
        }

        OnPropertyChanged(nameof(HasApprovals));
    }
}
