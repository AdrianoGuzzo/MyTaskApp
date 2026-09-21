using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Application.Lifecycle;

/// <summary>
/// A trilha de um checklist (§8). Responde mesmo para um id que não existe
/// mais: é justamente esse o caso que a auditoria serve para investigar.
/// </summary>
public sealed record GetChecklistAudit(Guid TaskId);

public sealed class GetChecklistAuditHandler(ITaskAuditLog audit)
{
    public Task<IReadOnlyList<TaskAuditEntry>> HandleAsync(
        GetChecklistAudit command,
        CancellationToken cancellationToken = default) =>
        audit.GetForTaskAsync(command.TaskId, cancellationToken);
}
