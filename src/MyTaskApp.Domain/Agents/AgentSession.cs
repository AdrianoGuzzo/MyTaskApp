namespace MyTaskApp.Domain.Agents;

/// <summary>Em que pé está uma sessão de agente (ADR-030).</summary>
/// <remarks>
/// Sem "Stopped": o app não encerra o agente nesta versão — quem sai é o
/// usuário, no terminal, e isso é <see cref="Exited"/>. Os números são
/// gravados no banco; não reordenar.
/// </remarks>
public enum AgentSessionStatus
{
    /// <summary>
    /// Gravada antes de o terminal abrir. Se o app cair no meio, a reconciliação
    /// não acha processo nenhum e a encerra.
    /// </summary>
    Starting = 1,

    Running = 2,

    /// <summary>O processo terminou — pelo usuário ou sozinho.</summary>
    Exited = 3,

    /// <summary>O terminal nem chegou a abrir.</summary>
    Failed = 4,
}

/// <summary>
/// O que o agente está fazendo <b>dentro</b> do processo, segundo os hooks dele
/// (ADR-037). É outra dimensão do <see cref="AgentSessionStatus"/>: o status diz
/// se o processo existe; a atividade diz se ele está trabalhando ou esperando
/// alguém. Os números são gravados no banco; não reordenar.
/// </summary>
public enum AgentActivity
{
    /// <summary>Nenhum aviso ainda — sessão sem acompanhamento, ou que acabou de abrir.</summary>
    Unknown = 0,

    /// <summary>Recebeu uma mensagem, ou acabou de usar uma ferramenta.</summary>
    Working = 1,

    /// <summary>Parou numa pergunta, numa permissão ou num plano para aprovar.</summary>
    WaitingForUser = 2,

    /// <summary>Terminou a resposta: o resultado está pronto para o usuário olhar.</summary>
    WaitingReview = 3,

    /// <summary>A resposta terminou em erro (limite, API fora, autenticação).</summary>
    Failed = 4,
}

/// <summary>
/// Uma execução de um agente de IA de linha de comando (hoje, o Claude Code)
/// num terminal do sistema, aberta pelo app para uma tarefa (ADR-030).
/// </summary>
/// <remarks>
/// <para>
/// É um aggregate próprio, e não parte do <c>TaskItem</c>: quem muda o estado
/// dela é um processo de fora, observado pelo monitor, e carregar a tarefa
/// inteira para trocar um status seria desperdício. A tarefa é referenciada
/// pelo id, e as sessões antigas ficam como histórico.
/// </para>
/// <para>
/// O <see cref="ProcessId"/> sozinho não identifica o processo: o Windows
/// reaproveita PIDs. Por isso o horário de início do processo vai junto
/// (<see cref="ProcessStartedAt"/>) — PID igual com início diferente é outro
/// programa, e a sessão está encerrada.
/// </para>
/// </remarks>
public sealed class AgentSession
{
    public const int MaxProviderIdLength = 64;

    public const int MaxPathLength = 1024;

    public const int MaxFailureLength = 2000;

    public const int MaxActivityMessageLength = 500;

    public const int MaxExternalSessionIdLength = 128;

    /// <summary>SHA-256 em hexadecimal.</summary>
    public const int HookTokenHashLength = 64;

    private AgentSession(
        Guid id,
        Guid taskItemId,
        Guid? taskDevelopmentId,
        string providerId,
        string command,
        string workingDirectory,
        DateTimeOffset startedAt)
    {
        Id = id;
        TaskItemId = taskItemId;
        TaskDevelopmentId = taskDevelopmentId;
        ProviderId = providerId;
        Command = command;
        WorkingDirectory = workingDirectory;
        StartedAt = startedAt;
        Status = AgentSessionStatus.Starting;
    }

    public Guid Id { get; }

    public Guid TaskItemId { get; }

    /// <summary>
    /// O ambiente da tarefa em que o agente foi aberto (ADR-031): uma tarefa
    /// com vários repositórios tem um agente por ambiente. <c>null</c> quando o
    /// ambiente saiu da lista — a sessão fica só como histórico.
    /// </summary>
    public Guid? TaskDevelopmentId { get; private set; }

    /// <summary>Qual agente: <c>claude-code</c>. Texto, para um agente novo não pedir migration.</summary>
    public string ProviderId { get; }

    /// <summary>O executável iniciado, como caminho absoluto.</summary>
    public string Command { get; }

    /// <summary>A pasta em que o agente foi aberto — o worktree da tarefa.</summary>
    public string WorkingDirectory { get; }

    public int? ProcessId { get; private set; }

