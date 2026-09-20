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

    void Remove(TaskItem task);
}
