using MyTaskApp.Application.Planning;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tasks;

/// <summary>Os números da lista inteira (ADR-059): quantas, em que estado, e o tempo recente.</summary>
public sealed record GetTaskStatistics;

/// <param name="Active">Na lista principal e não concluídas.</param>
/// <param name="Completed">Concluídas e ainda na lista principal.</param>
/// <param name="Overdue">Atrasadas pelo critério do quadro Hoje.</param>
/// <param name="PendingByPriority">Só as pendentes da lista principal.</param>
public sealed record TaskStatistics(
    DateOnly Today,
    int Total,
    int Active,
    int Completed,
    int Archived,
    int Trashed,
    int Overdue,
    int DueToday,
    int Unscheduled,
    int WithDeadline,
    IReadOnlyDictionary<TaskPriority, int> PendingByPriority,
    int CreatedLast7Days,
    int CompletedToday,
    int CompletedLast7Days,
    int CompletedLast30Days,
    TimeSpan WorkedToday,
    TimeSpan WorkedLast7Days);

public sealed class GetTaskStatisticsHandler(
    ITaskSearchQuery query,
    SearchTasksHandler search,
    TimeReportHandlers time,
    IUserClock clock,
    TimeProvider timeProvider)
{
    public async Task<TaskStatistics> HandleAsync(
        GetTaskStatistics request,
        CancellationToken cancellationToken = default)
    {
        var rows = await query.ListForCountingAsync(cancellationToken);
        var today = clock.Today;
        var now = timeProvider.GetUtcNow();

        bool InMainList(TaskCountRow row) => row.DeletedAt is null && row.ArchivedAt is null;

        var main = rows.Where(InMainList).ToList();
        var pending = main.Where(row => row.Status is TaskItemStatus.Pending).ToList();

        int CompletedSince(int days) => rows.Count(row =>
            row.DeletedAt is null
            && row.CompletedAt is { } completedAt
            && clock.ToLocalDate(completedAt).DayNumber > today.DayNumber - days);

        var week = await time.HandleAsync(
            new GetTimeSummary(today.AddDays(-6), today, TimeGrouping.Day),
            cancellationToken);

        var todayKey = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        return new TaskStatistics(
            today,
            rows.Count,
            main.Count(row => row.ConcludedAt is null),
            main.Count(row => row.ConcludedAt is not null),
            rows.Count(row => row.DeletedAt is null && row.ArchivedAt is not null),
            rows.Count(row => row.DeletedAt is not null),
            pending.Count(row => search.IsOverdue(AsBoardRow(row))),
            pending.Count(row => row.ScheduledDate == today || row.DeadlineDate == today),
            pending.Count(row => row.ScheduledDate is null),
            pending.Count(row => row.DeadlineDate is not null),
            Enum.GetValues<TaskPriority>().ToDictionary(
                priority => priority,
                priority => pending.Count(row => row.Priority == priority)),
            rows.Count(row => row.DeletedAt is null
                && clock.ToLocalDate(row.CreatedAt).DayNumber > today.DayNumber - 7),
            CompletedSince(1),
            CompletedSince(7),
            CompletedSince(30),
            week.Groups.FirstOrDefault(group => group.Key == todayKey)?.Total ?? TimeSpan.Zero,
            week.Total);
    }

    /// <summary>A linha mínima que o classificador do quadro precisa.</summary>
    private static TodayOccurrenceRow AsBoardRow(TaskCountRow row) =>
        new(
            row.OccurrenceId,
            row.TaskId,
            string.Empty,
            row.Priority,
            row.ScheduledDate,
            row.ScheduledTime,
            row.Status,
            row.CompletedAt,
            Deadline: row.DeadlineDate is { } date && row.DeadlineTime is { } time
                ? new TaskDeadline(date, time)
                : null);
}
