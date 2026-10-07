using System.Globalization;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Planning;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.History;

/// <summary>
/// "O que eu fiz nesta semana" (ADR-053): hoje e os dias anteriores, com o que
/// foi concluído e o que foi trabalhado em cada um.
/// </summary>
/// <param name="Days">Quantos dias, contando hoje. A tela pede sempre <see cref="Week"/>.</param>
public sealed record GetActivityHistory(int Days = GetActivityHistory.Week)
{
    public const int Week = 7;
}

/// <summary>O histórico já agrupado pelo dia do usuário e escrito: a tela só copia.</summary>
public sealed record ActivityHistoryView(
    /// <summary>De hoje para trás, sempre todos os dias pedidos.</summary>
    IReadOnlyList<ActivityDayView> Days,
    int Completed,
    TimeSpan Worked,
    /// <summary>"18 concluídas · 24h 35min", ou "Nenhuma atividade registrada".</summary>
    string Summary)
{
    public bool IsEmpty => Days.All(day => day.IsEmpty);
}

/// <summary>"HOJE · TER 06/10", "ONTEM · SEG 05/10", "DOM · 04/10".</summary>
public sealed record ActivityDayView(DateOnly Date, string Label, IReadOnlyList<ActivityItemView> Items)
{
    /// <summary>O dia sem nada continua na lista, com "Nenhuma atividade".</summary>
    public bool IsEmpty => Items.Count == 0;
}

/// <summary>
/// Uma linha do dia. <see cref="OccurrenceId"/> é o que abre a tarefa: numa série,
/// é a ocorrência daquele dia, e não a série.
/// </summary>
public sealed record ActivityItemView(
    Guid TaskId,
    Guid OccurrenceId,
    string Title,
    bool IsCompleted,
    /// <summary>"Concluída 17:42 · 2h 15min", "Concluída 17:42" ou "Trabalhado · 45min".</summary>
    string Detail);

public sealed class GetActivityHistoryHandler(
    IActivityHistoryQuery query,
    IUserClock clock,
    TimeProvider timeProvider)
{
    public async Task<ActivityHistoryView> HandleAsync(
        GetActivityHistory request,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Days, 1);

        var today = clock.Today;
        var now = timeProvider.GetUtcNow();

        // O dia começa na meia-noite do usuário, e não na de UTC (ADR-002): às
        // 22:00 em São Paulo já é amanhã em UTC.
        DateTimeOffset StartOfDay(DateOnly date) => clock.ToInstant(date, TimeOnly.MinValue);

        var rows = await query.GetAsync(
            StartOfDay(today.AddDays(1 - request.Days)),
            StartOfDay(today.AddDays(1)),
            cancellationToken);

        var history = ActivityHistoryBuilder.Build(
            today,
            request.Days,
            StartOfDay,
            clock.ToLocalDate,
            now,
            rows.Completions,
            rows.Periods);

        return new ActivityHistoryView(
            history.Days
                .Select(day => new ActivityDayView(
                    day.Date,
                    DayLabel(day.Date, today),
                    day.Items.Select(Describe).ToList()))
                .ToList(),
            history.Completed,
            history.Worked,
            Summary(history));
    }

    private ActivityItemView Describe(ActivityItem item) =>
        new(item.TaskId, item.OccurrenceId, item.Title, item.IsCompleted, Detail(item));

    private string Detail(ActivityItem item)
    {
        if (item.CompletedAt is not { } completedAt)
        {
            return $"Trabalhado · {WorkTimeFormatter.Duration(item.Worked)}";
        }

        var time = TimeZoneInfo.ConvertTime(completedAt, clock.TimeZone)
            .ToString("HH:mm", CultureInfo.InvariantCulture);

        // Concluída sem tempo lançado não ganha um "0min": seria afirmar que não
        // houve trabalho, e só não houve cronômetro.
        return item.Worked > TimeSpan.Zero
            ? $"Concluída {time} · {WorkTimeFormatter.Duration(item.Worked)}"
            : $"Concluída {time}";
    }

    internal static string DayLabel(DateOnly date, DateOnly today)
    {
        var weekday = DeadlineFormatter.WeekdayAbbreviation(date).ToUpperInvariant();
        var shortDate = date.ToString("dd/MM", CultureInfo.InvariantCulture);

        return (today.DayNumber - date.DayNumber) switch
        {
            0 => $"HOJE · {weekday} {shortDate}",
            1 => $"ONTEM · {weekday} {shortDate}",
            _ => $"{weekday} · {shortDate}",
        };
    }

    internal static string Summary(ActivityHistory history)
    {
        if (history.IsEmpty)
        {
            return "Nenhuma atividade registrada";
        }

        var completed = history.Completed == 1 ? "1 concluída" : $"{history.Completed} concluídas";

        return $"{completed} · {WorkTimeFormatter.Duration(history.Worked)}";
    }
}
