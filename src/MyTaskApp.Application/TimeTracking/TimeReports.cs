using MyTaskApp.Application.Planning;
using MyTaskApp.Domain;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.TimeTracking;

/// <summary>
/// Os períodos gravados como relatório (ADR-059): por intervalo, tarefa,
/// etiqueta, issue e origem. A aba "Tempo" mostra uma tarefa; isto responde
/// "quanto trabalhei esta semana, e em quê" — sempre a partir do que está no
/// banco, e com o id de cada período que entrou em cada total.
/// </summary>
/// <param name="From">Dia local inicial, inclusive.</param>
/// <param name="To">Dia local final, inclusive.</param>
/// <param name="Running"><c>true</c> = só o que corre; <c>false</c> = só os encerrados.</param>
/// <param name="IncludeTrashed">A lixeira fica de fora, como no histórico (ADR-053).</param>
public sealed record GetTimeEntries(
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? TaskId = null,
    Guid? TagId = null,
    string? ExternalKey = null,
    TimeEntrySource? Source = null,
    bool? Running = null,
    bool IncludeTrashed = false,
    int Limit = GetTimeEntries.DefaultLimit,
    int Offset = 0)
{
    public const int DefaultLimit = 100;

    public const int MaxLimit = 500;
}

/// <summary>
/// Um período, com as horas locais e o recorte no intervalo pedido.
/// </summary>
/// <param name="Duration">O período inteiro; o que corre conta até agora.</param>
/// <param name="DurationInRange">Só a parte dentro do intervalo pedido — é o que entra no total.</param>
/// <param name="IsLong">Mais de <see cref="TimeReport.LongEntry"/> seguidas: vale conferir se não foi esquecido ligado.</param>
public sealed record TimeEntryReportItem(
    Guid Id,
    Guid TaskId,
    Guid OccurrenceId,
    string TaskTitle,
    string? ExternalKey,
    IReadOnlyList<TagBadge> Tags,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateOnly StartDate,
    TimeOnly StartTime,
    DateOnly? EndDate,
    TimeOnly? EndTime,
    TimeEntrySource Source,
    string? Note,
    TimeSpan Duration,
    TimeSpan DurationInRange,
    bool IsRunning,
    bool CrossesMidnight,
    bool IsLong,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    bool TaskArchived,
    bool TaskInTrash);

/// <param name="Count">Quantos períodos casaram, antes da página.</param>
/// <param name="Total">A soma de <see cref="TimeEntryReportItem.DurationInRange"/> de todos eles, não só da página.</param>
public sealed record TimeEntryReport(
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<TimeEntryReportItem> Entries,
    int Count,
    TimeSpan Total,
    int Offset,
    int Limit);

public enum TimeGrouping
{
    Day = 0,
    Task = 1,
    Tag = 2,
    External = 3,
    Source = 4,
}

/// <summary>Os totais de um intervalo, agrupados.</summary>
/// <param name="IncludeRunning">O período que corre conta até agora; desligado, só os encerrados.</param>
public sealed record GetTimeSummary(
    DateOnly From,
    DateOnly To,
    TimeGrouping GroupBy = TimeGrouping.Day,
    Guid? TaskId = null,
    Guid? TagId = null,
    string? ExternalKey = null,
    TimeEntrySource? Source = null,
    bool IncludeRunning = true,
    bool IncludeTrashed = false);

/// <param name="EntryIds">Os períodos que somam este total — de onde ele veio.</param>
public sealed record TimeSummaryGroup(
    string Key,
    string Label,
    TimeSpan Total,
    int EntryCount,
    IReadOnlyList<Guid> EntryIds);

/// <param name="Total">
/// A soma do intervalo, cada período contado uma vez. Agrupado por etiqueta, um
/// período de tarefa com duas etiquetas entra nas duas: a soma dos grupos pode
/// passar do total, e o total é este.
/// </param>
public sealed record TimeSummary(
    DateOnly From,
    DateOnly To,
    TimeGrouping GroupBy,
    TimeSpan Total,
    int EntryCount,
    IReadOnlyList<TimeSummaryGroup> Groups);

/// <summary>Um período pelo id.</summary>
public sealed record GetTimeEntry(Guid EntryId);

/// <summary>Os filtros que o banco aplica.</summary>
public sealed record TimeEntryReportCriteria(
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    Guid? TaskId = null,
    Guid? EntryId = null,
    Guid? TagId = null,
    string? ExternalKey = null,
    TimeEntrySource? Source = null,
    bool? Running = null,
    bool IncludeTrashed = false);

public sealed record TimeEntryReportRow(
    Guid EntryId,
    Guid OccurrenceId,
    Guid TaskId,
    string TaskTitle,
    string? ExternalKey,
    IReadOnlyList<TagBadge> Tags,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    TimeEntrySource Source,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    bool TaskArchived,
    bool TaskInTrash);

