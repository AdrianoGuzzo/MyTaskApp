using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Abstractions;

internal static class TaskItemRepositoryExtensions
{
    /// <summary>
    /// Carrega o agregado dono da ocorrência, ou falha com mensagem que a UI pode
    /// exibir. Evita repetir o mesmo null-check em cada caso de uso.
    /// </summary>
    public static async Task<TaskItem> GetByOccurrenceIdAsync(
        this ITaskItemRepository tasks,
        Guid occurrenceId,
        CancellationToken cancellationToken) =>
        await tasks.FindByOccurrenceIdAsync(occurrenceId, cancellationToken)
        ?? throw new DomainException("Ocorrência não encontrada.");

    /// <summary>Carrega a tarefa por id, ou falha com mensagem exibível.</summary>
    public static async Task<TaskItem> GetByIdAsync(
        this ITaskItemRepository tasks,
        Guid taskId,
        CancellationToken cancellationToken) =>
        await tasks.FindByIdAsync(taskId, cancellationToken)
        ?? throw new DomainException("Tarefa não encontrada.");
}
