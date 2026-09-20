using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Reminders;

/// <summary>Linha crua de um lembrete vencido; leitura não passa pelo agregado (ADR-005).</summary>
public sealed record DueReminderRow(
    Guid OccurrenceId,
    Guid TaskId,
    string Title,
    TaskPriority Priority,
    DateOnly? ScheduledDate,
    TimeOnly? ScheduledTime,
    DateTimeOffset NextFireAtUtc,
    int Attempt,
    DateTimeOffset? WaitingSinceUtc,
    AlertChannels Channels);

public interface IDueReminderQuery
{
    /// <summary>
    /// Lembretes armados que já venceram, do mais antigo para o mais novo. O
    /// teto existe para que abrir o app depois de um mês não vire uma avalanche.
    /// </summary>
    Task<IReadOnlyList<DueReminderRow>> GetDueAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken = default);
}
