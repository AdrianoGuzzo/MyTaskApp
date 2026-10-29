namespace MyTaskApp.Domain.Agents;

/// <summary>Em que pé está uma sessão de agente (ADR-030).</summary>
/// <remarks>
/// No terminal, quem sai é o usuário, e isso é <see cref="Exited"/>. Só a
/// sessão sem terminal (ADR-089) é parada pelo app, e só a pedido:
/// <see cref="Stopped"/>. Os números são gravados no banco; não reordenar.
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

    /// <summary>
    /// Parada a pedido (<c>agent.stop</c>): o processo, se havia, foi
    /// encerrado, e a conversa não recebe mais prompts.
    /// </summary>
    Stopped = 5,
}

/// <summary>Como o agente roda. Os números são gravados no banco; não reordenar.</summary>
public enum AgentSessionMode
{
    /// <summary>Num terminal, com o usuário olhando, acompanhado pelos hooks (ADR-030, ADR-037).</summary>
    Interactive = 0,

    /// <summary>
    /// Sem terminal, pedido de longe (ADR-089): cada prompt é um processo, e a
    /// sessão é a conversa. Entre um prompt e outro ela fica
    /// <see cref="AgentSessionStatus.Exited"/>, pronta para continuar.
    /// </summary>
    Headless = 1,
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

/// <summary>Para que o agente foi aberto. Os números são gravados no banco; não reordenar.</summary>
public enum AgentSessionKind
{
    /// <summary>No worktree de um ambiente da tarefa, para desenvolver (ADR-030, ADR-031).</summary>
    Development = 0,

    /// <summary>
    /// A Consulta Inteligente da tarefa (ADR-081): só leitura, nas pastas
    /// escolhidas, sem ambiente.
    /// </summary>
    Consultation = 1,
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

    public const int MaxChoiceLength = 32;

    /// <summary>O apelido do controlador, o mesmo teto do pareamento.</summary>
    public const int MaxRemoteOriginLength = 60;

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

    /// <summary>Desenvolvimento (com ambiente) ou Consulta Inteligente (sem ambiente).</summary>
    public AgentSessionKind Kind { get; private set; }

    public AgentSessionMode Mode { get; private set; }

    public bool IsHeadless => Mode == AgentSessionMode.Headless;

    /// <summary>
    /// A operação remota do prompt atual, ou do último (ADR-089): é por ela que
    /// a outra máquina acompanha a conversa. <c>null</c> no terminal.
    /// </summary>
    public Guid? RemoteOperationId { get; private set; }

    /// <summary>
    /// Quando o prompt atual começou, na sessão sem terminal. A conversa pode
    /// ter horas; a folga da reconciliação para quem ainda não tem processo
    /// conta daqui.
    /// </summary>
    public DateTimeOffset? PromptStartedAt { get; private set; }

    /// <summary>
    /// O perfil do agente sem terminal (ADR-089), escolhido no primeiro prompt:
    /// os seguintes continuam com o mesmo, como o modelo e o esforço.
    /// </summary>
    public string? HeadlessProfileId { get; private set; }

    /// <summary>O modelo do agente sem terminal (ADR-040); <c>null</c> é o padrão dele.</summary>
    public string? Model { get; private set; }

    public string? Effort { get; private set; }

    /// <summary>
    /// Quem abriu a conversa sem terminal: o apelido do controlador que mandou
    /// o primeiro prompt ("Notebook", "Celular"). É o que o card desta máquina
    /// mostra, para a conversa aberta de longe não rodar escondida.
    /// </summary>
    public string? RemoteOrigin { get; private set; }

    /// <summary>A sessão sem terminal pode receber mais um prompt: o último acabou e ninguém a parou.</summary>
    public bool CanContinue => IsHeadless && Status == AgentSessionStatus.Exited;

    public bool IsConsultation => Kind == AgentSessionKind.Consultation;

    /// <summary>
    /// O hash do segredo com que o Claude da consulta fala com o servidor MCP
    /// (ADR-081). Só vale enquanto a sessão está ativa: o processo saiu, o
    /// segredo morre junto.
    /// </summary>
    public string? ConsultationTokenHash { get; private set; }

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

    /// <summary>
    /// A conversa sem terminal de um ambiente, pedida de longe (ADR-089). O id
    /// da conversa nasce aqui (<c>--session-id</c>), e os prompts seguintes o
    /// continuam (<c>--resume</c>).
    /// </summary>
    public static AgentSession CreateHeadless(
        Guid taskItemId,
        Guid taskDevelopmentId,
        string providerId,
        string command,
        string workingDirectory,
        Guid conversationId,
        Guid remoteOperationId,
        string profileId,
        string? model,
        string? effort,
        DateTimeOffset startedAt,
        string? remoteOrigin = null)
    {
        if (conversationId == Guid.Empty || remoteOperationId == Guid.Empty)
        {
            throw new DomainException("A sessão sem terminal precisa da conversa e da operação.");
        }

        var session = Create(taskItemId, taskDevelopmentId, providerId, command, workingDirectory, startedAt);
        session.Mode = AgentSessionMode.Headless;
        session.HeadlessProfileId = Required(profileId, MaxChoiceLength, "Informe o perfil da sessão.");
        session.Model = Truncate(model, MaxChoiceLength);
        session.Effort = Truncate(effort, MaxChoiceLength);
        session.RemoteOrigin = Truncate(remoteOrigin, MaxRemoteOriginLength);
        session.ExternalSessionId = conversationId.ToString();
        session.RemoteOperationId = remoteOperationId;
        session.PromptStartedAt = startedAt;
        return session;
    }

