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
    ReminderPolicy? TaskReminder = null);

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
