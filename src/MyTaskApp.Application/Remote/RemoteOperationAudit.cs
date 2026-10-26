using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>Uma operação de outra máquina que terminou aqui, para a trilha da tarefa (T-21).</summary>
public sealed record RecordRemoteOperation(
    Guid TaskId,
    string Kind,
    string ControllerNickname,
    RemoteOperationStatus Status,
    string? Summary);

/// <summary>
/// A linha da trilha da tarefa (§8) de cada operação remota que terminou
/// nesta máquina: o que foi pedido, por quem ("Remoto: Pixel 8") e como
/// acabou — inclusive a recusa. O que ela mudou na tarefa já tem a linha
/// própria, com a mesma origem.
/// </summary>
public sealed class RecordRemoteOperationHandler(
    ITaskItemRepository tasks,
    ITaskAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <returns><c>false</c> se a tarefa não está aqui (ainda não chegou, ou foi excluída).</returns>
    public async Task<bool> HandleAsync(RecordRemoteOperation command, CancellationToken cancellationToken = default)
    {
        if (await tasks.FindByIdAsync(command.TaskId, cancellationToken) is not { } task)
        {
            return false;
        }

        await audit.RecordAsync(
            TaskAuditEntry.ByUser(
                task.Id,
                task.Title,
                TaskAuditOperation.RemoteOperation,
                timeProvider.GetUtcNow(),
                $"Remoto: {command.ControllerNickname}",
                Describe(command.Kind, command.Status, command.Summary)),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>"Rodar comando: terminou bem — "Testes" terminou bem."</summary>
    public static string Describe(string kind, RemoteOperationStatus status, string? summary)
    {
        var line = $"{KindName(kind)}: {Outcome(status)}";
        return string.IsNullOrWhiteSpace(summary) ? line + "." : $"{line} — {summary}";
    }

    public static string KindName(string kind) => kind switch
    {
        RemoteOperationKinds.EnvironmentInspect => "Ver o ambiente",
        RemoteOperationKinds.CommandRun => "Rodar comando",
        RemoteOperationKinds.WorktreeProvision => "Preparar o ambiente",
        RemoteOperationKinds.WorktreeRemove => "Apagar o worktree",
        RemoteOperationKinds.AgentStart => "Abrir o Claude",
        RemoteOperationKinds.AgentPrompt => "Prompt ao Claude",
        RemoteOperationKinds.AgentStop => "Parar o Claude",
        RemoteOperationKinds.DatabaseCopy => "Copiar o banco",
        _ => kind,
    };

    private static string Outcome(RemoteOperationStatus status) => status switch
    {
        RemoteOperationStatus.Succeeded => "terminou bem",
        RemoteOperationStatus.Failed => "falhou",
        RemoteOperationStatus.Canceled => "cancelada por quem pediu",
        RemoteOperationStatus.Interrupted => "interrompida nesta máquina",
        RemoteOperationStatus.Rejected => "recusada nesta máquina",
        RemoteOperationStatus.Expired => "expirou antes de começar",
        _ => status.ToString(),
    };
}
