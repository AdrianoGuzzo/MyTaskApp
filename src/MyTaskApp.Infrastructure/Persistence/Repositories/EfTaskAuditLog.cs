using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class EfTaskAuditLog(MyTaskAppDbContext context) : ITaskAuditLog
{
    /// <summary>
    /// Só rastreia; quem grava é o <c>SaveChanges</c> do caso de uso. É isso que
    /// põe a linha de auditoria na mesma transação da operação auditada — e o
    /// que faz a auditoria da exclusão definitiva ser gravada no mesmo comando
    /// em que o checklist é removido.
    /// </summary>
    public async Task RecordAsync(
        TaskAuditEntry entry,
        CancellationToken cancellationToken = default) =>
        await context.TaskAudit.AddAsync(entry, cancellationToken);

    public async Task<IReadOnlyList<TaskAuditEntry>> GetForTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        await context.TaskAudit
            .AsNoTracking()
            .Where(entry => entry.TaskId == taskId)
            .OrderByDescending(entry => entry.OccurredAt)
            // Desempate pelo id, que é Version7 e portanto cresce com o tempo:
            // num mesmo tique da varredura várias linhas compartilham o instante,
            // e sem isto a ordem na tela mudaria entre duas aberturas.
            .ThenByDescending(entry => entry.Id)
            .ToListAsync(cancellationToken);
}
