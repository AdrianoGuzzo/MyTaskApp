using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class CommandExecutionRepository(MyTaskAppDbContext context) : ICommandExecutionRepository
{
    public async Task AddAsync(CommandExecution execution, CancellationToken cancellationToken = default) =>
        await context.CommandExecutions.AddAsync(execution, cancellationToken);

    public Task<CommandExecution?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.CommandExecutions.SingleOrDefaultAsync(execution => execution.Id == id, cancellationToken);

    public async Task<IReadOnlyList<CommandExecution>> ListActiveAsync(CancellationToken cancellationToken = default) =>
        await context.CommandExecutions
            .Where(execution => execution.Status == CommandExecutionStatus.Queued
                                || execution.Status == CommandExecutionStatus.Running)
            .OrderBy(execution => execution.StartedAt)
            .ToListAsync(cancellationToken);

    /// <remarks>O id é v7: desempata duas execuções no mesmo tique, na ordem em que nasceram.</remarks>
    public async Task<IReadOnlyList<CommandExecution>> ListForDevelopmentAsync(
        Guid developmentId,
        CancellationToken cancellationToken = default) =>
        await context.CommandExecutions
            .Where(execution => execution.TaskDevelopmentId == developmentId)
            .OrderByDescending(execution => execution.StartedAt)
            .ThenByDescending(execution => execution.Id)
            .ToListAsync(cancellationToken);

    public void Remove(CommandExecution execution) => context.CommandExecutions.Remove(execution);
}
