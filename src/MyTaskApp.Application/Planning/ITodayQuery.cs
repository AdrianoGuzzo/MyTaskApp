using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Planning;

/// <summary>Linha crua vinda do banco; a classificação acontece no domínio.</summary>
public sealed record TodayOccurrenceRow(
    Guid OccurrenceId,
    Guid TaskId,
    string Title,
    TaskPriority Priority,
    DateOnly? ScheduledDate,
    TimeOnly? ScheduledTime,
    TaskItemStatus Status,
    DateTimeOffset? CompletedAt,
    // Acrescentados no fim, com default, para nao obrigar cada helper de teste
    // existente a mudar no mesmo commit.
    DateTimeOffset? ReminderWaitingSinceUtc = null,
    int ReminderAttempt = 0,
    ReminderPolicy? TaskReminder = null,
    /// <summary>A anotação livre do checklist (§12), em Markdown.</summary>
    string? Description = null,
    /// <summary>A casa escolhida à mão; <c>null</c> = ordem natural da seção.</summary>
    int? Position = null,
    /// <summary>As etiquetas do checklist, em ordem alfabética (ADR-025).</summary>
    IReadOnlyList<TagBadge>? Tags = null,
    /// <summary>Os agentes de IA em execução para a tarefa, um por ambiente (ADR-030, ADR-031).</summary>
    IReadOnlyList<ActiveAgentRow>? ActiveAgents = null,
    /// <summary>Os worktrees prontos da tarefa, um por ambiente (ADR-031, ADR-032).</summary>
    IReadOnlyList<WorktreeRow>? Worktrees = null);

/// <summary>
/// Um agente em execução: em qual ambiente e de qual repositório. Ambiente
/// <c>null</c> é sessão de antes de haver vários ambientes, ou de um que saiu da lista.
/// </summary>
public sealed record ActiveAgentRow(
    Guid? DevelopmentId,
    string ProviderId,
    string? RepositoryPath = null,
    string? Branch = null);

/// <summary>
/// Um worktree pronto da tarefa. Só caminhos e nomes: se tem alteração ou
/// commit por enviar é pergunta para o Git, nunca para o banco (ADR-032).
/// </summary>
public sealed record WorktreeRow(
    Guid DevelopmentId,
    string RepositoryPath,
    string Branch,
    string SourceBranch,
    string WorktreePath);

/// <summary>O que a linha precisa de uma etiqueta: a bolinha e o nome do balão.</summary>
public sealed record TagBadge(Guid Id, string Name, string ColorHex);

public interface ITodayQuery
{
    /// <summary>
    /// Candidatas ao quadro de hoje: pendentes até <paramref name="today"/> e
    /// concluídas recentes. Filtrar no banco evita trazer o histórico inteiro.
    /// </summary>
    Task<IReadOnlyList<TodayOccurrenceRow>> GetCandidatesAsync(
        DateOnly today,
        CancellationToken cancellationToken = default);
}