public interface ITimeEntryReportQuery
{
    /// <summary>
    /// Os períodos que tocam o intervalo — começaram antes do fim e não
    /// terminaram antes do início —, do mais recente ao mais antigo.
    /// </summary>
    Task<IReadOnlyList<TimeEntryReportRow>> ListAsync(
        TimeEntryReportCriteria criteria,
        CancellationToken cancellationToken = default);
}

public sealed class TimeReportHandlers(
    ITimeEntryReportQuery query,
    IUserClock clock,
    TimeProvider timeProvider)
{
    public async Task<TimeEntryReport> HandleAsync(GetTimeEntries request, CancellationToken cancellationToken = default)
    {
        if (request.Limit is < 1 or > GetTimeEntries.MaxLimit)
        {
            throw new DomainException($"O limite fica entre 1 e {GetTimeEntries.MaxLimit} períodos.");
        }

        if (request.Offset < 0)
        {
            throw new DomainException("O deslocamento não pode ser negativo.");
        }

        var window = Window(request.From, request.To);
        var now = timeProvider.GetUtcNow();

        var rows = await query.ListAsync(
            new TimeEntryReportCriteria(
                window.Since,
                window.Until,
                request.TaskId,
                TagId: request.TagId,
                ExternalKey: TimeReport.NormalizeKey(request.ExternalKey),
                Source: request.Source,
                Running: request.Running,
                IncludeTrashed: request.IncludeTrashed),
            cancellationToken);

        var items = rows.Select(row => Describe(row, window, now)).ToList();

        return new TimeEntryReport(
            request.From,
            request.To,
            items.Skip(request.Offset).Take(request.Limit).ToList(),
            items.Count,
            items.Aggregate(TimeSpan.Zero, (total, item) => total + item.DurationInRange),
            request.Offset,
            request.Limit);
    }

    public async Task<TimeEntryReportItem> HandleAsync(GetTimeEntry request, CancellationToken cancellationToken = default)
    {
        var rows = await query.ListAsync(
            new TimeEntryReportCriteria(EntryId: request.EntryId, IncludeTrashed: true),
            cancellationToken);

        return rows.Count == 0
            ? throw new DomainException("Período não encontrado.")
            : Describe(rows[0], (null, null), timeProvider.GetUtcNow());
    }

    public async Task<TimeSummary> HandleAsync(GetTimeSummary request, CancellationToken cancellationToken = default)
    {
        var window = Window(request.From, request.To);

        if (request.To.DayNumber - request.From.DayNumber >= TimeReport.MaxSummaryDays)
        {
            throw new DomainException($"O resumo cobre no máximo {TimeReport.MaxSummaryDays} dias.");
        }

        var now = timeProvider.GetUtcNow();

        var rows = await query.ListAsync(
            new TimeEntryReportCriteria(
                window.Since,
                window.Until,
                request.TaskId,
                TagId: request.TagId,
                ExternalKey: TimeReport.NormalizeKey(request.ExternalKey),
                Source: request.Source,
                Running: request.IncludeRunning ? null : false,
                IncludeTrashed: request.IncludeTrashed),
            cancellationToken);

        var items = rows.Select(row => Describe(row, window, now)).Where(item => item.DurationInRange > TimeSpan.Zero).ToList();

        var groups = request.GroupBy switch
        {
            TimeGrouping.Day => ByDay(items, request.From, request.To, now),
            TimeGrouping.Task => Group(items, item => [(item.TaskId.ToString(), item.TaskTitle)]),
            TimeGrouping.Tag => Group(items, item => item.Tags.Count == 0
                ? [(TimeReport.NoneKey, "Sem etiqueta")]
                : item.Tags.Select(tag => (tag.Id.ToString(), tag.Name)).ToArray()),
            TimeGrouping.External => Group(items, item => item.ExternalKey is { } key
                ? [(key, key)]
                : [(TimeReport.NoneKey, "Sem issue")]),
            _ => Group(items, item => [(item.Source.ToString(), WorkTimeFormatter.Source(item.Source))]),
        };

        return new TimeSummary(
            request.From,
            request.To,
            request.GroupBy,
            items.Aggregate(TimeSpan.Zero, (total, item) => total + item.DurationInRange),
            items.Count,
            groups);
    }

    /// <summary>Um dia por grupo, na data do usuário: o período que vira a noite conta em cada dia o que coube nele.</summary>
    private IReadOnlyList<TimeSummaryGroup> ByDay(
        IReadOnlyList<TimeEntryReportItem> items,
        DateOnly from,
        DateOnly to,
        DateTimeOffset now)
    {
        var groups = new List<TimeSummaryGroup>();

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var start = clock.ToInstant(day, TimeOnly.MinValue);
            var end = clock.ToInstant(day.AddDays(1), TimeOnly.MinValue);

            var inDay = items
                .Select(item => (item.Id, Part: Overlap(item.StartedAt, item.EndedAt ?? now, start, end)))
                .Where(part => part.Part > TimeSpan.Zero)
                .ToList();

            if (inDay.Count > 0)
            {
                groups.Add(new TimeSummaryGroup(
                    day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    day.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture),
                    inDay.Aggregate(TimeSpan.Zero, (total, part) => total + part.Part),
                    inDay.Count,
                    inDay.Select(part => part.Id).ToList()));
            }
        }

        return groups;
    }

    private static IReadOnlyList<TimeSummaryGroup> Group(
        IReadOnlyList<TimeEntryReportItem> items,
        Func<TimeEntryReportItem, (string Key, string Label)[]> keys) =>
        items
            .SelectMany(item => keys(item).Select(key => (key.Key, key.Label, Item: item)))
            .GroupBy(entry => entry.Key)
            .Select(group => new TimeSummaryGroup(
                group.Key,
                group.First().Label,
                group.Aggregate(TimeSpan.Zero, (total, entry) => total + entry.Item.DurationInRange),
                group.Count(),
                group.Select(entry => entry.Item.Id).ToList()))
            .OrderByDescending(group => group.Total)
            .ThenBy(group => group.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private (DateTimeOffset? Since, DateTimeOffset? Until) Window(DateOnly? from, DateOnly? to)
    {
        if (from > to)
        {
            throw new DomainException("A data inicial é depois da final.");
        }

        return (
            from is { } first ? clock.ToInstant(first, TimeOnly.MinValue) : null,
            to is { } last ? clock.ToInstant(last.AddDays(1), TimeOnly.MinValue) : null);
    }

    private TimeEntryReportItem Describe(
        TimeEntryReportRow row,
        (DateTimeOffset? Since, DateTimeOffset? Until) window,
        DateTimeOffset now)
    {
        var start = TimeEntryText.Local(row.StartedAt, clock);
        DateTime? end = row.EndedAt is { } ended ? TimeEntryText.Local(ended, clock) : null;
        var effectiveEnd = row.EndedAt ?? now;
        var duration = effectiveEnd > row.StartedAt ? effectiveEnd - row.StartedAt : TimeSpan.Zero;

        return new TimeEntryReportItem(
            row.EntryId,
            row.TaskId,
            row.OccurrenceId,
            row.TaskTitle,
            row.ExternalKey,
            row.Tags,
            row.StartedAt,
            row.EndedAt,
            DateOnly.FromDateTime(start),
            TimeOnly.FromDateTime(start),
            end is { } e ? DateOnly.FromDateTime(e) : null,
            end is { } f ? TimeOnly.FromDateTime(f) : null,
            row.Source,
            row.Note,
            duration,
            Overlap(row.StartedAt, effectiveEnd, window.Since ?? DateTimeOffset.MinValue, window.Until ?? DateTimeOffset.MaxValue),
            row.EndedAt is null,
            DateOnly.FromDateTime(start) != DateOnly.FromDateTime(end ?? TimeEntryText.Local(now, clock)),
            duration > TimeReport.LongEntry,
            row.CreatedAt,
            row.UpdatedAt,
            row.TaskArchived,
            row.TaskInTrash);
    }

    private static TimeSpan Overlap(DateTimeOffset start, DateTimeOffset end, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var from = start > windowStart ? start : windowStart;
        var until = end < windowEnd ? end : windowEnd;

        return until > from ? until - from : TimeSpan.Zero;
    }
}

public static class TimeReport
{
    /// <summary>Um período maior que isso costuma ser cronômetro esquecido ligado.</summary>
    public static readonly TimeSpan LongEntry = TimeSpan.FromHours(10);

    /// <summary>Um ano e um dia: o bastante para "o ano inteiro", e não o histórico todo de uma vez.</summary>
    public const int MaxSummaryDays = 366;

    /// <summary>O grupo do que não tem etiqueta ou issue.</summary>
    public const string NoneKey = "none";

    /// <summary>A chave da issue ou do projeto em maiúsculas, ou recusa — filtro inválido não vira "tudo".</summary>
    public static string? NormalizeKey(string? key)
    {
        if (key is null)
        {
            return null;
        }

        var trimmed = key.Trim();

        if (trimmed.Length is 0 or > 50 || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new DomainException("A chave da issue usa só letras, números e hífen, como ECO-123 ou ECO.");
        }

        return trimmed.ToUpperInvariant();
    }
}
