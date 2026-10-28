using System.Globalization;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.TimeTracking;

/// <summary>Em que blocos o relatório de horas soma (ADR-097).</summary>
public enum HoursScale
{
    /// <summary>Os dias de um mês.</summary>
    Day = 0,

    /// <summary>Os meses de um ano.</summary>
    Month = 1,

    /// <summary>Os anos, do primeiro com horas até o atual.</summary>
    Year = 2,
}

/// <summary>
/// O relatório de horas (ADR-097): quanto foi lançado em cada dia de um mês,
/// em cada mês de um ano ou em cada ano, com os indicadores do período.
/// </summary>
/// <param name="Anchor">Um dia dentro do período; <c>null</c> = hoje. Um dia no futuro vira hoje.</param>
/// <param name="TagId">Só as horas das tarefas com esta etiqueta.</param>
/// <param name="Device">De qual computador (ADR-062); <c>null</c> = este e as compartilhadas.</param>
public sealed record GetHoursHistory(
    HoursScale Scale = HoursScale.Day,
    DateOnly? Anchor = null,
    Guid? TagId = null,
    DeviceView? Device = null)
{
    /// <summary>Antes disto não há o que navegar: o app é de 2026.</summary>
    public const int FirstYear = 2000;
}

/// <summary>Um dia, um mês ou um ano do relatório, já escrito.</summary>
/// <param name="Key">"2026-10-14", "2026-10" ou "2026": identifica o bloco entre uma carga e outra.</param>
/// <param name="From">O primeiro dia local, inclusive.</param>
/// <param name="To">O último dia local, inclusive.</param>
/// <param name="Label">O rótulo curto do eixo: "14", "out", "2026".</param>
/// <param name="LongLabel">"ter, 14/10/2026", "outubro de 2026", "2026".</param>
/// <param name="Summary">A leitura inteira, para o balão: "ter, 14/10 · 6h 20min · 4 lançamentos".</param>
/// <param name="EntryCount">Quantos períodos entram aqui; o que vira a noite conta nos dois dias.</param>
/// <param name="ActiveDays">Em quantos dias do bloco houve horas.</param>
/// <param name="Level">De 0 a 4, pelas faixas de <see cref="HoursLevels"/>; só nos dias.</param>
public sealed record HoursBucket(
    string Key,
    DateOnly From,
    DateOnly To,
    string Label,
    string LongLabel,
    string Summary,
    TimeSpan Total,
    int EntryCount,
    int ActiveDays,
    bool IsWeekend,
    bool IsCurrent,
    bool IsFuture,
    int Level);

/// <summary>O total contra o do período anterior.</summary>
/// <param name="Label">Contra o quê: "vs 1–27 de setembro", "vs 2025".</param>
/// <param name="Change">A variação, em fração (0,12 = 12% a mais); <c>null</c> quando o anterior foi zero.</param>
public sealed record HoursComparison(string Label, TimeSpan Previous, double? Change);

/// <summary>O relatório de um período, agregado e escrito: a tela só desenha.</summary>
/// <param name="Title">"outubro de 2026", "2026" ou "Todos os anos".</param>
/// <param name="PreviousAnchor">Um dia do período anterior; <c>null</c> = não há.</param>
/// <param name="NextAnchor">Um dia do próximo; <c>null</c> quando ele começaria no futuro.</param>
/// <param name="Buckets">Todos os blocos do período, inclusive os sem horas e os futuros.</param>
/// <param name="Days">Os dias do período, para o mapa de calor; vazio na escala de anos.</param>
/// <param name="ElapsedDays">Os dias do período até hoje.</param>
/// <param name="AveragePerActiveDay">O total dividido pelos dias em que houve horas.</param>
/// <param name="AveragePerActiveBucket">A média dos blocos com horas: a linha de média do gráfico.</param>
/// <param name="Best">O bloco com mais horas; <c>null</c> sem horas.</param>
/// <param name="Comparison">Contra o período anterior; <c>null</c> na escala de anos.</param>
/// <param name="Tasks">O período inteiro por tarefa, da que mais teve horas para a que menos teve.</param>
/// <param name="IncludesRunning">O cronômetro correndo entrou, contado até agora.</param>
public sealed record HoursHistory(
    HoursScale Scale,
    DateOnly From,
    DateOnly To,
    string Title,
    DateOnly? PreviousAnchor,
    DateOnly? NextAnchor,
    IReadOnlyList<HoursBucket> Buckets,
    IReadOnlyList<HoursBucket> Days,
    TimeSpan Total,
    int EntryCount,
    int ActiveDays,
    int ElapsedDays,
    TimeSpan AveragePerActiveDay,
    TimeSpan AveragePerActiveBucket,
    HoursBucket? Best,
    HoursComparison? Comparison,
    IReadOnlyList<TimeSummaryGroup> Tasks,
    bool IncludesRunning)
{
    public bool IsEmpty => Total <= TimeSpan.Zero;
}

