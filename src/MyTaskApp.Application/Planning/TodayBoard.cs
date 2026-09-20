using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Planning;

public sealed record TodayTask(
    Guid OccurrenceId,
    Guid TaskId,
    string Title,
    TaskPriority Priority,
    DateOnly? ScheduledDate,
    TimeOnly? ScheduledTime,
    bool IsLate,
    /// <summary>Ha quanto tempo o checklist espera atencao; <c>null</c> = nao espera.</summary>
    TimeSpan? WaitingForAttention = null,
    int ReminderStep = 0,
    ReminderPolicy? Reminder = null);

/// <summary>Tela "Hoje" (§9), já separada em seções mutuamente exclusivas.</summary>
public sealed record TodayBoard(
    DateOnly Date,
    IReadOnlyList<TodayTask> Overdue,
    IReadOnlyList<TodayTask> Now,
    IReadOnlyList<TodayTask> Today,
    IReadOnlyList<TodayTask> Unscheduled,
    IReadOnlyList<TodayTask> Completed)
{
    public int TotalVisible =>
        Overdue.Count + Now.Count + Today.Count + Unscheduled.Count + Completed.Count;

    public int RemainingCount => Overdue.Count + Now.Count + Today.Count + Unscheduled.Count;
}
