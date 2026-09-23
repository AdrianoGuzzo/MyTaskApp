using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Development;

/// <summary>O que a tela precisa saber antes de perguntar "remover?".</summary>
public sealed record WorktreeInspection(bool Exists, IReadOnlyList<string> Changes)
{
    public bool IsClean => Changes.Count == 0;
}

/// <summary>A pasta do worktree ainda existe? Tem alterações não commitadas?</summary>
public sealed record InspectWorktree(Guid TaskId);

public sealed class InspectWorktreeHandler(
    ITaskItemRepository tasks,
    IGitClient git,
    IDirectoryProbe directories)
{
    public async Task<WorktreeInspection> HandleAsync(
        InspectWorktree query,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(query.TaskId, cancellationToken);
        var development = task.Development
            ?? throw new DomainException("Esta tarefa não tem ambiente de desenvolvimento.");

        if (!await directories.ExistsAsync(development.WorktreePath, cancellationToken))
        {
            return new WorktreeInspection(false, []);
        }

        try
        {
            var status = await git.GetStatusAsync(development.WorktreePath, cancellationToken);
            return new WorktreeInspection(true, status.Entries);
        }
        catch (GitCommandFailedException exception)
        {
            throw new DevelopmentStepException(
                DevelopmentStep.RemoveWorktree,
                "Não foi possível verificar as alterações do worktree.",
                exception.Result);
        }
    }
}

/// <summary>
/// Remove o worktree da tarefa com <c>git worktree remove</c>, sem força. A
/// branch fica no repositório: apagar branch é decisão que o app não toma.
/// </summary>
public sealed record RemoveWorktree(Guid TaskId);

/// <summary>
/// Confere as alterações de novo, aqui dentro, mesmo que a tela já tenha
/// conferido: entre a pergunta e o clique o usuário pode ter editado um arquivo.
/// E o próprio Git recusa remover com alterações — são duas travas.
/// </summary>
/// <remarks>
/// Com um agente aberto no worktree (ADR-029), nada é removido: o processo
/// está com a pasta aberta, e apagar o chão debaixo de uma sessão em andamento
/// é o tipo de coisa que o usuário não percebe que pediu.
/// </remarks>
public sealed class RemoveWorktreeHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IGitClient git,
    IDirectoryProbe directories,
    IAgentSessionRepository agentSessions,
    IAgentProcessTracker processes,
    TimeProvider timeProvider,
    ILogger<RemoveWorktreeHandler> logger)
{
    public async Task<TaskDevelopmentView> HandleAsync(
        RemoveWorktree command,
        CancellationToken cancellationToken = default)
    {
        const DevelopmentStep step = DevelopmentStep.RemoveWorktree;

        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);
        var development = task.Development
            ?? throw new DomainException("Esta tarefa não tem ambiente de desenvolvimento.");

        if (development.Status is TaskDevelopmentStatus.Removed)
        {
            return TaskDevelopmentView.From(development);
        }

        await EnsureNoAgentRunningAsync(task.Id, cancellationToken);

        try
        {
            if (await directories.ExistsAsync(development.WorktreePath, cancellationToken))
            {
                var status = await git.GetStatusAsync(development.WorktreePath, cancellationToken);

                if (!status.IsClean)
                {
                    throw new DevelopmentStepException(
                        step,
                        $"O worktree tem {(status.Entries.Count == 1 ? "1 alteração não commitada" : $"{status.Entries.Count} alterações não commitadas")}. "
                        + "Nada foi removido.",
                        changes: status.Entries);
                }

                var result = await git.RemoveWorktreeAsync(
                    development.RepositoryPath,
                    development.WorktreePath,
                    cancellationToken);

                if (!result.Succeeded)
                {
                    throw new DevelopmentStepException(step, "O Git não removeu o worktree.", result);
                }
            }
            else
            {
                // A pasta já sumiu: só falta o repositório esquecer dela.
                var prune = await git.PruneWorktreesAsync(development.RepositoryPath, cancellationToken);

                if (!prune.Succeeded)
                {
                    logger.LogWarning(
                        "WorktreePruneFailed {TaskId} {StandardError}",
                        task.Id,
                        prune.StandardError);
                }
            }
        }
        catch (GitCommandFailedException exception)
        {
            throw new DevelopmentStepException(step, "Não foi possível verificar as alterações do worktree.", exception.Result);
        }

        task.MarkDevelopmentRemoved(timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("WorktreeRemoved {TaskId} {WorktreePath}", task.Id, development.WorktreePath);

        return TaskDevelopmentView.From(development);
    }

    private async Task EnsureNoAgentRunningAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var session = await agentSessions.FindLatestForTaskAsync(taskId, cancellationToken);

        if (session is null)
        {
            return;
        }

        if (AgentSessionReconciler.EndIfGone(session, processes, timeProvider.GetUtcNow()))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        if (session.IsActive)
        {
            throw new DomainException(
                "Há um agente de IA aberto neste worktree. Encerre-o no terminal antes de remover o worktree.");
        }
    }
}