/// <summary>As faixas fixas do mapa de calor: o mesmo dia tem a mesma cor em qualquer ano.</summary>
public static class HoursLevels
{
    public const int Max = 4;

    /// <summary>0 sem horas; 1 abaixo de 2h; 2 abaixo de 5h; 3 abaixo de 8h; 4 de 8h para cima.</summary>
    public static int Of(TimeSpan day) => day.TotalHours switch
    {
        <= 0 => 0,
        < 2 => 1,
        < 5 => 2,
        < 8 => 3,
        _ => 4,
    };
}

/// <summary>
/// Monta o relatório com uma leitura só: os períodos que tocam o período (e o
/// trecho anterior, da comparação), repartidos pelos dias locais do usuário.
/// </summary>
/// <remarks>
/// O teto de <see cref="TimeReport.MaxSummaryDays"/> não vale aqui: a resposta
/// traz só os totais, nunca a lista de períodos. Na escala de anos, o histórico
/// inteiro passa pela memória — num banco pessoal, alguns milhares de linhas
/// por ano (ADR-097).
/// </remarks>
public sealed class GetHoursHistoryHandler(
    ITimeEntryReportQuery query,
    IUserClock clock,
    TimeProvider timeProvider,
    DevicePlacementState? placements = null)
{
    public async Task<HoursHistory> HandleAsync(GetHoursHistory request, CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var now = timeProvider.GetUtcNow();
        var anchor = Clamp(request.Anchor ?? today, today);
        var (from, to) = Window(request.Scale, anchor, today);
        var comparison = ComparedTo(request.Scale, from, to, today);

        var rows = await query.ListAsync(
            new TimeEntryReportCriteria(
                Since: request.Scale == HoursScale.Year ? null : StartOf(comparison?.From ?? from),
                Until: StartOf(to.AddDays(1)),
                TagId: request.TagId,
                Placement: placements?.Filter(request.Device ?? DeviceView.Here) ?? PlacementFilter.All),
            cancellationToken);

        if (request.Scale == HoursScale.Year)
        {
            var first = rows.Count == 0 ? today.Year : clock.ToLocalDate(rows.Min(row => row.StartedAt)).Year;
            from = new DateOnly(Math.Clamp(first, GetHoursHistory.FirstYear, today.Year), 1, 1);
        }

        var grid = new LocalDayGrid(comparison?.From ?? from, to, StartOf);
        var totals = new TimeSpan[grid.Count];
        var counts = new int[grid.Count];
        var bucketCounts = new int[BucketCount(request.Scale, from, to)];
        var previous = TimeSpan.Zero;
        var entryCount = 0;
        var includesRunning = false;
        var tasks = new Dictionary<Guid, TaskHours>();

        foreach (var row in rows)
        {
            var inWindow = TimeSpan.Zero;
            var lastBucket = -1;

            foreach (var (index, part) in grid.Split(row.StartedAt, row.EndedAt ?? now))
            {
                var date = grid.DateAt(index);

                if (date < from)
                {
                    previous += comparison is { } compared && date <= compared.To ? part : TimeSpan.Zero;
                    continue;
                }

                totals[index] += part;
                counts[index]++;
                inWindow += part;

                var bucket = BucketOf(request.Scale, from, date);

                if (bucket != lastBucket)
                {
                    bucketCounts[bucket]++;
                    lastBucket = bucket;
                }
            }

            if (inWindow <= TimeSpan.Zero)
            {
                continue;
            }

            entryCount++;
            includesRunning |= row.EndedAt is null;
            if (!tasks.TryGetValue(row.TaskId, out var task))
            {
                task = new TaskHours(row.TaskTitle);
                tasks[row.TaskId] = task;
            }

            task.Total += inWindow;
            task.Ids.Add(row.EntryId);
        }

        var days = new List<HoursBucket>(to.DayNumber - from.DayNumber + 1);

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var index = grid.IndexOf(date);
            days.Add(Day(date, totals[index], counts[index], today));
        }

        var buckets = request.Scale switch
        {
            HoursScale.Day => days,
            HoursScale.Month => Enumerable.Range(1, 12)
                .Select(month => Month(new DateOnly(from.Year, month, 1), days, bucketCounts[month - 1], today))
                .ToList(),
            _ => Enumerable.Range(from.Year, to.Year - from.Year + 1)
                .Select(year => Year(year, days, bucketCounts[year - from.Year], today))
                .ToList(),
        };

        var total = buckets.Aggregate(TimeSpan.Zero, (sum, bucket) => sum + bucket.Total);
        var activeDays = days.Count(day => day.Total > TimeSpan.Zero);
        var activeBuckets = buckets.Where(bucket => bucket.Total > TimeSpan.Zero).ToList();

        return new HoursHistory(
            request.Scale,
            from,
            to,
            Title(request.Scale, from),
            Previous(request.Scale, from),
            Next(request.Scale, from, today),
            buckets,
            request.Scale == HoursScale.Year ? [] : days,
            total,
            entryCount,
            activeDays,
            days.Count(day => !day.IsFuture),
            activeDays == 0 ? TimeSpan.Zero : total / activeDays,
            activeBuckets.Count == 0 ? TimeSpan.Zero : total / activeBuckets.Count,
            activeBuckets.MaxBy(bucket => bucket.Total),
            comparison is { } c ? new HoursComparison(c.Label, previous, Change(total, previous)) : null,
            tasks
                .Select(task => new TimeSummaryGroup(task.Key.ToString(), task.Value.Title, task.Value.Total, task.Value.Ids.Count, task.Value.Ids))
                .OrderByDescending(group => group.Total)
                .ThenBy(group => group.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            includesRunning);
    }

    private DateTimeOffset StartOf(DateOnly date) => clock.ToInstant(date, TimeOnly.MinValue);

    private static DateOnly Clamp(DateOnly anchor, DateOnly today)
    {
        if (anchor > today)
        {
            return today;
        }

        return anchor.Year < GetHoursHistory.FirstYear ? new DateOnly(GetHoursHistory.FirstYear, 1, 1) : anchor;
    }

    /// <summary>O mês do dia, o ano do dia, ou — na escala de anos — até o fim do ano atual.</summary>
    private static (DateOnly From, DateOnly To) Window(HoursScale scale, DateOnly anchor, DateOnly today) => scale switch
    {
        HoursScale.Day => (new DateOnly(anchor.Year, anchor.Month, 1), new DateOnly(anchor.Year, anchor.Month, 1).AddMonths(1).AddDays(-1)),
        HoursScale.Month => (new DateOnly(anchor.Year, 1, 1), new DateOnly(anchor.Year, 12, 31)),
        _ => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
    };

    /// <summary>
    /// O período anterior, contado de forma justa: com o período atual pela
    /// metade, só o mesmo trecho do anterior (1–27 de outubro contra 1–27 de
    /// setembro); com o período encerrado, inteiro contra inteiro.
    /// </summary>
    private static (DateOnly From, DateOnly To, string Label)? ComparedTo(HoursScale scale, DateOnly from, DateOnly to, DateOnly today)
    {
        if (scale == HoursScale.Year)
        {
            return null;
        }

        var previousFrom = scale == HoursScale.Day ? from.AddMonths(-1) : from.AddYears(-1);

        if (previousFrom.Year < GetHoursHistory.FirstYear)
        {
            return null;
        }

        var partial = from <= today && today <= to;
        var previousTo = !partial
            ? from.AddDays(-1)
            : scale == HoursScale.Day ? today.AddMonths(-1) : today.AddYears(-1);

        var month = previousFrom.Year == from.Year
            ? ReportCalendar.MonthName(previousFrom.Month)
            : ReportCalendar.Month(previousFrom);

        var label = (scale, partial) switch
        {
            (HoursScale.Day, true) => string.Create(CultureInfo.InvariantCulture, $"vs 1–{previousTo.Day} de {month}"),
            (HoursScale.Day, false) => $"vs {month}",
            (_, true) => string.Create(CultureInfo.InvariantCulture, $"vs {previousFrom.Year} até {previousTo:dd/MM}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"vs {previousFrom.Year}"),
        };

        return (previousFrom, previousTo, label);
    }

    private static int BucketCount(HoursScale scale, DateOnly from, DateOnly to) => scale switch
    {
        HoursScale.Day => to.DayNumber - from.DayNumber + 1,
        HoursScale.Month => 12,
        _ => to.Year - from.Year + 1,
    };

    private static int BucketOf(HoursScale scale, DateOnly from, DateOnly date) => scale switch
    {
        HoursScale.Day => date.DayNumber - from.DayNumber,
        HoursScale.Month => date.Month - 1,
        _ => date.Year - from.Year,
    };

    private static HoursBucket Day(DateOnly date, TimeSpan total, int count, DateOnly today)
    {
        var future = date > today;
        var summary = future
            ? ReportCalendar.ShortDay(date)
            : $"{ReportCalendar.ShortDay(date)} · {Amount(total, count)}";

        return new HoursBucket(
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            date,
            date,
            date.Day.ToString(CultureInfo.InvariantCulture),
            ReportCalendar.LongDay(date),
            summary,
            total,
            count,
            total > TimeSpan.Zero ? 1 : 0,
            date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
            date == today,
            future,
            HoursLevels.Of(total));
    }

    private static HoursBucket Month(DateOnly first, IReadOnlyList<HoursBucket> days, int count, DateOnly today)
    {
        var last = first.AddMonths(1).AddDays(-1);

        return Rollup(
            first.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            first,
            last,
            ReportCalendar.MonthAbbreviation(first.Month),
            ReportCalendar.Month(first),
            days.Where(day => day.From.Month == first.Month).ToList(),
            count,
            today);
    }

    private static HoursBucket Year(int year, IReadOnlyList<HoursBucket> days, int count, DateOnly today)
    {
        var label = year.ToString(CultureInfo.InvariantCulture);

        return Rollup(
            label,
            new DateOnly(year, 1, 1),
            new DateOnly(year, 12, 31),
            label,
            label,
            days.Where(day => day.From.Year == year).ToList(),
            count,
            today);
    }

    private static HoursBucket Rollup(
        string key,
        DateOnly from,
        DateOnly to,
        string label,
        string longLabel,
        IReadOnlyList<HoursBucket> days,
        int count,
        DateOnly today)
    {
        var total = days.Aggregate(TimeSpan.Zero, (sum, day) => sum + day.Total);
        var active = days.Count(day => day.Total > TimeSpan.Zero);
        var future = from > today;
        var summary = future
            ? longLabel
            : total <= TimeSpan.Zero
                ? $"{longLabel} · sem horas"
                : $"{longLabel} · {WorkTimeFormatter.Duration(total)} · {ReportCalendar.Days(active)} · {ReportCalendar.Entries(count)}";

        return new HoursBucket(
            key,
            from,
            to,
            label,
            longLabel,
            summary,
            total,
            count,
            active,
            IsWeekend: false,
            IsCurrent: from <= today && today <= to,
            future,
            Level: 0);
    }

    private static string Amount(TimeSpan total, int count) =>
        total <= TimeSpan.Zero ? "sem horas" : $"{WorkTimeFormatter.Duration(total)} · {ReportCalendar.Entries(count)}";

    private static string Title(HoursScale scale, DateOnly from) => scale switch
    {
        HoursScale.Day => ReportCalendar.Month(from),
        HoursScale.Month => from.Year.ToString(CultureInfo.InvariantCulture),
        _ => "Todos os anos",
    };

    private static DateOnly? Previous(HoursScale scale, DateOnly from)
    {
        DateOnly? previous = scale switch
        {
            HoursScale.Day => from.AddMonths(-1),
            HoursScale.Month => from.AddYears(-1),
            _ => null,
        };

        return previous?.Year >= GetHoursHistory.FirstYear ? previous : null;
    }

    private static DateOnly? Next(HoursScale scale, DateOnly from, DateOnly today)
    {
        DateOnly? next = scale switch
        {
            HoursScale.Day => from.AddMonths(1),
            HoursScale.Month => from.AddYears(1),
            _ => null,
        };

        return next <= today ? next : null;
    }

    private static double? Change(TimeSpan current, TimeSpan previous) =>
        previous <= TimeSpan.Zero ? null : (current - previous) / previous;

    /// <summary>O que uma tarefa somou no período.</summary>
    private sealed class TaskHours(string title)
    {
        public string Title { get; } = title;

        public TimeSpan Total { get; set; }

        public List<Guid> Ids { get; } = [];
    }
}
