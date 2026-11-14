using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Terminals;

/// <summary>Quem está ligado a uma sessão de terminal (ADR-098).</summary>
public enum TerminalClientKind
{
    /// <summary>A janela de terminal nativa da máquina (<c>MyTaskApp.TerminalAttach</c>).</summary>
    NativeWindow,

    /// <summary>O terminal embutido no app desta máquina.</summary>
    Embedded,

    /// <summary>Um controlador de outra máquina, pelo servidor (ADR-099).</summary>
    Remote,
}

/// <summary>O processo, segundo o agente: é ele a autoridade, não a tela de quem olha.</summary>
public enum TerminalProcessState
{
    Running,

    /// <summary>Saiu com código 0.</summary>
    Exited,

    /// <summary>Saiu com outro código, ou o sistema não disse qual.</summary>
    Crashed,
}

/// <summary>Os diálogos do Claude que o app sabe reconhecer pelos hooks (ADR-099).</summary>
public enum TerminalDialogKind
{
    /// <summary>Pedido de permissão para usar uma ferramenta.</summary>
    Permission,

    /// <summary>Uma pergunta (AskUserQuestion), respondida no terminal.</summary>
    Question,

    /// <summary>O plano pronto (ExitPlanMode), aprovado no terminal.</summary>
    Plan,

    /// <summary>Outro pedido de atenção (elicitação, entrada).</summary>
    Input,
}

/// <summary>
/// Um diálogo aberto agora no terminal. O <see cref="Id"/> muda a cada
/// diálogo: responder um que já fechou é recusado, em vez de cair no próximo.
/// </summary>
/// <param name="Message">O contexto que os hooks deram: a ferramenta e o comando, a pergunta.</param>
public sealed record TerminalDialog(Guid Id, TerminalDialogKind Kind, string Message, DateTimeOffset OpenedAt)
{
    /// <summary>Só o pedido de permissão tem resposta pelo app; o resto se responde no terminal.</summary>
    public bool AnswerableFromTheApp => Kind == TerminalDialogKind.Permission;
}

/// <summary>Quem tem a vez de digitar.</summary>
public sealed record TerminalLease(Guid ClientId, TerminalClientKind Kind, string Label, DateTimeOffset Since);

/// <summary>Um cliente ligado, como os outros o veem.</summary>
public sealed record TerminalClientView(Guid Id, TerminalClientKind Kind, string Label, DateTimeOffset AttachedAt);

/// <summary>
/// O estado de uma sessão de terminal, como o agente o vê (ADR-098). O
/// <see cref="Process"/> vem do processo; a <see cref="Activity"/> e o
/// <see cref="Dialog"/>, dos hooks; nada sai do texto da tela.
/// </summary>
/// <param name="Monitored">Os hooks chegam. Sem eles, só o teclado vale: as ações estruturadas pedem o estado.</param>
/// <param name="PermissionsBypassed">
/// Aberto sem as aprovações do Claude (<c>--dangerously-skip-permissions</c>
/// ou o modo <c>bypassPermissions</c>). Quem controla de longe vê o aviso.
/// </param>
/// <param name="YourClientId">Quem é a vista que recebe, entre os <paramref name="Clients"/>: só a de longe sabe.</param>
public sealed record TerminalSessionStatus(
    Guid SessionId,
    Guid TaskId,
    Guid? DevelopmentId,
    string WorkingDirectory,
    int ProcessId,
    DateTimeOffset StartedAt,
    TerminalSize Size,
    TerminalProcessState Process,
    int? ExitCode,
    AgentActivity Activity,
    string? ActivityMessage,
    TerminalDialog? Dialog,
    TerminalLease? Lease,
    IReadOnlyList<TerminalClientView> Clients,
    bool Monitored,
    bool PermissionsBypassed,
    string? PermissionMode,
    DateTimeOffset? LastOutputAt,
    Guid? YourClientId = null,
    string ProviderId = "claude-code")
{
    public bool IsRunning => Process == TerminalProcessState.Running;

    public bool HasNativeWindow => Clients.Any(client => client.Kind == TerminalClientKind.NativeWindow);
}

/// <summary>O que chega a um cliente, em ordem.</summary>
public abstract record TerminalMessage;

/// <summary>
/// Bytes da tela. <see cref="Offset"/> é a posição do primeiro byte no fluxo
/// inteiro da sessão: quem vê um buraco pede o retrato de novo. Com
/// <see cref="Reset"/>, é o retrato: o cliente limpa o terminal e desenha a
/// partir dele, e a saída seguinte começa em <see cref="Offset"/>.
/// </summary>
public sealed record TerminalOutput(long Offset, byte[] Data, bool Reset) : TerminalMessage
{
    /// <summary>Onde a próxima saída começa.</summary>
    public long End => Reset ? Offset : Offset + Data.Length;
}

public sealed record TerminalStateMessage(TerminalSessionStatus Status) : TerminalMessage;

/// <summary>Um aviso para quem olha: "o notebook assumiu o controle".</summary>
public sealed record TerminalNotice(string Text) : TerminalMessage;

/// <summary>As ações que um cliente pede (ADR-099). Elas não se confundem.</summary>
public abstract record TerminalInput;

/// <summary>Teclas cruas, como o teclado mandaria. Pede a vez de digitar.</summary>
public sealed record TerminalKeys(byte[] Data) : TerminalInput;

/// <summary>
/// Um prompt para o Claude: só com ele parado esperando o próximo, e entra
/// colado (bracketed paste) seguido do Enter, como quem cola e confirma.
/// </summary>
public sealed record TerminalPrompt(string Text) : TerminalInput;

/// <summary>A resposta a um pedido de permissão aberto agora.</summary>
public sealed record TerminalChoice(Guid DialogId, bool Allow) : TerminalInput;

/// <summary>Interromper o que o Claude está fazendo (o Esc), sem encerrar nada.</summary>
public sealed record TerminalInterrupt : TerminalInput;

public sealed record TerminalResize(TerminalSize Size) : TerminalInput;

/// <summary>Pegar a vez de digitar, tirando de quem tinha.</summary>
public sealed record TerminalTakeControl : TerminalInput;

public sealed record TerminalReleaseControl : TerminalInput;

/// <summary>O cliente viu um buraco na saída e pede o retrato.</summary>
public sealed record TerminalResync : TerminalInput;
