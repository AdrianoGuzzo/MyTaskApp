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

    private AgentSession(
        Guid id,
        Guid taskItemId,
        string providerId,
        string command,
        string workingDirectory,
        DateTimeOffset startedAt)
    {
        Id = id;
        TaskItemId = taskItemId;
        ProviderId = providerId;
        Command = command;
        WorkingDirectory = workingDirectory;
        StartedAt = startedAt;
        Status = AgentSessionStatus.Starting;
    }

    public Guid Id { get; }

    public Guid TaskItemId { get; }

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

    public static AgentSession Create(
        Guid taskItemId,
        string providerId,
        string command,
        string workingDirectory,
        DateTimeOffset startedAt)
    {
        if (taskItemId == Guid.Empty)
        {
            throw new DomainException("A sessão precisa de uma tarefa.");
        }

        return new AgentSession(
            Guid.CreateVersion7(startedAt),
            taskItemId,
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
