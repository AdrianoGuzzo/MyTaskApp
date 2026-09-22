using Microsoft.Extensions.Options;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Domain.Planning;
using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Application.Planning;

/// <summary>
/// Monta a tela "Hoje": busca as candidatas, deixa o domínio decidir a seção de
/// cada uma e ordena cada seção pelo critério que faz sentido para ela.
/// </summary>
public sealed class GetTodayBoardHandler(
    ITodayQuery query,
    IUserClock clock,
    TimeProvider timeProvider,
    IOptions<ApplicationOptions> options)
{
    public async Task<TodayBoard> HandleAsync(CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var now = clock.CurrentTime;
        var window = options.Value.ToNowWindow();

        var nowUtc = timeProvider.GetUtcNow();

        var rows = await query.GetCandidatesAsync(today, cancellationToken);

        var placed = rows
            .Select(row => (Row: row, Placement: Place(row, today, now, window)))
            .Where(entry => entry.Placement is not null)
            .ToList();

        TodayTask ToTask((TodayOccurrenceRow Row, TodayPlacement? Placement) entry) =>
            Project(entry, nowUtc);

        IEnumerable<(TodayOccurrenceRow Row, TodayPlacement? Placement)> InSection(TodaySection section) =>
            placed.Where(entry => entry.Placement!.Section == section);

        return new TodayBoard(
            Date: today,
            Overdue: InSection(TodaySection.Overdue)
                .OrderBy(entry => entry.Row.ScheduledDate)
                .ThenBy(entry => entry.Row.ScheduledTime ?? TimeOnly.MinValue)
                .Select(ToTask)
                .ToList(),
            Now: InSection(TodaySection.Now)
                .OrderBy(entry => entry.Row.ScheduledTime)
                .Select(ToTask)
                .ToList(),
            Today: InSection(TodaySection.Today)
                .OrderBy(entry => entry.Row.ScheduledTime)
                .Select(ToTask)
                .ToList(),
            Unscheduled: InSection(TodaySection.Unscheduled)
                .OrderByDescending(entry => entry.Row.Priority)
                .ThenBy(entry => entry.Row.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(ToTask)
                .ToList(),
            Completed: InSection(TodaySection.Completed)
                .OrderByDescending(entry => entry.Row.CompletedAt)
                .Select(ToTask)
                .ToList());
    }

    private TodayPlacement? Place(
        TodayOccurrenceRow row,
        DateOnly today,
        TimeOnly now,
        NowWindow window) =>
        TodayClassifier.Classify(
            new TodayCandidate(
                row.ScheduledDate,
                row.ScheduledTime,
                row.Status,
                // O domínio raciocina em data local; a conversão é daqui (ADR-002).
                row.CompletedAt is { } completedAt ? clock.ToLocalDate(completedAt) : null),
            today,
            now,
            window);

    private static TodayTask Project(
        (TodayOccurrenceRow Row, TodayPlacement? Placement) entry,
        DateTimeOffset nowUtc)
    {
        var row = entry.Row;

        // Concluida nao espera mais nada, por mais que tenha esperado antes.
        var waiting = entry.Placement!.Section is not TodaySection.Completed
            && row.ReminderWaitingSinceUtc is { } since
                ? nowUtc - since
                : (TimeSpan?)null;

        return new TodayTask(
            row.OccurrenceId,
            row.TaskId,
            row.Title,
            row.Priority,
            row.ScheduledDate,
            row.ScheduledTime,
            entry.Placement!.IsLate,
            waiting,
            waiting is null
                ? 0
                : ReminderEscalation.LevelFor(
                    row.ReminderAttempt,
                    row.TaskReminder?.Channels ?? AlertChannels.All).Step,
            row.TaskReminder,
            row.Description);
    }
}
