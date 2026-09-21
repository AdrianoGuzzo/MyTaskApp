using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Lifecycle;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

/// <summary>
/// As duas consultas da varredura automática. Os predicados são exatamente os
/// filtros dos índices parciais <c>IX_Tasks_ReadyToArchive</c> e
/// <c>IX_Tasks_Trashed</c>, para que o tique seja uma varredura de índice e não
/// da tabela.
/// </summary>
internal sealed class LifecycleSweepQuery(MyTaskAppDbContext context) : ILifecycleSweepQuery
{
    public async Task<IReadOnlyList<Guid>> GetReadyToArchiveAsync(
        DateTimeOffset concludedBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        return await context.Tasks
            .AsNoTracking()
            .Where(task =>
                task.ConcludedAt != null
                && task.ConcludedAt <= concludedBefore
                && task.ArchivedAt == null
                && task.DeletedAt == null)
            // Mais antigo primeiro: o que passou do prazo há mais tempo sai na
            // frente quando o lote não cabe todo num tique.
            .OrderBy(task => task.ConcludedAt)
            .Take(limit)
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetReadyToPurgeAsync(
        DateTimeOffset deletedBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        return await context.Tasks
            .AsNoTracking()
            .Where(task => task.DeletedAt != null && task.DeletedAt <= deletedBefore)
            .OrderBy(task => task.DeletedAt)
            .Take(limit)
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);
    }
}