    /// <summary>
    /// Mais um prompt na mesma conversa: a sessão volta a iniciar, agora pela
    /// operação <paramref name="remoteOperationId"/>. O processo é outro; o
    /// PID antigo sai, para a reconciliação não olhar para ele.
    /// </summary>
    public void ContinueWith(Guid remoteOperationId, DateTimeOffset at)
    {
        if (!IsHeadless)
        {
            throw new DomainException("Só a sessão sem terminal continua por um novo prompt.");
        }

        if (Status == AgentSessionStatus.Stopped)
        {
            throw new DomainException("A conversa foi parada. Abra outra para continuar.");
        }

        if (IsActive)
        {
            throw new DomainException("O Claude ainda está respondendo o prompt anterior.");
        }

        if (Status != AgentSessionStatus.Exited || remoteOperationId == Guid.Empty)
        {
            throw new DomainException("A conversa não chegou a abrir. Abra outra.");
        }

        Status = AgentSessionStatus.Starting;
        RemoteOperationId = remoteOperationId;
        PromptStartedAt = at;
        ProcessId = null;
        ProcessStartedAt = null;
        EndedAt = null;
        Activity = AgentActivity.Working;
        ActivityMessage = null;
        ActivityChangedAt = at;
    }

    /// <summary>
    /// O prompt sem terminal acabou: o que o Claude estava fazendo no fim e o
    /// fim do processo. Se uma reconciliação viu o processo sair antes e já
    /// fechou a sessão, o "como acabou" ainda é gravado, porque só quem leu a
    /// saída sabe. A conversa parada a pedido não muda.
    /// </summary>
    public void EndHeadlessPrompt(AgentActivity activity, string? message, DateTimeOffset at)
    {
        if (!IsHeadless)
        {
            throw new DomainException("Só a sessão sem terminal termina um prompt.");
        }

        if (Status == AgentSessionStatus.Stopped || !Enum.IsDefined(activity))
        {
            return;
        }

        if (IsActive)
        {
            RecordActivity(activity, message, at);
            MarkExited(at);
            return;
        }

        if (Status == AgentSessionStatus.Exited)
        {
            Activity = activity;
            ActivityMessage = Truncate(message, MaxActivityMessageLength);
            ActivityChangedAt = at;
        }
    }

    /// <summary>
    /// Parada a pedido (<c>agent.stop</c>), com ou sem prompt rodando: a
    /// conversa não recebe mais nada. Idempotente.
    /// </summary>
    public void MarkStopped(DateTimeOffset at)
    {
        if (!IsHeadless)
        {
            throw new DomainException("A sessão no terminal é encerrada no próprio terminal.");
        }

        if (Status == AgentSessionStatus.Stopped)
        {
            return;
        }

        // O fim do último prompt fica; parar no meio de um é agora.
        EndedAt = IsActive ? at : EndedAt ?? at;
        Status = AgentSessionStatus.Stopped;
    }

    /// <summary>
    /// Uma Consulta Inteligente (ADR-081): da tarefa, sem ambiente. O id da
    /// conversa nasce aqui (<c>--session-id</c>), e é ele que "Reabrir" retoma.
    /// </summary>
    public static AgentSession CreateConsultation(
        Guid taskItemId,
        string providerId,
        string command,
        string workingDirectory,
        string externalSessionId,
        DateTimeOffset startedAt)
    {
        if (taskItemId == Guid.Empty)
        {
            throw new DomainException("A sessão precisa de uma tarefa.");
        }

        var session = new AgentSession(
            Guid.CreateVersion7(startedAt),
            taskItemId,
            null,
            Required(providerId, MaxProviderIdLength, "Informe o agente da sessão."),
            Required(command, MaxPathLength, "Informe o comando da sessão."),
            Required(workingDirectory, MaxPathLength, "Informe a pasta da sessão."),
            startedAt)
        {
            Kind = AgentSessionKind.Consultation,
            ExternalSessionId = Required(externalSessionId, MaxExternalSessionIdLength, "Informe a conversa da sessão."),
        };

        return session;
    }

    /// <summary>
    /// Dá à consulta o segredo do servidor MCP. Antes de o terminal abrir,
    /// porque o segredo vai no ambiente do processo.
    /// </summary>
    public void GrantConsultationAccess(string tokenHash)
    {
        if (!IsConsultation)
        {
            throw new DomainException("Só uma Consulta Inteligente acessa as ferramentas de consulta.");
        }

        if (Status != AgentSessionStatus.Starting)
        {
            throw new DomainException("O acesso da consulta só é dado antes de o agente abrir.");
        }

        ConsultationTokenHash = ValidHash(tokenHash, "O segredo da consulta é inválido.");
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

        HookTokenHash = ValidHash(tokenHash, "O segredo do acompanhamento é inválido.");
    }

    private static string ValidHash(string tokenHash, string message) =>
        tokenHash is { Length: HookTokenHashLength } && tokenHash.All(char.IsAsciiHexDigit)
            ? tokenHash.ToUpperInvariant()
            : throw new DomainException(message);

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
