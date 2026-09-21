using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tasks;

/// <summary>
/// Registra "checklist concluído" e "checklist reaberto" (§8) a partir da
/// <b>mudança</b> do estado da série, e não do clique que a causou.
/// </summary>
/// <remarks>
/// A distinção importa: marcar uma ocorrência de uma série que ainda tem outras
/// pendentes não conclui o checklist, e gravar "concluído" ali encheria a
/// trilha de eventos que não aconteceram. Comparar antes e depois é também o
/// que faz cancelar a última pendente — que conclui o checklist sem ninguém ter
/// marcado nada — aparecer corretamente na auditoria.
/// </remarks>
internal static class ChecklistConclusionAudit
{
    public static Task RecordIfChangedAsync(
        ITaskAuditLog audit,
        TaskItem task,
        bool wasConcluded,
        DateTimeOffset at,
        string? userName,
        CancellationToken cancellationToken)
    {
        var isConcluded = task.ConcludedAt is not null;

        if (isConcluded == wasConcluded)
        {
            return Task.CompletedTask;
        }

        return audit.RecordAsync(
            TaskAuditEntry.ByUser(
                task.Id,
                task.Title,
                isConcluded ? TaskAuditOperation.Completed : TaskAuditOperation.Reopened,
                at,
                userName),
            cancellationToken);
    }
}
