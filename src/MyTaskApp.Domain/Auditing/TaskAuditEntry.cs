namespace MyTaskApp.Domain.Auditing;

/// <summary>
/// Uma linha da trilha de auditoria do ciclo de vida de um checklist (§8).
/// Imutável por construção: auditoria que se edita não é auditoria.
/// </summary>
/// <remarks>
/// <para>
/// <b>Não há chave estrangeira para <c>Tasks</c>, e isso é a decisão central
/// deste tipo.</b> O §8 exige que o registro da exclusão definitiva sobreviva ao
/// checklist excluído; com FK, o <c>ON DELETE CASCADE</c> apagaria justamente a
/// prova da operação mais irreversível do app, e sem cascata o próprio
/// <c>DELETE</c> passaria a falhar. O preço é que <see cref="TaskId"/> pode
/// apontar para nada — o que é exatamente o que se quer dizer depois de um
/// <see cref="TaskAuditOperation.PermanentlyDeleted"/>.
/// </para>
/// <para>
/// <see cref="TaskTitle"/> é cópia, não junção, pela mesma razão: depois da
/// exclusão definitiva não há mais de onde ler o título, e uma trilha que só
/// mostra GUIDs não serve para investigar nada.
/// </para>
/// </remarks>
public sealed class TaskAuditEntry
{
    public const int MaxTitleLength = 200;
    public const int MaxDetailsLength = 1000;

    // Exigido pela materialização do EF Core.
    private TaskAuditEntry()
    {
        TaskTitle = string.Empty;
    }

    private TaskAuditEntry(
        Guid id,
        Guid taskId,
        string taskTitle,
        TaskAuditOperation operation,
        DateTimeOffset occurredAt,
        AuditActor actor,
        string? actorName,
        string? details)
    {
        Id = id;
        TaskId = taskId;
        TaskTitle = taskTitle;
        Operation = operation;
        OccurredAt = occurredAt;
        Actor = actor;
        ActorName = actorName;
        Details = details;
    }

    public Guid Id { get; }

    /// <summary>
    /// O checklist da operação. Pode não existir mais: ver as observações do
    /// tipo sobre a ausência de chave estrangeira.
    /// </summary>
    public Guid TaskId { get; }

    /// <summary>O título no instante da operação, copiado de propósito.</summary>
    public string TaskTitle { get; }

    public TaskAuditOperation Operation { get; }

    public DateTimeOffset OccurredAt { get; }

    public AuditActor Actor { get; }

    /// <summary>Quem, quando há identificação. Nulo no que o sistema fez sozinho.</summary>
    public string? ActorName { get; }

    /// <summary>Contexto livre — "prazo de 30 dias vencido", por exemplo.</summary>
    public string? Details { get; }

    /// <summary>Operação disparada por uma ação do usuário.</summary>
    public static TaskAuditEntry ByUser(
        Guid taskId,
        string taskTitle,
        TaskAuditOperation operation,
        DateTimeOffset occurredAt,
        string? userName,
        string? details = null) =>
        new(
            Guid.CreateVersion7(occurredAt),
            taskId,
            Truncate(taskTitle, MaxTitleLength),
            operation,
            occurredAt,
            AuditActor.User,
            TruncateOptional(userName, MaxTitleLength),
            TruncateOptional(details, MaxDetailsLength));

    /// <summary>Operação executada por uma rotina automática.</summary>
    public static TaskAuditEntry BySystem(
        Guid taskId,
        string taskTitle,
        TaskAuditOperation operation,
        DateTimeOffset occurredAt,
        string? details = null) =>
        new(
            Guid.CreateVersion7(occurredAt),
            taskId,
            Truncate(taskTitle, MaxTitleLength),
            operation,
            occurredAt,
            AuditActor.System,
            actorName: null,
            TruncateOptional(details, MaxDetailsLength));

    /// <summary>
    /// Corta em vez de recusar. Uma auditoria que <b>lança</b> por causa de um
    /// título comprido derrubaria a operação que ela deveria apenas registrar —
    /// e o registro existe justamente para o caso em que algo deu errado.
    /// </summary>
    private static string Truncate(string? value, int maxLength) =>
        TruncateOptional(value, maxLength) ?? string.Empty;

    private static string? TruncateOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }
}
