using System.Globalization;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Mcp.Tools;

// As respostas das ferramentas (ADR-059). Uma tradução fina dos registros da
// Application para o que uma IA lê bem: datas locais em AAAA-MM-DD, horas em
// HH:mm, durações em segundos e horas decimais, e nenhum campo de senha.

/// <summary>Uma duração legível por gente e por máquina.</summary>
public sealed record Duration(long Seconds, double Hours, string Label)
{
    public static Duration Of(TimeSpan span) =>
        new((long)span.TotalSeconds, Math.Round(span.TotalHours, 2), WorkTimeFormatter.Duration(span));

    public static Duration? Of(TimeSpan? span) => span is { } value ? Of(value) : null;
}

public sealed record TagRef(Guid Id, string Name);

public sealed record DeadlineInfo(string Date, string Time, string Status, string Label, string? Countdown)
{
    public static DeadlineInfo? Of(TaskDeadlineView? view) => view is null
        ? null
        : new DeadlineInfo(
            Text.Date(view.Deadline.Date),
            Text.Time(view.Deadline.Time),
            view.Status.ToString(),
            view.Label,
            view.Countdown);
}

/// <summary>Uma tarefa numa lista: o bastante para escolher, sem a anotação inteira.</summary>
public sealed record TaskSummary(
    Guid Id,
    Guid OccurrenceId,
    string Title,
    string Priority,
    string Status,
    string Lifecycle,
    string? ScheduledDate,
    string? ScheduledTime,
    DeadlineInfo? Deadline,
    bool IsOverdue,
    IReadOnlyList<TagRef> Tags,
    string? IssueKey,
    Duration Logged,
    Duration? Estimate,
    bool TimerRunning,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? CompletedAt)
{
    public static TaskSummary Of(TaskListItem item) => new(
        item.Task.TaskId,
        item.Task.OccurrenceId,
        item.Task.Title,
        item.Task.Priority.ToString(),
        item.Status.ToString(),
        item.Lifecycle.ToString(),
        Text.Date(item.Task.ScheduledDate),
        Text.Time(item.Task.ScheduledTime),
        DeadlineInfo.Of(item.Task.Deadline),
        item.IsOverdue,
        Text.Tags(item.Task.Tags),
        item.Task.External?.Id,
        Duration.Of(item.Task.Logged),
        Duration.Of(item.Task.Estimate),
        item.Task.TimerStartedAt is not null,
        item.CreatedAt,
        item.CompletedAt);

    /// <summary>Do quadro Hoje, que não traz o ciclo de vida: está na lista principal por definição.</summary>
    public static TaskSummary Of(TodayTask task, bool isCompleted, bool isOverdue) => new(
        task.TaskId,
        task.OccurrenceId,
        task.Title,
        task.Priority.ToString(),
        isCompleted ? nameof(TaskItemStatus.Completed) : nameof(TaskItemStatus.Pending),
        isCompleted ? nameof(TaskLifecycle.Completed) : nameof(TaskLifecycle.Active),
        Text.Date(task.ScheduledDate),
        Text.Time(task.ScheduledTime),
        DeadlineInfo.Of(task.Deadline),
        isOverdue,
        Text.Tags(task.Tags),
        task.External?.Id,
        Duration.Of(task.Logged),
        Duration.Of(task.Estimate),
        task.TimerStartedAt is not null,
        null,
        null);

}

public sealed record ReminderInfo(
    bool Enabled,
    string Anchor,
    int OffsetMinutes,
    bool RepeatUntilAcknowledged,
    int RepeatEveryMinutes,
    string Channels)
{
    public static ReminderInfo? Of(ReminderPolicy? policy) => policy is null
        ? null
        : new ReminderInfo(
            policy.IsEnabled,
            policy.Anchor.ToString(),
            (int)policy.Offset.TotalMinutes,
            policy.RepeatUntilAcknowledged,
            (int)policy.RepeatEvery.TotalMinutes,
            policy.Channels.ToString());
}

public sealed record ExternalInfo(string Provider, string Key, string Title, string Url, string? IssueType, string? Status, DateTimeOffset SyncedAt);

public sealed record WorktreeInfo(Guid DevelopmentId, string Repository, string Branch, string SourceBranch, string Path);

