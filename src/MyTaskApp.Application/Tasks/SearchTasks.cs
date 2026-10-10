using Microsoft.Extensions.Options;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Planning;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tasks;

/// <summary>Como ordenar o resultado da busca.</summary>
public enum TaskSort
{
    /// <summary>Mais novas primeiro.</summary>
    Created = 0,

    /// <summary>Pela data marcada; sem data por último.</summary>
    Scheduled = 1,

    /// <summary>Pelo prazo; sem prazo por último.</summary>
    Deadline = 2,

    /// <summary>Mais urgentes primeiro.</summary>
    Priority = 3,

    Title = 4,
}

/// <summary>
/// Buscar tarefas por qualquer combinação de filtros (ADR-059). O quadro Hoje
/// responde "o que faço agora"; isto responde "quais tarefas existem" — inclusive
/// as pendentes sem data, que nenhuma tela lista, e as arquivadas e na lixeira
/// quando pedidas.
/// </summary>
/// <param name="Lifecycles">
/// <c>null</c> = a lista principal: ativas e concluídas, sem arquivo nem lixeira.
/// </param>
/// <param name="TagIds">Qualquer uma delas basta.</param>
/// <param name="ExternalKey">A chave da issue (<c>ECO-123</c>) ou o prefixo do projeto (<c>ECO</c>).</param>
/// <param name="OverdueOnly">Só as atrasadas: o mesmo critério do quadro Hoje.</param>
public sealed record SearchTasks(
    string? Text = null,
    IReadOnlyCollection<TaskLifecycle>? Lifecycles = null,
    IReadOnlyCollection<TaskItemStatus>? Statuses = null,
    IReadOnlyCollection<Guid>? TagIds = null,
    IReadOnlyCollection<TaskPriority>? Priorities = null,
    DateOnly? ScheduledFrom = null,
    DateOnly? ScheduledTo = null,
    bool? HasSchedule = null,
    DateOnly? DeadlineFrom = null,
    DateOnly? DeadlineTo = null,
    bool? HasDeadline = null,
    string? ExternalKey = null,
    bool OverdueOnly = false,
    TaskSort Sort = TaskSort.Created,
    int Limit = SearchTasks.DefaultLimit,
    int Offset = 0,
    IReadOnlyCollection<Guid>? TaskIds = null)
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;
}

/// <summary>Uma tarefa na busca: a linha do quadro, mais o que só o ciclo de vida sabe.</summary>
public sealed record TaskListItem(
    TodayTask Task,
    TaskItemStatus Status,
    TaskLifecycle Lifecycle,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ConcludedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset? DeletedAt,
    string? DeletedBy,
    bool IsOverdue);

/// <param name="Total">Quantas casaram com os filtros, antes da página.</param>
/// <param name="Truncated">
/// A busca parou no teto de <see cref="TaskSearchCriteria.MaxRows"/>: o total
/// é "pelo menos isso". Filtros mais estreitos resolvem.
/// </param>
public sealed record TaskSearchResult(
    IReadOnlyList<TaskListItem> Items,
    int Total,
    int Offset,
    int Limit,
    bool Truncated);

/// <summary>Os filtros que o banco aplica; o resto (atraso exato, ordem, página) é daqui.</summary>
/// <param name="OverdueCandidatesOn">
/// Pendentes com data ou prazo até este dia: um superconjunto das atrasadas, para
/// o teto não cortar justamente elas.
/// </param>
public sealed record TaskSearchCriteria(
    IReadOnlyCollection<TaskLifecycle> Lifecycles,
    IReadOnlyCollection<Guid>? TaskIds = null,
    string? Text = null,
    IReadOnlyCollection<TaskItemStatus>? Statuses = null,
    IReadOnlyCollection<Guid>? TagIds = null,
    IReadOnlyCollection<TaskPriority>? Priorities = null,
    DateOnly? ScheduledFrom = null,
    DateOnly? ScheduledTo = null,
    bool? HasSchedule = null,
    DateOnly? DeadlineFrom = null,
    DateOnly? DeadlineTo = null,
    bool? HasDeadline = null,
    string? ExternalKey = null,
    DateOnly? OverdueCandidatesOn = null)
{
    /// <summary>Teto do que a consulta traz: um app de uma pessoa, e a página vem depois.</summary>
    public const int MaxRows = 2000;
}

