using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Abstractions;

/// <summary>
/// Acesso ao agregado <see cref="TaskItem"/>. Interface estreita e por
/// necessidade — sem IRepository&lt;T&gt; genérico (ADR-005).
/// </summary>
public interface ITaskItemRepository
{
    Task AddAsync(TaskItem task, CancellationToken cancellationToken = default);

    Task<TaskItem?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Carrega o agregado dono da ocorrência informada.</summary>
    Task<TaskItem?> FindByOccurrenceIdAsync(
        Guid occurrenceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Os agregados donos das ocorrências informadas, numa consulta só.
    /// </summary>
    /// <remarks>
    /// Reordenar uma seção toca dezenas de checklists diferentes de uma vez
    /// (ADR-022) — cada linha da lista é um <see cref="TaskItem"/> próprio, e
    /// repetir <see cref="FindByOccurrenceIdAsync"/> seria uma ida ao banco por
    /// linha a cada arrasto. Interface estreita por necessidade, como manda o
    /// ADR-005; não é um passo em direção a um repositório genérico.
    /// </remarks>
    Task<IReadOnlyList<TaskItem>> FindByOccurrenceIdsAsync(
        IReadOnlyCollection<Guid> occurrenceIds,
        CancellationToken cancellationToken = default);

    void Remove(TaskItem task);
}
