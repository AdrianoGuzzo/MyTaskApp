namespace MyTaskApp.Remote.Contracts;

/// <summary>
/// Os tetos do protocolo, os mesmos no servidor e no agente
/// (docs/architecture/remote-orchestration.md, seção 3.10).
/// </summary>
public static class RemoteLimits
{
    /// <summary>A versão deste contrato, anunciada em <see cref="DeviceCapabilities.ProtocolVersion"/>.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Quanto uma operação espera ser aceita, se o controlador não disser.</summary>
    public static readonly TimeSpan DefaultAcceptanceWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// O máximo entre criar e expirar: máquina desligada não roda amanhã o que
    /// foi pedido hoje.
    /// </summary>
    public static readonly TimeSpan MaxAcceptanceWindow = TimeSpan.FromHours(24);

    /// <summary>Quanto o relógio de quem pede pode estar adiantado em relação ao de quem confere.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Quanto o agente lembra os ids já vistos: mais que qualquer <see cref="MaxAcceptanceWindow"/>.</summary>
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromDays(30);

    /// <summary>Sem resposta até aqui, a aprovação vale "negar".</summary>
    public static readonly TimeSpan DefaultApprovalLifetime = TimeSpan.FromMinutes(10);

    /// <summary>O mesmo teto do prompt guardado no ambiente (<c>TaskDevelopment.AgentPrompt</c>).</summary>
    public const int MaxPromptLength = 8000;

    /// <summary>Texto de um evento, já mascarado. Uma linha maior é cortada e marcada.</summary>
    public const int MaxEventTextLength = 8 * 1024;

    public const int MaxEventsPerBatch = 200;

    /// <summary>O SPKI de uma chave P-256 tem 91 bytes, 122 caracteres em base64url; folga para nada além disso.</summary>
    public const int MaxSubjectPublicKeyInfoLength = 200;

    /// <summary>Quanto um pedido de pareamento espera a confirmação na máquina executora (RS-08).</summary>
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Pedidos de pareamento em aberto por conta: ninguém enche a tela do executor de pedidos.</summary>
    public const int MaxOpenPairings = 10;

    /// <summary>Operações não terminadas por máquina executora (RS-31): acima, o servidor recusa o pedido novo.</summary>
    public const int MaxOpenOperationsPerExecutor = 20;

    /// <summary>Sem sinal de vida por mais que isto, a máquina aparece desligada.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(90);

    /// <summary>Entregue e sem aceite por mais que isto, a operação volta para a fila.</summary>
    public static readonly TimeSpan DispatchTimeout = TimeSpan.FromMinutes(2);

    /// <summary>O máximo que uma aprovação espera; acima, o servidor recusa o pedido.</summary>
    public static readonly TimeSpan MaxApprovalLifetime = TimeSpan.FromHours(1);

    /// <summary>Sem o hub (proxy que barra WebSocket), de quanto em quanto tempo o agente pergunta.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Eventos guardados por operação. Acima disto, a saída (<see cref="RemoteEventKind.Output"/>)
    /// é confirmada e descartada; o resto continua: o fim da operação nunca se perde.
    /// </summary>
    public const int MaxEventsPerOperation = 10_000;

    public const int MaxEventPageSize = 500;

    /// <summary>A linha de resumo do resultado.</summary>
    public const int MaxResultSummaryLength = 500;

    /// <summary>O resumo de uma aprovação, como a tela mostra.</summary>
    public const int MaxApprovalSummaryLength = 500;

    /// <summary>Conversas por lote de relato do agente (ADR-091).</summary>
    public const int MaxSessionsPerBatch = 50;

    /// <summary>As operações de uma conversa que a linha do tempo traz: as mais recentes.</summary>
    public const int MaxSessionOperations = 100;

    /// <summary>As conversas que a lista da conta traz, no máximo.</summary>
    public const int MaxSessionListSize = 200;

    /// <summary>O começo do prompt que vai no primeiro evento dele, para a linha do tempo mostrar "Você: …".</summary>
    public const int MaxPromptPreviewLength = 1500;

    /// <summary>O texto da atividade de uma conversa.</summary>
    public const int MaxSessionActivityLength = 200;
}
