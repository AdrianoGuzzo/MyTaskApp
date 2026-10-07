using System.Globalization;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.TimeTracking;

/// <summary>A aba "Tempo" da tarefa (ADR-052): os períodos, o total e a estimativa.</summary>
public sealed record GetTaskTimeLog(Guid OccurrenceId);

/// <summary>
/// O histórico de uma ocorrência, já agrupado pelo dia do usuário e escrito. A
/// tela só copia — e o relógio do período que corre, que muda a cada segundo,
/// ela calcula de <see cref="RunningSince"/>.
/// </summary>
public sealed record TaskTimeLogView(
    Guid OccurrenceId,
    Guid TaskId,
    /// <summary>A soma dos períodos encerrados.</summary>
    TimeSpan Logged,
    /// <summary>O início do período que corre nesta tarefa; <c>null</c> = parado.</summary>
    DateTimeOffset? RunningSince,
    TimeSpan? Estimate,
    /// <summary>Do dia mais recente ao mais antigo.</summary>
    IReadOnlyList<TimeEntryDayView> Days)
{
    public bool IsRunning => RunningSince is not null;

    public bool IsEmpty => Days.Count == 0;

    /// <summary>O registrado mais o que corre: é o que se compara com a estimativa.</summary>
    public TimeSpan SpentAt(DateTimeOffset now) =>
        Logged + (RunningSince is { } since && now > since ? now - since : TimeSpan.Zero);
}

/// <summary>"Hoje", "Ontem" ou "05/10/2026", com os períodos daquele dia em ordem.</summary>
public sealed record TimeEntryDayView(
    DateOnly Date,
    string Label,
    TimeSpan Total,
    IReadOnlyList<TimeEntryView> Entries);

/// <summary>
/// Um período. As datas e horas locais são as que o diálogo de edição mostra;
/// os rótulos são os da lista.
/// </summary>
public sealed record TimeEntryView(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    TimeEntrySource Source,
    string? Note,
    DateOnly StartDate,
    TimeOnly StartTime,
    DateOnly? EndDate,
    TimeOnly? EndTime,
    string RangeLabel,
    string DurationLabel,
    string SourceLabel)
{
    public bool IsActive => EndedAt is null;
}

public sealed class GetTaskTimeLogHandler(
    ITaskItemRepository tasks,
    ITimeEntryRepository timeEntries,
    IUserClock clock,
    TimeProvider timeProvider)
{
    public async Task<TaskTimeLogView> HandleAsync(GetTaskTimeLog query, CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByOccurrenceIdAsync(query.OccurrenceId, cancellationToken);
        var entries = await timeEntries.ListForOccurrenceAsync(query.OccurrenceId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = clock.Today;

        var days = entries
            .Select(entry => Describe(entry, now))
            .GroupBy(view => view.StartDate)
            .OrderByDescending(group => group.Key)
            .Select(group => new TimeEntryDayView(
                group.Key,
                DayLabel(group.Key, today),
                group.Where(view => !view.IsActive)
                    .Aggregate(TimeSpan.Zero, (total, view) => total + (view.EndedAt!.Value - view.StartedAt)),
                group.OrderBy(view => view.StartedAt).ToList()))
            .ToList();

        return new TaskTimeLogView(
            query.OccurrenceId,
            task.Id,
            TimeLog.Logged(entries),
            entries.FirstOrDefault(entry => entry.IsActive)?.StartedAt,
            task.Estimate,
            days);
    }

    private TimeEntryView Describe(TimeEntry entry, DateTimeOffset now)
    {
        var start = TimeEntryText.Local(entry.StartedAt, clock);
        DateTime? end = entry.EndedAt is { } ended ? TimeEntryText.Local(ended, clock) : null;

        return new TimeEntryView(
            entry.Id,
            entry.StartedAt,
            entry.EndedAt,
            entry.Source,
            entry.Note,
            DateOnly.FromDateTime(start),
            TimeOnly.FromDateTime(start),
            end is { } e ? DateOnly.FromDateTime(e) : null,
            end is { } f ? TimeOnly.FromDateTime(f) : null,
            WorkTimeFormatter.Range(start, end),
            WorkTimeFormatter.Duration(entry.Duration(now)),
            WorkTimeFormatter.Source(entry.Source));
    }

    private static string DayLabel(DateOnly date, DateOnly today) => (today.DayNumber - date.DayNumber) switch
    {
        0 => "Hoje",
        1 => "Ontem",
        _ => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
    };
}
