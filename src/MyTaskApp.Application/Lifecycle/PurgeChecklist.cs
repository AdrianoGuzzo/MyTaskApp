using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Application.Lifecycle;

/// <summary>
/// Exclusão definitiva, pedida pelo usuário (§7). Substitui o antigo
/// <c>DeleteTask</c>, que removia o registro direto: um caso de uso chamado
/// "excluir" que apagava de vez era a única porta do app por onde se perdia
/// dado sem volta.
/// </summary>
public sealed record PurgeChecklist(Guid TaskId);

public sealed class PurgeChecklistHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ITaskAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<PurgeChecklistHandler> logger)
{
    public async Task HandleAsync(
        PurgeChecklist command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        // Nunca direto da lista ativa: a regra é do agregado, não da tela.
        task.EnsurePermanentDeletionIsAllowed();

        var now = timeProvider.GetUtcNow();

        // O registro é gravado com o título em mãos, antes de o checklist
        // deixar de existir. Como a tabela de auditoria não tem chave
        // estrangeira para Tasks, a linha sobrevive ao DELETE que vem a seguir —
        // é o §8 em uma decisão de esquema, não em uma ordem de chamadas.
        await audit.RecordAsync(
            TaskAuditEntry.ByUser(
                task.Id,
                task.Title,
                TaskAuditOperation.PermanentlyDeleted,
                now,
                currentUser.Name,
                DescribeOrigin(task.IsInTrash)),
            cancellationToken);

        tasks.Remove(task);

        // As ocorrências e o estado dos lembretes saem junto, por cascata já
        // configurada no mapeamento. Não há outra tabela apontando para Tasks,
        // então não sobra órfão — a auditoria não aponta de propósito.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogWarning("ChecklistPermanentlyDeleted {TaskId} {Title}", task.Id, task.Title);
    }

    private static string DescribeOrigin(bool fromTrash) =>
        fromTrash ? "Excluído da lixeira pelo usuário." : "Excluído do arquivo pelo usuário.";
}