    /// <summary>Quando o <b>processo</b> começou, segundo o sistema. Distingue PID reaproveitado.</summary>
    public DateTimeOffset? ProcessStartedAt { get; private set; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? EndedAt { get; private set; }

    public AgentSessionStatus Status { get; private set; }

    /// <summary>O motivo, em pt-BR, quando <see cref="Status"/> é <see cref="AgentSessionStatus.Failed"/>.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Ainda pode haver um processo vivo por trás.</summary>
    public bool IsActive => Status is AgentSessionStatus.Starting or AgentSessionStatus.Running;

    /// <summary>
    /// O hash do segredo que só o processo desta sessão conhece (ADR-037).
    /// <c>null</c> = sessão aberta sem acompanhamento: nenhum aviso é aceito.
    /// </summary>
    public string? HookTokenHash { get; private set; }

    public bool IsMonitored => HookTokenHash is not null;

    /// <summary>O id que o próprio agente dá à conversa (o <c>session_id</c> do Claude).</summary>
    public string? ExternalSessionId { get; private set; }

    public AgentActivity Activity { get; private set; }

    /// <summary>O texto do último aviso que mudou a atividade: a pergunta, o erro.</summary>
    public string? ActivityMessage { get; private set; }

    public DateTimeOffset? ActivityChangedAt { get; private set; }

    /// <summary>O agente está parado esperando o usuário — e ainda está aberto.</summary>
    public bool NeedsAttention =>
        IsActive && Activity is AgentActivity.WaitingForUser or AgentActivity.WaitingReview or AgentActivity.Failed;

    public static AgentSession Create(
        Guid taskItemId,
        Guid taskDevelopmentId,
        string providerId,
        string command,
        string workingDirectory,
        DateTimeOffset startedAt)
    {
        if (taskItemId == Guid.Empty)
        {
            throw new DomainException("A sessão precisa de uma tarefa.");
        }

        if (taskDevelopmentId == Guid.Empty)
        {
            throw new DomainException("A sessão precisa de um ambiente de desenvolvimento.");
        }

        return new AgentSession(
            Guid.CreateVersion7(startedAt),
            taskItemId,
            taskDevelopmentId,
            Required(providerId, MaxProviderIdLength, "Informe o agente da sessão."),
            Required(command, MaxPathLength, "Informe o comando da sessão."),
            Required(workingDirectory, MaxPathLength, "Informe a pasta da sessão."),
            startedAt);
    }

    /// <summary>O terminal abriu: a partir daqui a sessão é o processo <paramref name="processId"/>.</summary>
    public void MarkRunning(int processId, DateTimeOffset processStartedAt)
    {
        if (Status != AgentSessionStatus.Starting)
        {
            throw new DomainException("Só uma sessão iniciando pode passar a executar.");
        }

        if (processId <= 0)
        {
            throw new DomainException("O processo da sessão é inválido.");
        }

        ProcessId = processId;
        ProcessStartedAt = processStartedAt;
        Status = AgentSessionStatus.Running;
    }

    /// <summary>
    /// O processo acabou. Idempotente para quem já acabou: o evento do processo
    /// e a reconciliação podem chegar os dois.
    /// </summary>
    public void MarkExited(DateTimeOffset at)
    {
        if (!IsActive)
        {
            return;
        }

        Status = AgentSessionStatus.Exited;
        EndedAt = at;
    }

    /// <summary>
    /// Liga o acompanhamento: a partir daqui, só quem apresentar o segredo cujo
    /// hash é <paramref name="tokenHash"/> muda a atividade. Antes de o terminal
    /// abrir, porque o segredo vai no ambiente do processo.
    /// </summary>
    public void EnableMonitoring(string tokenHash)
    {
        if (Status != AgentSessionStatus.Starting)
        {
            throw new DomainException("O acompanhamento só é ligado antes de o agente abrir.");
        }

        if (tokenHash is not { Length: HookTokenHashLength } || !tokenHash.All(char.IsAsciiHexDigit))
        {
            throw new DomainException("O segredo do acompanhamento é inválido.");
        }

        HookTokenHash = tokenHash.ToUpperInvariant();
    }

    /// <summary>
    /// Um aviso do agente mudou (ou não) o que ele está fazendo. Devolve
    /// <c>true</c> só quando a atividade <b>mudou</b> — é o que decide avisar o
    /// usuário, e o mesmo aviso repetido não avisa duas vezes.
    /// </summary>
    /// <remarks>
    /// Sessão encerrada não muda mais: o aviso atrasado de um processo que já
    /// saiu não pode reacender "aguardando você".
    /// </remarks>
    public bool RecordActivity(AgentActivity activity, string? message, DateTimeOffset at)
    {
        if (!IsActive || !Enum.IsDefined(activity))
        {
            return false;
        }

        var normalized = Truncate(message, MaxActivityMessageLength);

        if (activity == Activity)
        {
            // Mesma atividade com texto novo (outra pergunta): o texto acompanha,
            // mas não conta como mudança.
            if (normalized is not null)
            {
                ActivityMessage = normalized;
            }

            return false;
        }

        Activity = activity;
        ActivityMessage = normalized;
        ActivityChangedAt = at;
        return true;
    }

    /// <summary>Guarda o id da conversa do agente. Muda com <c>/clear</c> — fica o último.</summary>
    public void RecordExternalSession(string? externalSessionId)
    {
        if (Truncate(externalSessionId, MaxExternalSessionIdLength) is { } normalized)
        {
            ExternalSessionId = normalized;
        }
    }

    public void MarkFailed(string reason, DateTimeOffset at)
    {
        if (Status != AgentSessionStatus.Starting)
        {
            throw new DomainException("Só uma sessão iniciando pode falhar ao abrir.");
        }

        var normalized = string.IsNullOrWhiteSpace(reason) ? "Falha desconhecida." : reason.Trim();

        FailureReason = normalized.Length > MaxFailureLength ? normalized[..MaxFailureLength] : normalized;
        Status = AgentSessionStatus.Failed;
        EndedAt = at;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        var normalized = value?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return normalized.Length > maxLength ? normalized[..maxLength] : normalized;
    }

    private static string Required(string? value, int maxLength, string message)
    {
        var normalized = value?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            throw new DomainException(message);
        }

        if (normalized.Length > maxLength)
        {
            throw new DomainException($"O valor não pode passar de {maxLength} caracteres.");
        }

        return normalized;
    }
}
