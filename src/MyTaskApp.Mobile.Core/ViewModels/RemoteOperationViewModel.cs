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
/// Uma operação pedida de longe, ao vivo (E7): o estado, a saída e as
/// aprovações dela, relidos a cada <see cref="Interval"/> enquanto a tela está
/// aberta. Com uma conversa do Claude aberta, manda mais um prompt ou para.
/// </summary>
public sealed partial class RemoteOperationViewModel : MobileViewModel, IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>O fim da saída: uma conversa longa não pesa a tela.</summary>
    public const int MaxOutputLength = 64 * 1024;

    private readonly IUseCaseRunner _runner;

    private readonly RemoteSender _sender;

    private readonly ApprovalWatcher _watcher;

    private readonly ApprovalAnswers _answers;

    private readonly IUiDispatcher _dispatcher;

    private readonly TimeProvider _time;

    private readonly StringBuilder _lines = new();

    private readonly List<RemoteOperationEvent> _events = [];

    private CancellationTokenSource? _loop;

    private long _after;

    /// <summary>Terminou e a saída foi lida até o fim: o laço pode parar.</summary>
    private volatile bool _drained;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished), nameof(CanCancel))]
    private RemoteOperationView? _operation;

    [ObservableProperty]
    private string _status = "Carregando…";

    [ObservableProperty]
    private string? _summary;

    [ObservableProperty]
    private string _output = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConversation))]
    private Guid? _conversation;

    public RemoteOperationViewModel(
        IUseCaseRunner runner,
        RemoteSender sender,
        ApprovalWatcher watcher,
        ApprovalAnswers answers,
        IUiDispatcher dispatcher,
        IDialogService dialogs,
        TimeProvider time,
        ILogger<RemoteOperationViewModel> logger)
        : base(dialogs, logger)
    {
        _runner = runner;
        _sender = sender;
        _watcher = watcher;
        _answers = answers;
        _dispatcher = dispatcher;
        _time = time;
        _watcher.Changed += OnApprovalsChanged;
    }

    public Guid OperationId { get; private set; }

    public Guid DeviceId { get; private set; }

    public ObservableCollection<ApprovalLine> Approvals { get; } = [];

    public bool HasApprovals => Approvals.Count > 0;

    public bool IsFinished => Operation is { } operation && RemoteOperationLifecycle.IsFinal(operation.Status);

    public bool CanCancel => Operation is { CancelRequestedAt: null } && !IsFinished;

    /// <summary>A conversa ainda recebe prompts: a operação terminou e o Claude não parou.</summary>
    public bool HasConversation => Conversation is not null;

    /// <summary>Começa a acompanhar; trocar de operação recomeça a saída.</summary>
    public async Task OpenAsync(Guid operationId, Guid deviceId)
    {
        if (operationId != OperationId)
        {
            OperationId = operationId;
            DeviceId = deviceId;
            _after = 0;
            _drained = false;
            _events.Clear();
            _lines.Clear();
            Output = string.Empty;
            Summary = null;
            Conversation = null;
            Operation = null;
        }

        await PollAsync(CancellationToken.None);
        ShowApprovals();
        Start();
    }

    /// <summary>A tela saiu: para de ler.</summary>
    public void Close()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
    }

    [RelayCommand]
    public async Task CancelAsync()
    {
        if (await Dialogs.ConfirmAsync("Cancelar?", "A outra máquina para no próximo ponto seguro.", "Cancelar a operação", "Voltar")
            && await TryAsync(
                async () => Operation = await _runner.RunAsync<CancelRemoteOperationHandler, RemoteOperationView>(
                    (handler, token) => handler.HandleAsync(new CancelRemoteOperation(OperationId), token)),
                "MobileRemoteCancelFailed"))
        {
            Status = RemoteOperationTexts.Status(Operation!);
        }
    }

    /// <summary>Mais um prompt na mesma conversa; abre outra tela de operação.</summary>
    [RelayCommand]
    public async Task ContinueAsync()
    {
        if (Conversation is not { } conversation
            || await Dialogs.PromptAsync("Mais um prompt", "Na mesma conversa do Claude", string.Empty, RemoteLimits.MaxPromptLength) is not { } prompt
            || string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        await TryAsync(
            () => _sender.SendAsync(RemoteRequests.PromptAgent(DeviceId, Operation?.TaskId, conversation, prompt), "Claude · mais um prompt"),
            "MobileRemotePromptFailed");
    }

    [RelayCommand]
    public async Task StopAgentAsync()
    {
        if (Conversation is { } conversation
            && await Dialogs.ConfirmAsync("Parar o Claude?", "A conversa fecha; para continuar depois, abra outra.", "Parar", "Voltar"))
        {
            await TryAsync(
                () => _sender.SendAsync(RemoteRequests.StopAgent(DeviceId, Operation?.TaskId, conversation), "Parar o Claude"),
                "MobileRemoteStopFailed");
        }
    }

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
        Close();
    }

    private void Start()
    {
        if (_loop is not null || _drained)
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
            // Até drenar, não até o estado final: o "terminou" pode chegar com saída ainda por ler.
            while (!cancellationToken.IsCancellationRequested && !_drained)
            {
                await Task.Delay(Interval, _time, cancellationToken);
                await PollAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            var progress = await _runner.RunAsync<GetRemoteOperationProgressHandler, RemoteOperationProgress>(
                (handler, token) => handler.HandleAsync(new GetRemoteOperationProgress(OperationId, _after), token),
                cancellationToken);

            _dispatcher.Post(() => Show(progress));
        }
        catch (SyncApiException exception)
        {
            Logger.LogInformation("MobileRemoteProgressUnavailable {Kind}", exception.Kind);
            _dispatcher.Post(() => Status = "Sem notícias do servidor agora");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(exception, "MobileRemoteProgressFailed");
        }
    }

    private void Show(RemoteOperationProgress progress)
    {
        if (progress.Operation.OperationId != OperationId)
        {
            return;
        }

        _after = progress.After;
        _drained = progress.IsFinished;
        _events.AddRange(progress.Events);

        foreach (var item in progress.Events)
        {
            if (RemoteOperationTexts.Line(item) is { } line)
            {
                _lines.Append(line).Append('\n');
            }
        }

        if (_lines.Length > MaxOutputLength)
        {
            _lines.Remove(0, _lines.Length - MaxOutputLength);
        }

        if (progress.Events.Count > 0)
        {
            Output = _lines.ToString();
        }

        Operation = progress.Operation;
        Status = RemoteOperationTexts.Status(progress.Operation);
        Summary = progress.Operation.ResultSummary;
        Conversation = progress.IsFinished && RemoteOperationTexts.Conversation(_events) is { CanContinue: true } found
            ? found.AgentSessionId
            : null;
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

        foreach (var view in _watcher.Pending.Where(view => view.Request.OperationId == OperationId))
        {
            Approvals.Add(_answers.Line(view, "Esta operação"));
        }

        OnPropertyChanged(nameof(HasApprovals));
    }
}
