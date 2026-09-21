using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Lifecycle;

/// <summary>Qual área está sendo consultada.</summary>
public enum ChecklistScope
{
    Archived = 0,
    Trashed = 1,
}

/// <summary>
/// Uma linha das áreas de arquivados/lixeira. DTO de leitura: não passa pelo
/// agregado (ADR-005), e já traz o resumo dos itens para a tela não precisar de
/// uma segunda consulta por linha.
/// </summary>
public sealed record ChecklistSummaryRow(
    Guid TaskId,
    string Title,
    string? Description,
    TaskPriority Priority,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConcludedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset? DeletedAt,
    string? DeletedBy,
    int TotalItems,
    int CompletedItems)
{
    public TaskLifecycle Lifecycle =>
        DeletedAt is not null ? TaskLifecycle.Trashed
        : ArchivedAt is not null ? TaskLifecycle.Archived
        : ConcludedAt is not null ? TaskLifecycle.Completed
        : TaskLifecycle.Active;
}

public interface IChecklistArchiveQuery
{
    /// <summary>
    /// Os checklists de uma área, do mais recente para o mais antigo.
    /// <paramref name="search"/> vazio traz tudo; caso contrário filtra por
    /// título e descrição, sem diferenciar maiúsculas.
    /// </summary>
    /// <param name="since">
    /// Recorte por período (§3). Compara com a data que define a área — quando
    /// foi arquivado, ou quando foi excluído —, e não com a criação: numa lista
    /// de arquivados, "últimos 30 dias" quer dizer "arquivados nos últimos 30
    /// dias". <c>null</c> não recorta nada.
    /// </param>
    Task<IReadOnlyList<ChecklistSummaryRow>> SearchAsync(
        ChecklistScope scope,
        string? search,
        DateTimeOffset? since = null,
        CancellationToken cancellationToken = default);
}