/// <summary>A linha do quadro e as datas do ciclo de vida da tarefa.</summary>
public sealed record TaskSearchRow(
    TodayOccurrenceRow Row,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConcludedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset? DeletedAt,
    string? DeletedBy);

/// <summary>O que basta para contar: uma linha por tarefa, sem anotação nem etiqueta.</summary>
public sealed record TaskCountRow(
    Guid TaskId,
    Guid OccurrenceId,
    TaskPriority Priority,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConcludedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset? DeletedAt,
    TaskItemStatus Status,
    DateOnly? ScheduledDate,
    TimeOnly? ScheduledTime,
    DateTimeOffset? CompletedAt,
    DateOnly? DeadlineDate,
    TimeOnly? DeadlineTime);

public interface ITaskSearchQuery
{
    /// <summary>
    /// As tarefas que casam com os filtros, mais novas primeiro, até
    /// <see cref="TaskSearchCriteria.MaxRows"/> + 1 — o excedente diz que cortou.
    /// </summary>
    Task<IReadOnlyList<TaskSearchRow>> SearchAsync(
        TaskSearchCriteria criteria,
        CancellationToken cancellationToken = default);

    /// <summary>Todas as tarefas, em linha curta, para as estatísticas.</summary>
    Task<IReadOnlyList<TaskCountRow>> ListForCountingAsync(CancellationToken cancellationToken = default);
}

