namespace MyTaskApp.Remote.Contracts;

/// <summary>
/// <c>terminal.start</c> (ADR-099): abre o Claude no terminal do agente da
/// máquina executora. Só o ambiente: os parâmetros, o modelo e o
/// acompanhamento são os salvos lá, nunca texto vindo de longe.
/// </summary>
public sealed record RemoteTerminalStartRequest(Guid DevelopmentId, string? ProviderId = null);

/// <summary>
/// <c>terminal.attach</c> (ADR-099): seguir uma sessão do terminal do agente.
/// Termina na hora, e o id da operação vira o id da conexão. A entrada dessa
/// conexão vem assinada pela <paramref name="ViewerKey"/>, uma chave P-256
/// criada para ela e jogada fora quando ela fecha.
/// </summary>
/// <param name="Columns">O tamanho da vista de longe; o agente só aplica quando ela dita o tamanho.</param>
public sealed record RemoteTerminalAttachRequest(Guid SessionId, Guid DevelopmentId, DevicePublicKey ViewerKey, int Columns, int Rows);

public enum RemoteTerminalStopMode
{
    /// <summary>Como fechar a janela de um terminal: o Claude recebe o fim e salva a conversa.</summary>
    Graceful,

    /// <summary>Mata a árvore da sessão, e só ela.</summary>
    Force,
}

/// <summary><c>terminal.stop</c> (ADR-099): encerra uma sessão do terminal do agente.</summary>
public sealed record RemoteTerminalStopRequest(Guid SessionId, RemoteTerminalStopMode Mode);

/// <summary>
/// Um lote de entrada de quem controla de longe, assinado com a chave da
/// conexão (<see cref="RemoteJws.TerminalInputType"/>). O agente confere a
/// conexão, a máquina, a sessão, a sequência estritamente crescente e o horário.
/// </summary>
public sealed record RemoteTerminalInput(
    Guid AttachId,
    Guid TargetDeviceId,
    Guid SessionId,
    long Sequence,
    DateTimeOffset IssuedAt,
    RemoteTerminalAction Action);

/// <summary>Uma ação de <see cref="RemoteTerminalActions"/>, com o que ela precisa.</summary>
/// <param name="Keys">Os bytes de <see cref="RemoteTerminalActions.Keys"/>: o teclado é o do Claude, inteiro.</param>
public sealed record RemoteTerminalAction(
    string Kind,
    byte[]? Keys = null,
    string? Text = null,
    Guid? DialogId = null,
    bool Allow = false,
    int Columns = 0,
    int Rows = 0);

/// <summary>As ações do terminal (ADR-098), as mesmas da vista desta máquina.</summary>
public static class RemoteTerminalActions
{
    public const string Keys = "keys";
    public const string Prompt = "prompt";
    public const string Choice = "choice";
    public const string Interrupt = "interrupt";
    public const string Resize = "resize";
    public const string TakeControl = "take";
    public const string ReleaseControl = "release";
    public const string Resync = "resync";

    public static IReadOnlyCollection<string> All { get; } = [Keys, Prompt, Choice, Interrupt, Resize, TakeControl, ReleaseControl, Resync];
}

/// <summary>O que o agente manda para uma conexão, pelo relay do servidor, em ordem.</summary>
/// <param name="Offset">Na saída, onde os bytes começam no fluxo da sessão; com <paramref name="Reset"/>, a tela recomeça daqui.</param>
/// <param name="Text">O aviso ou a recusa, em palavras.</param>
/// <param name="More">Vêm mais pedaços desta mesma saída: quem recebe junta antes de desenhar (o retrato passa de um pedaço).</param>
public sealed record RemoteTerminalFrame(
    Guid AttachId,
    string Kind,
    long Offset = 0,
    byte[]? Data = null,
    bool Reset = false,
    RemoteTerminalState? State = null,
    string? Text = null,
    bool More = false);

public static class RemoteTerminalFrameKinds
{
    public const string Output = "output";
    public const string State = "state";
    public const string Notice = "notice";
    public const string Refusal = "refusal";
}

public enum RemoteTerminalProcess
{
    Running,
    Exited,
    Crashed,
}

/// <param name="Kind">permission, question, plan ou input: só a permissão tem resposta por botão.</param>
public sealed record RemoteTerminalDialog(Guid Id, string Kind, string Message, DateTimeOffset OpenedAt);

/// <param name="Kind">window, app ou remote.</param>
public sealed record RemoteTerminalViewer(Guid Id, string Kind, string Label, DateTimeOffset AttachedAt);

/// <summary>O estado de uma sessão do terminal do agente, como uma conexão de longe vê.</summary>
/// <param name="YourViewerId">Quem é esta conexão entre os <paramref name="Viewers"/>: é como ela sabe se tem a vez.</param>
/// <param name="ControlledBy">Quem tem a vez de digitar, entre os <paramref name="Viewers"/>.</param>
public sealed record RemoteTerminalState(
    Guid SessionId,
    Guid TaskId,
    Guid? DevelopmentId,
    int ProcessId,
    DateTimeOffset StartedAt,
    int Columns,
    int Rows,
    RemoteTerminalProcess Process,
    int? ExitCode,
    RemoteAgentActivity Activity,
    string? ActivityMessage,
    RemoteTerminalDialog? Dialog,
    Guid? ControlledBy,
    IReadOnlyList<RemoteTerminalViewer> Viewers,
    bool Monitored,
    bool PermissionsBypassed,
    string? PermissionMode,
    Guid? YourViewerId,
    string ProviderId = "claude-code");

/// <summary>
/// Uma sessão do terminal do agente, relatada pela máquina executora
/// (<c>PUT /api/v1/remote/agent/terminals</c>). Só ids e estado, como as
/// conversas sem terminal: o servidor guarda em memória, nada no banco.
/// </summary>
public sealed record RemoteTerminalSessionReport(
    Guid SessionId,
    Guid TaskId,
    Guid? DevelopmentId,
    RemoteTerminalProcess Process,
    int? ExitCode,
    RemoteAgentActivity Activity,
    string? ActivityMessage,
    bool HasDialog,
    bool Monitored,
    bool PermissionsBypassed,
    bool RemotelyControlled,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string ProviderId = "claude-code");

/// <summary>Todas as sessões do terminal desta máquina de uma vez: a que sumiu do lote acabou.</summary>
public sealed record RemoteTerminalSessionBatch(IReadOnlyList<RemoteTerminalSessionReport> Sessions);

/// <summary>O servidor guardou o relato: quantas sessões ficaram.</summary>
public sealed record RemoteTerminalSessionAck(int Sessions);

/// <summary>Uma sessão do terminal como a conta vê: o último relato, de qual máquina e quando chegou.</summary>
public sealed record RemoteTerminalSessionView(Guid DeviceId, RemoteTerminalSessionReport Session, DateTimeOffset ReportedAt);

/// <summary>As sessões do terminal da conta (<c>GET /api/v1/remote/terminals</c>).</summary>
public sealed record RemoteTerminalSessionList(IReadOnlyList<RemoteTerminalSessionView> Sessions);