/// <summary>Uma tarefa inteira: os mesmos campos que a janela da tarefa mostra.</summary>
public sealed record TaskDetail(
    Guid Id,
    Guid OccurrenceId,
    string Title,
    string? Description,
    string Priority,
    string Status,
    string Lifecycle,
    string? ScheduledDate,
    string? ScheduledTime,
    DeadlineInfo? Deadline,
    string? DeadlineAlerts,
    bool IsOverdue,
    IReadOnlyList<TagRef> Tags,
    ExternalInfo? Issue,
    string? NextAction,
    Duration? Estimate,
    Duration Logged,
    DateTimeOffset? TimerStartedAt,
    ReminderInfo? Reminder,
    IReadOnlyList<WorktreeInfo> Worktrees,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ConcludedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset? DeletedAt,
    string? DeletedBy)
{
    public static TaskDetail Of(TaskListItem item)
    {
        var task = item.Task;

        return new TaskDetail(
            task.TaskId,
            task.OccurrenceId,
            task.Title,
            task.Notes,
            task.Priority.ToString(),
            item.Status.ToString(),
            item.Lifecycle.ToString(),
            Text.Date(task.ScheduledDate),
            Text.Time(task.ScheduledTime),
            DeadlineInfo.Of(task.Deadline),
            task.DeadlineAlerts?.ToString(),
            item.IsOverdue,
            Text.Tags(task.Tags),
            task.External is { } link
                ? new ExternalInfo(link.Provider, link.Id, link.Title, link.Url, link.IssueType, link.Status, link.SyncedAt)
                : null,
            task.NextAction,
            Duration.Of(task.Estimate),
            Duration.Of(task.Logged),
            task.TimerStartedAt,
            ReminderInfo.Of(task.Reminder),
            task.Worktrees?.Select(worktree => new WorktreeInfo(
                worktree.DevelopmentId, worktree.RepositoryName, worktree.Branch, worktree.SourceBranch, worktree.WorktreePath)).ToList() ?? [],
            item.CreatedAt,
            item.CompletedAt,
            item.ConcludedAt,
            item.ArchivedAt,
            item.DeletedAt,
            item.DeletedBy);
    }
}

public sealed record TaskPage(IReadOnlyList<TaskSummary> Tasks, int Total, int Offset, int Limit, bool Truncated, string? Note);

/// <summary>O resultado de uma operação: o que a tarefa ficou sendo depois dela, relido do banco.</summary>
public sealed record TaskOperationResult(string Message, TaskDetail Task);

public sealed record TimerInfo(Guid EntryId, Guid TaskId, Guid OccurrenceId, string TaskTitle, DateTimeOffset StartedAt, string StartedAtLocal, Duration Elapsed);

public sealed record TimeEntryInfo(
    Guid Id,
    Guid TaskId,
    string TaskTitle,
    string? IssueKey,
    IReadOnlyList<TagRef> Tags,
    string Source,
    string StartDate,
    string StartTime,
    string? EndDate,
    string? EndTime,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    Duration Duration,
    Duration DurationInRange,
    string? Note,
    bool IsRunning,
    bool CrossesMidnight,
    bool IsLong,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    bool TaskArchived,
    bool TaskInTrash)
{
    public static TimeEntryInfo Of(TimeEntryReportItem item) => new(
        item.Id,
        item.TaskId,
        item.TaskTitle,
        item.ExternalKey,
        Text.Tags(item.Tags),
        item.Source.ToString(),
        Text.Date(item.StartDate),
        Text.Time(item.StartTime),
        Text.Date(item.EndDate),
        Text.Time(item.EndTime),
        item.StartedAt,
        item.EndedAt,
        Duration.Of(item.Duration),
        Duration.Of(item.DurationInRange),
        item.Note,
        item.IsRunning,
        item.CrossesMidnight,
        item.IsLong,
        item.CreatedAt,
        item.UpdatedAt,
        item.TaskArchived,
        item.TaskInTrash);
}

internal static class Text
{
    public static IReadOnlyList<TagRef> Tags(IReadOnlyList<TagBadge>? tags) =>
        tags?.Select(tag => new TagRef(tag.Id, tag.Name)).ToList() ?? [];

    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? Date(DateOnly? date) => date is { } value ? Date(value) : null;

    public static string Time(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string? Time(TimeOnly? time) => time is { } value ? Time(value) : null;
}
