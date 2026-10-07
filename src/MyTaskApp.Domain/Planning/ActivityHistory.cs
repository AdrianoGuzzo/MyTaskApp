namespace MyTaskApp.Domain.Planning;

/// <summary>Uma ocorrência concluída dentro da janela do histórico.</summary>
public sealed record ActivityCompletion(Guid TaskId, Guid OccurrenceId, string Title, DateTimeOffset CompletedAt);

/// <summary>
/// Um período de trabalho que encosta na janela (ADR-052). <c>EndedAt</c> nulo é
/// o cronômetro que ainda corre.
/// </summary>
public sealed record ActivityPeriod(
    Guid TaskId,
    Guid OccurrenceId,
    string Title,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

/// <summary>
/// O que foi feito numa ocorrência num dia: concluída nele, trabalhada nele, ou
/// as duas coisas. Uma tarefa longa aparece uma vez em cada dia em que foi tocada.
/// </summary>
public sealed record ActivityItem(
    Guid TaskId,
    Guid OccurrenceId,
    string Title,
    /// <summary>A conclusão, se aconteceu neste dia; <c>null</c> = só trabalhada.</summary>
    DateTimeOffset? CompletedAt,
    /// <summary>O tempo trabalhado neste dia, já recortado na meia-noite do usuário.</summary>
    TimeSpan Worked)
{
    public bool IsCompleted => CompletedAt is not null;
}

/// <summary>Um dia do histórico, mesmo sem nada: o dia vazio também é resposta.</summary>
public sealed record ActivityDay(DateOnly Date, IReadOnlyList<ActivityItem> Items)
{
    public bool IsEmpty => Items.Count == 0;
}

/// <summary>Os dias do histórico, de hoje para trás.</summary>
public sealed record ActivityHistory(IReadOnlyList<ActivityDay> Days)
{
    public int Completed => Days.Sum(day => day.Items.Count(item => item.IsCompleted));

    public TimeSpan Worked => Days
        .SelectMany(day => day.Items)
        .Aggregate(TimeSpan.Zero, (total, item) => total + item.Worked);

    public bool IsEmpty => Days.All(day => day.IsEmpty);
}

/// <summary>
/// Monta o histórico dos últimos dias a partir do que já está gravado: as
/// conclusões das ocorrências e os períodos de trabalho. É uma projeção, e não
/// uma cópia — não há tabela de histórico. Função pura, como o
/// <see cref="TodayClassifier"/>: o fuso chega pronto, em
/// <paramref name="startOfDay"/> e <paramref name="toLocalDate"/>, e cada borda
/// vira um teste direto.
/// </summary>
public static class ActivityHistoryBuilder
{
    /// <param name="today">O dia do usuário; é o primeiro da lista.</param>
    /// <param name="dayCount">Quantos dias, contando hoje.</param>
    /// <param name="startOfDay">O instante da meia-noite local de um dia.</param>
    /// <param name="toLocalDate">O dia do usuário de um instante.</param>
    /// <param name="now">Onde termina o período que ainda corre.</param>
    public static ActivityHistory Build(
        DateOnly today,
        int dayCount,
        Func<DateOnly, DateTimeOffset> startOfDay,
        Func<DateTimeOffset, DateOnly> toLocalDate,
        DateTimeOffset now,
        IEnumerable<ActivityCompletion> completions,
        IEnumerable<ActivityPeriod> periods)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dayCount, 1);
        ArgumentNullException.ThrowIfNull(startOfDay);
        ArgumentNullException.ThrowIfNull(toLocalDate);
        ArgumentNullException.ThrowIfNull(completions);
        ArgumentNullException.ThrowIfNull(periods);

        var oldest = today.AddDays(1 - dayCount);

        // As fronteiras vêm do fuso, e não de "+24 h": o dia da troca de horário
        // de verão tem 23 ou 25 horas.
        var dates = Enumerable.Range(0, dayCount).Select(offset => oldest.AddDays(offset)).ToList();
        var bounds = dates.Append(today.AddDays(1)).Select(startOfDay).ToList();

        var cells = new Dictionary<(DateOnly Date, Guid OccurrenceId), Cell>();

        Cell CellFor(DateOnly date, Guid taskId, Guid occurrenceId, string title)
        {
            if (!cells.TryGetValue((date, occurrenceId), out var cell))
            {
                cell = new Cell(taskId, occurrenceId, title);
                cells[(date, occurrenceId)] = cell;
            }

            return cell;
        }

        foreach (var period in periods)
        {
            var end = period.EndedAt ?? now;

            if (end <= period.StartedAt)
            {
                continue;
            }

            // Um período que atravessa a meia-noite conta em cada dia o que
            // aconteceu nele: 23:00 → 01:30 é 1h ontem e 1h 30min hoje.
            for (var index = 0; index < dates.Count; index++)
            {
                var from = Max(period.StartedAt, bounds[index]);
                var until = Min(end, bounds[index + 1]);

                if (until <= from)
                {
                    continue;
                }

                var cell = CellFor(dates[index], period.TaskId, period.OccurrenceId, period.Title);
                cell.Worked += until - from;
                cell.LastAt = Max(cell.LastAt, until);
            }
        }

        foreach (var completion in completions)
        {
            var date = toLocalDate(completion.CompletedAt);

            if (date < oldest || date > today)
            {
                continue;
            }

            var cell = CellFor(date, completion.TaskId, completion.OccurrenceId, completion.Title);
            cell.CompletedAt = completion.CompletedAt;
            cell.LastAt = Max(cell.LastAt, completion.CompletedAt);
        }

        var byDate = cells
            .GroupBy(entry => entry.Key.Date)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ActivityItem>)group
                    .Select(entry => entry.Value)
                    // O mais recente primeiro: é a ordem em que se conta o dia.
                    .OrderByDescending(cell => cell.LastAt)
                    .ThenBy(cell => cell.Title, StringComparer.CurrentCultureIgnoreCase)
                    .Select(cell => new ActivityItem(
                        cell.TaskId,
                        cell.OccurrenceId,
                        cell.Title,
                        cell.CompletedAt,
                        cell.Worked))
                    .ToList());

        return new ActivityHistory(dates
            .AsEnumerable()
            .Reverse()
            .Select(date => new ActivityDay(date, byDate.GetValueOrDefault(date) ?? []))
            .ToList());
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    private sealed class Cell(Guid taskId, Guid occurrenceId, string title)
    {
        public Guid TaskId { get; } = taskId;

        public Guid OccurrenceId { get; } = occurrenceId;

        public string Title { get; } = title;

        public TimeSpan Worked { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }

        public DateTimeOffset LastAt { get; set; } = DateTimeOffset.MinValue;
    }
}
