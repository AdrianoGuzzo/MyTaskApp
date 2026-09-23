using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Development;

/// <summary>
/// O que a tela precisa saber antes de perguntar "remover?". <see cref="IsLeftover"/>
/// é a pasta que sobrou de uma remoção anterior: o Git já esqueceu o worktree,
/// mas algum processo segurava a pasta (ADR-029).
/// </summary>
public sealed record WorktreeInspection(bool Exists, IReadOnlyList<string> Changes, bool IsLeftover = false)
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
            if (!await WorktreeRegistry.IsRegisteredAsync(git, development, cancellationToken))
            {
                return new WorktreeInspection(true, [], IsLeftover: true);
            }

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
/// Remove o worktree da tarefa com <c>git worktree remove</c>, sem força, e
/// depois apaga o que o Git deixou na pasta. A branch fica no repositório:
/// apagar branch é decisão que o app não toma.
/// </summary>
/// <param name="TerminateLockers">
/// Os processos que o usuário viu segurando a pasta e mandou encerrar. Vazio,
/// nada é encerrado: a primeira tentativa só descobre quem são (ADR-029).
/// </param>
public sealed record RemoveWorktree(Guid TaskId, IReadOnlyList<DirectoryLocker>? TerminateLockers = null);

/// <summary>
/// Confere as alterações de novo, aqui dentro, mesmo que a tela já tenha
/// conferido: entre a pergunta e o clique o usuário pode ter editado um arquivo.
/// E o próprio Git recusa remover com alterações — são duas travas.
/// </summary>
/// <remarks>
/// Com a pasta presa (um terminal aberto nela, a IDE, um executável rodando),
/// o Git apaga os arquivos, esquece o worktree e sai com erro, deixando as
/// pastas vazias. Por isso a pergunta depois de uma falha é "o Git ainda
/// conhece o worktree?", e não o exit code: se não conhece, só falta a pasta,
/// e ela fica com <see cref="IDirectoryRemover"/>.
///
/// Com um agente aberto no worktree (ADR-030), nada é removido: o processo
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
    IDirectoryRemover remover,
    TimeProvider timeProvider,
    ILogger<RemoveWorktreeHandler> logger)
{
    private const DevelopmentStep Step = DevelopmentStep.RemoveWorktree;

    public async Task<TaskDevelopmentView> HandleAsync(
        RemoveWorktree command,
        CancellationToken cancellationToken = default)
    {
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
                if (await WorktreeRegistry.IsRegisteredAsync(git, development, cancellationToken))
                {
                    await RemoveRegisteredAsync(development, cancellationToken);
                }
                else
                {
                    await EnsureLeftoverAsync(development, cancellationToken);
                }

                await DeleteDirectoryAsync(task.Id, development, command.TerminateLockers ?? [], cancellationToken);
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
            throw new DevelopmentStepException(Step, "Não foi possível verificar as alterações do worktree.", exception.Result);
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

    private async Task RemoveRegisteredAsync(TaskDevelopment development, CancellationToken cancellationToken)
    {
        var status = await git.GetStatusAsync(development.WorktreePath, cancellationToken);

        if (!status.IsClean)
        {
            throw new DevelopmentStepException(
                Step,
                $"O worktree tem {(status.Entries.Count == 1 ? "1 alteração não commitada" : $"{status.Entries.Count} alterações não commitadas")}. "
                + "Nada foi removido.",
                changes: status.Entries);
        }

        var result = await git.RemoveWorktreeAsync(
            development.RepositoryPath,
            development.WorktreePath,
            cancellationToken);

        if (!result.Succeeded && await WorktreeRegistry.IsRegisteredAsync(git, development, cancellationToken))
        {
            throw new DevelopmentStepException(Step, "O Git não removeu o worktree.", result);
        }
    }

    /// <summary>
    /// A pasta existe e o Git não a conhece: é o que sobrou de uma remoção
    /// anterior. A não ser que agora seja um repositório por conta própria — aí
    /// não é mais o worktree desta tarefa, e não é o app quem vai apagá-lo.
    /// </summary>
    private async Task EnsureLeftoverAsync(TaskDevelopment development, CancellationToken cancellationToken)
    {
        var inspection = await git.InspectAsync(development.WorktreePath, cancellationToken);

        if (inspection.IsRepository && WorktreePathPlanner.SamePath(inspection.TopLevel, development.WorktreePath))
        {
            throw new DevelopmentStepException(
                Step,
                $"A pasta {development.WorktreePath} não é mais o worktree desta tarefa: agora é outro repositório. "
                + "Nada foi removido.",
                inspection.Result);
        }
    }

    private async Task DeleteDirectoryAsync(
        Guid taskId,
        TaskDevelopment development,
        IReadOnlyCollection<DirectoryLocker> terminate,
        CancellationToken cancellationToken)
    {
        var removal = await remover.RemoveAsync(development.WorktreePath, terminate, cancellationToken);

        foreach (var terminated in removal.Terminated)
        {
            logger.LogWarning(
                "WorktreeLockerTerminated {TaskId} {ProcessId} {ProcessName}",
                taskId,
                terminated.ProcessId,
                terminated.ProcessName);
        }

        if (removal.Removed)
        {
            return;
        }

        logger.LogWarning(
            "WorktreeDirectoryLocked {TaskId} {WorktreePath} {Lockers} {Error}",
            taskId,
            development.WorktreePath,
            string.Join(", ", removal.Lockers.Select(locker => locker.Display)),
            removal.Error);

        var message = removal.Lockers.Count switch
        {
            0 => $"O Git removeu o worktree, mas a pasta {development.WorktreePath} não pôde ser apagada"
                 + (removal.Error is null ? "." : $": {removal.Error}"),
            1 => $"O Git removeu o worktree, mas a pasta {development.WorktreePath} está sendo usada por 1 processo.",
            var count => $"O Git removeu o worktree, mas a pasta {development.WorktreePath} está sendo usada por {count} processos.",
        };

        throw new DevelopmentStepException(Step, message, lockers: removal.Lockers) { IsDirectoryLocked = true };
    }
}

internal static class WorktreeRegistry
{
    /// <summary>O repositório ainda lista o worktree desta tarefa?</summary>
    public static async Task<bool> IsRegisteredAsync(
        IGitClient git,
        TaskDevelopment development,
        CancellationToken cancellationToken)
    {
        var worktrees = await git.ListWorktreesAsync(development.RepositoryPath, cancellationToken);
        return worktrees.Any(worktree => WorktreePathPlanner.SamePath(worktree.Path, development.WorktreePath));
    }
}