public sealed class SearchTasksHandler(
    ITaskSearchQuery query,
    GetTodayBoardHandler board,
    IUserClock clock,
    TimeProvider timeProvider,
    IOptions<ApplicationOptions> options)
{
    /// <summary>A lista principal: o que a tela mostra fora de Arquivados e Lixeira.</summary>
    public static readonly IReadOnlyCollection<TaskLifecycle> MainList =
        [TaskLifecycle.Active, TaskLifecycle.Completed];

    public static readonly IReadOnlyCollection<TaskLifecycle> Everything =
        [TaskLifecycle.Active, TaskLifecycle.Completed, TaskLifecycle.Archived, TaskLifecycle.Trashed];

    public async Task<TaskSearchResult> HandleAsync(
        SearchTasks request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        var lifecycles = request.Lifecycles is { Count: > 0 } chosen ? chosen : MainList;

        var rows = await query.SearchAsync(
            new TaskSearchCriteria(
                lifecycles,
                request.TaskIds,
                request.Text,
                request.Statuses,
                request.TagIds,
                request.Priorities,
                request.ScheduledFrom,
                request.ScheduledTo,
                request.HasSchedule,
                request.DeadlineFrom,
                request.DeadlineTo,
                request.HasDeadline,
                request.ExternalKey,
                // Só as que podem estar atrasadas chegam ao teto: o critério exato
                // é do classificador, aqui embaixo, mas as 2000 mais novas não
                // podem esconder a atrasada mais antiga.
                request.OverdueOnly ? clock.Today : null),
            cancellationToken);

        var truncated = rows.Count > TaskSearchCriteria.MaxRows;
        var items = rows.Take(TaskSearchCriteria.MaxRows).Select(Describe).ToList();

        if (request.OverdueOnly)
        {
            items = items.Where(item => item.IsOverdue).ToList();
        }

        var sorted = Sort(items, request.Sort).ToList();

        return new TaskSearchResult(
            sorted.Skip(request.Offset).Take(request.Limit).ToList(),
            sorted.Count,
            request.Offset,
            request.Limit,
            truncated);
    }

    /// <summary>Uma tarefa pelo id, em qualquer estado — inclusive arquivo e lixeira.</summary>
    internal async Task<TaskListItem?> FindAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var rows = await query.SearchAsync(
            new TaskSearchCriteria(Everything, TaskIds: [taskId]),
            cancellationToken);

        return rows.Count == 0 ? null : Describe(rows[0]);
    }

    /// <summary>Atrasada pelo mesmo critério do quadro: seção ATRASADAS, ou a hora de hoje já passou.</summary>
    internal bool IsOverdue(TodayOccurrenceRow row)
    {
        if (row.Status is not TaskItemStatus.Pending)
        {
            return false;
        }

        return board.Place(row, clock.Today, clock.CurrentTime, options.Value.ToNowWindow()) is
            { Section: TodaySection.Overdue } or { IsLate: true };
    }

    private TaskListItem Describe(TaskSearchRow found)
    {
        var row = found.Row;
        var nowUtc = timeProvider.GetUtcNow();
        var isCompleted = row.Status is TaskItemStatus.Completed;

        // Fora do quadro não há seção; a linha sai como a da tarefa avulsa (ADR-053).
        var placement = board.Place(row, clock.Today, clock.CurrentTime, options.Value.ToNowWindow())
            ?? new TodayPlacement(isCompleted ? TodaySection.Completed : TodaySection.Today, IsLate: false);

        var lifecycle = found.DeletedAt is not null ? TaskLifecycle.Trashed
            : found.ArchivedAt is not null ? TaskLifecycle.Archived
            : found.ConcludedAt is not null ? TaskLifecycle.Completed
            : TaskLifecycle.Active;

        return new TaskListItem(
            board.Describe(row, placement, nowUtc),
            row.Status,
            lifecycle,
            found.CreatedAt,
            row.CompletedAt,
            found.ConcludedAt,
            found.ArchivedAt,
            found.DeletedAt,
            found.DeletedBy,
            // Arquivada ou na lixeira não cobra nada de ninguém.
            lifecycle is TaskLifecycle.Active or TaskLifecycle.Completed && IsOverdue(row));
    }

    private static IEnumerable<TaskListItem> Sort(IEnumerable<TaskListItem> items, TaskSort sort) => sort switch
    {
        TaskSort.Scheduled => items
            .OrderBy(item => item.Task.ScheduledDate is null)
            .ThenBy(item => item.Task.ScheduledDate)
            .ThenBy(item => item.Task.ScheduledTime ?? TimeOnly.MinValue)
            .ThenByDescending(item => item.CreatedAt),
        TaskSort.Deadline => items
            .OrderBy(item => item.Task.Deadline is null)
            .ThenBy(item => item.Task.Deadline?.Deadline.Date)
            .ThenBy(item => item.Task.Deadline?.Deadline.Time)
            .ThenByDescending(item => item.CreatedAt),
        TaskSort.Priority => items
            .OrderByDescending(item => item.Task.Priority)
            .ThenByDescending(item => item.CreatedAt),
        TaskSort.Title => items
            .OrderBy(item => item.Task.Title, StringComparer.CurrentCultureIgnoreCase),
        _ => items.OrderByDescending(item => item.CreatedAt),
    };

    private static void Validate(SearchTasks request)
    {
        if (request.Limit is < 1 or > SearchTasks.MaxLimit)
        {
            throw new DomainException($"O limite da busca fica entre 1 e {SearchTasks.MaxLimit}.");
        }

        if (request.Offset < 0)
        {
            throw new DomainException("O deslocamento da busca não pode ser negativo.");
        }

        if (request.ScheduledFrom > request.ScheduledTo)
        {
            throw new DomainException("A data inicial do agendamento é depois da final.");
        }

        if (request.DeadlineFrom > request.DeadlineTo)
        {
            throw new DomainException("A data inicial do prazo é depois da final.");
        }

        if (request.Text is { Length: > 200 })
        {
            throw new DomainException("O texto da busca passa de 200 caracteres.");
        }

        // Filtro inválido recusado, e não ignorado: ignorar devolveria tudo como se tivesse casado.
        if (request.ExternalKey is { } key
            && (string.IsNullOrWhiteSpace(key) || key.Trim().Length > 50
                || !key.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            throw new DomainException("A chave da issue usa só letras, números e hífen, como ECO-123 ou ECO.");
        }
    }
}

/// <summary>Uma tarefa por id, com tudo o que a busca sabe dela.</summary>
public sealed record GetTaskDetails(Guid TaskId);

public sealed class GetTaskDetailsHandler(SearchTasksHandler search)
{
    public async Task<TaskListItem> HandleAsync(
        GetTaskDetails request,
        CancellationToken cancellationToken = default) =>
        await search.FindAsync(request.TaskId, cancellationToken)
            ?? throw new DomainException("Tarefa não encontrada.");
}
