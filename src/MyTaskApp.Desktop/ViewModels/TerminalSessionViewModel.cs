using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Terminals;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Uma vista de uma sessão do terminal do agente (ADR-098, ADR-099): a tela que
/// chega pelo canal, o estado que o agente manda, e o que a pessoa digita ou
/// pede. O mesmo para a vista dentro do app e para quem controla de longe; só
/// muda o canal.
/// </summary>
/// <remarks>
/// O miolo é o <see cref="TerminalSessionClient"/>, o mesmo do celular
/// (ADR-101). Aqui ficam os comandos da janela e a pergunta antes de assumir
/// sem as aprovações ou de forçar o fim.
/// </remarks>
public sealed partial class TerminalSessionViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>O que muda quando o estado muda: tudo, menos a tela.</summary>
    private static readonly string[] StateProperties =
    [
        nameof(Connection), nameof(IsConnected), nameof(IsDisconnected), nameof(IsRunning), nameof(IsReadOnly), nameof(StatusText),
        nameof(Status), nameof(Message), nameof(DialogText), nameof(CanAnswerDialog), nameof(BypassWarning), nameof(MonitoringWarning),
        nameof(LeaseText), nameof(OthersText), nameof(ProcessText), nameof(HasControl), nameof(CanTakeControlNow), nameof(CanReleaseControlNow),
    ];

    private readonly TerminalSessionClient _client;

    private readonly IConfirmationDialog? _confirmations;

    /// <param name="kind">Que vista é esta: a lista "também aberto em" não a repete.</param>
    /// <param name="connect">Liga à sessão pedindo o tamanho que cabe; o canal local ou o relay remoto.</param>
    /// <param name="post">Leva uma ação para a thread da tela.</param>
    /// <param name="confirmations">Pergunta antes de forçar o fim; sem ele, "Forçar" não aparece.</param>
    public TerminalSessionViewModel(
        Guid sessionId,
        string title,
        TerminalClientKind kind,
        Func<TerminalSize, CancellationToken, Task<ITerminalChannel>> connect,
        Action<Action> post,
        IConfirmationDialog? confirmations,
        ILogger<TerminalSessionViewModel> logger)
    {
        Title = title;
        _confirmations = confirmations;
        _client = new TerminalSessionClient(sessionId, kind, connect, post, logger);
        _client.ScreenChanged += () =>
        {
            OnPropertyChanged(nameof(Screen));
            OnPropertyChanged(nameof(Version));
        };
        _client.StateChanged += OnStateChanged;
    }

    /// <summary>A vista já aberta foi pedida de novo: a janela vem para a frente.</summary>
    public event EventHandler? ActivationRequested;

    public Guid SessionId => _client.SessionId;

    public string Title { get; }

    public TerminalConnection Connection => _client.Connection;

    public VtScreen? Screen => _client.Screen;

    /// <summary>Sobe a cada alimentação da tela: a vista redesenha.</summary>
    public long Version => _client.Version;

    public TerminalSessionStatus? Status => _client.Status;

    /// <summary>Um recado curto do agente ou desta vista: uma recusa, o controle que voltou.</summary>
    public string? Message => _client.Message;

    public bool IsConnected => _client.IsConnected;

    public bool IsDisconnected => _client.IsDisconnected;

    public bool IsRunning => _client.IsRunning;

    /// <inheritdoc cref="TerminalSessionClient.IsReadOnly"/>
    public bool IsReadOnly => _client.IsReadOnly;

    /// <summary>A vista de quem controla de outra máquina: assume e solta a vez pelos botões.</summary>
    public bool IsRemote => _client.IsRemote;

    /// <summary>Esta vista tem a vez de digitar.</summary>
    public bool HasControl => _client.HasControl;

    public bool CanTakeControlNow => _client.CanTakeControlNow;

    public bool CanReleaseControlNow => _client.CanReleaseControlNow;

    public bool CanForceStop => _confirmations is not null;

    public string StatusText => _client.StatusText;

    public string? DialogText => _client.DialogText;

    /// <summary>Permitir ou negar por aqui: só a permissão, que tem só essas duas saídas.</summary>
    public bool CanAnswerDialog => _client.CanAnswerDialog;

    public string? BypassWarning => _client.BypassWarning;

    public string? MonitoringWarning => _client.MonitoringWarning;

    public string? LeaseText => _client.LeaseText;

    public string? OthersText => _client.OthersText;

    /// <summary>De longe, a pasta não vem: o caminho é da outra máquina.</summary>
    public string? ProcessText => _client.ProcessText;

    /// <summary>Liga (ou religa) à sessão. Falhar não lança: a vista mostra o motivo e "Reconectar".</summary>
    public Task ConnectAsync() => _client.ConnectAsync();

    public void RequestActivation() => ActivationRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>O que a pessoa digitou na vista.</summary>
    public Task TypeAsync(byte[] keys) => _client.TypeAsync(keys);

    /// <summary>O tamanho que cabe na vista; o agente só aplica se ela dita o tamanho.</summary>
    public Task ResizeAsync(TerminalSize size) => _client.ResizeAsync(size);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    [RelayCommand(CanExecute = nameof(IsDisconnected))]
    private Task ReconnectAsync() => ConnectAsync();

    /// <summary>
    /// Pede a vez de digitar. Numa sessão sem as aprovações do Claude Code, pergunta
    /// antes: o que for digitado roda lá sem nenhuma confirmação.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTakeControlNow))]
    private async Task TakeControlAsync()
    {
        if (Status?.PermissionsBypassed == true && _confirmations is not null)
        {
            var confirmed = await _confirmations.AskAsync(new ConfirmationRequest(
                "Assumir uma sessão sem as aprovações do Claude Code?",
                "Esta sessão foi aberta sem as aprovações: o que você pedir daqui roda na outra máquina sem perguntar antes. O MyTaskApp não muda isso.",
                "Assumir o controle"));

            if (!confirmed)
            {
                return;
            }
        }

        await _client.TakeControlAsync();
    }

    [RelayCommand(CanExecute = nameof(CanReleaseControlNow))]
    private Task ReleaseControlAsync() => _client.ReleaseControlAsync();

    private bool CanInterrupt => _client.CanInterrupt;

    /// <summary>O Esc do Claude: para o que ele está fazendo, a conversa fica.</summary>
    [RelayCommand(CanExecute = nameof(CanInterrupt))]
    private Task InterruptAsync() => _client.InterruptAsync();

    private bool CanAnswer => _client.CanAnswer;

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task AllowAsync() => _client.AnswerAsync(allow: true);

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task DenyAsync() => _client.AnswerAsync(allow: false);

    private bool CanStop => _client.CanStop;

    /// <summary>Como fechar a janela de um terminal: o Claude recebe o fim e salva a conversa.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => _client.StopAsync(force: false);

    private bool CanForce => CanStop && CanForceStop;

    /// <summary>Mata a árvore da sessão, e só ela: o que o Claude abriu dentro dela vai junto.</summary>
    [RelayCommand(CanExecute = nameof(CanForce))]
    private async Task ForceStopAsync()
    {
        var confirmed = await _confirmations!.AskAsync(new ConfirmationRequest(
            "Forçar o fim desta sessão?",
            "O Claude e tudo o que ele abriu nesta sessão são encerrados na hora, sem salvar. Os outros programas da máquina não são tocados.",
            "Forçar o fim",
            IsIrreversible: true));

        if (confirmed)
        {
            await _client.StopAsync(force: true);
        }
    }

    private void OnStateChanged()
    {
        foreach (var property in StateProperties)
        {
            OnPropertyChanged(property);
        }

        ReconnectCommand.NotifyCanExecuteChanged();
        TakeControlCommand.NotifyCanExecuteChanged();
        ReleaseControlCommand.NotifyCanExecuteChanged();
        InterruptCommand.NotifyCanExecuteChanged();
        AllowCommand.NotifyCanExecuteChanged();
        DenyCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ForceStopCommand.NotifyCanExecuteChanged();
    }
}
