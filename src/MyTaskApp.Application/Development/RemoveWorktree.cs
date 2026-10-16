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
public sealed record InspectWorktree(Guid TaskId, Guid DevelopmentId);

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
        var development = task.GetDevelopment(query.DevelopmentId);

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
/// <param name="Force">
/// Só a limpeza de worktrees (ADR-065) passa algo além de
/// <see cref="WorktreeRemoveForce.None"/>: o usuário viu as alterações na lista
/// e confirmou que elas vão junto.
/// </param>
public sealed record RemoveWorktree(
    Guid TaskId,
    Guid DevelopmentId,
    IReadOnlyList<DirectoryLocker>? TerminateLockers = null,
    WorktreeRemoveForce Force = WorktreeRemoveForce.None);

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
    public async Task<TaskDevelopmentView> HandleAsync(
        RemoveWorktree command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);
        var development = task.GetDevelopment(command.DevelopmentId);

        if (development.Status is TaskDevelopmentStatus.Removed)
        {
            return TaskDevelopmentView.From(development);
        }

        await EnsureNoAgentRunningAsync(development.Id, cancellationToken);

        await new WorktreeEraser(git, directories, remover, logger).EraseAsync(
            development.RepositoryPath,
            development.WorktreePath,
            command.Force,
            command.TerminateLockers ?? [],
            cancellationToken);

        task.MarkDevelopmentRemoved(development.Id, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("WorktreeRemoved {TaskId} {WorktreePath}", task.Id, development.WorktreePath);

        return TaskDevelopmentView.From(development);
    }

    /// <remarks>Só o agente deste ambiente segura a remoção (ADR-031): o de outro repositório está em outra pasta.</remarks>
    private async Task EnsureNoAgentRunningAsync(Guid developmentId, CancellationToken cancellationToken)
    {
        var session = await agentSessions.FindLatestForDevelopmentAsync(developmentId, cancellationToken);

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

/// <summary>
/// Tira um worktree do disco e do repositório, pelo caminho: é o miolo de
/// <see cref="RemoveWorktreeHandler"/>, e a limpeza (ADR-065) usa o mesmo para
/// worktrees que nenhuma tarefa conhece.
/// </summary>
/// <remarks>
/// Três casos. Registrado no Git: <c>git worktree remove</c> (sem força, a não
/// ser que peçam) e depois a pasta. Pasta que o Git já esqueceu: é sobra, e só
/// a pasta sai. Pasta que sumiu: <c>git worktree prune</c>.
/// </remarks>
internal sealed class WorktreeEraser(
    IGitClient git,
    IDirectoryProbe directories,
    IDirectoryRemover remover,
    ILogger logger)
{
    private const DevelopmentStep Step = DevelopmentStep.RemoveWorktree;

    public async Task EraseAsync(
        string repository,
        string worktreePath,
        WorktreeRemoveForce force,
        IReadOnlyCollection<DirectoryLocker> terminate,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await directories.ExistsAsync(worktreePath, cancellationToken))
            {
                if (await WorktreeRegistry.IsRegisteredAsync(git, repository, worktreePath, cancellationToken))
                {
                    await RemoveRegisteredAsync(repository, worktreePath, force, cancellationToken);
                }
                else
                {
                    await EnsureLeftoverAsync(worktreePath, cancellationToken);
                }

                await DeleteDirectoryAsync(worktreePath, terminate, cancellationToken);
            }
            else
            {
                // A pasta já sumiu: só falta o repositório esquecer dela.
                var prune = await git.PruneWorktreesAsync(repository, cancellationToken);

                if (!prune.Succeeded)
                {
                    logger.LogWarning(
                        "WorktreePruneFailed {WorktreePath} {StandardError}",
                        worktreePath,
                        prune.StandardError);
                }
            }
        }
        catch (GitCommandFailedException exception)
        {
            throw new DevelopmentStepException(Step, "Não foi possível verificar as alterações do worktree.", exception.Result);
        }
    }

    private async Task RemoveRegisteredAsync(
        string repository,
        string worktreePath,
        WorktreeRemoveForce force,
        CancellationToken cancellationToken)
    {
        if (force is WorktreeRemoveForce.None)
        {
            var status = await git.GetStatusAsync(worktreePath, cancellationToken);

            if (!status.IsClean)
            {
                throw new DevelopmentStepException(
                    Step,
                    $"O worktree tem {(status.Entries.Count == 1 ? "1 alteração não commitada" : $"{status.Entries.Count} alterações não commitadas")}. "
                    + "Nada foi removido.",
                    changes: status.Entries);
            }
        }

        var result = await git.RemoveWorktreeAsync(repository, worktreePath, force, cancellationToken);

        if (!result.Succeeded && await WorktreeRegistry.IsRegisteredAsync(git, repository, worktreePath, cancellationToken))
        {
            throw new DevelopmentStepException(Step, "O Git não removeu o worktree.", result);
        }
    }

    /// <summary>
    /// A pasta existe e o Git não a conhece: é o que sobrou de uma remoção
    /// anterior. A não ser que agora seja um repositório por conta própria — aí
    /// não é mais aquele worktree, e não é o app quem vai apagá-lo.
    /// </summary>
    private async Task EnsureLeftoverAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var inspection = await git.InspectAsync(worktreePath, cancellationToken);

        if (inspection.IsRepository && WorktreePathPlanner.SamePath(inspection.TopLevel, worktreePath))
        {
            throw new DevelopmentStepException(
                Step,
                $"A pasta {worktreePath} não é mais o worktree desta tarefa: agora é outro repositório. "
                + "Nada foi removido.",
                inspection.Result);
        }
    }

    private async Task DeleteDirectoryAsync(
        string worktreePath,
        IReadOnlyCollection<DirectoryLocker> terminate,
        CancellationToken cancellationToken)
    {
        var removal = await remover.RemoveAsync(worktreePath, terminate, cancellationToken);

        foreach (var terminated in removal.Terminated)
        {
            logger.LogWarning(
                "WorktreeLockerTerminated {WorktreePath} {ProcessId} {ProcessName}",
                worktreePath,
                terminated.ProcessId,
                terminated.ProcessName);
        }

        if (removal.Removed)
        {
            return;
        }

        logger.LogWarning(
            "WorktreeDirectoryLocked {WorktreePath} {Lockers} {Error}",
            worktreePath,
            string.Join(", ", removal.Lockers.Select(locker => locker.Display)),
            removal.Error);

        var message = removal.Lockers.Count switch
        {
            0 => $"O Git removeu o worktree, mas a pasta {worktreePath} não pôde ser apagada"
                 + (removal.Error is null ? "." : $": {removal.Error}"),
            1 => $"O Git removeu o worktree, mas a pasta {worktreePath} está sendo usada por 1 processo.",
            var count => $"O Git removeu o worktree, mas a pasta {worktreePath} está sendo usada por {count} processos.",
        };

        throw new DevelopmentStepException(Step, message, lockers: removal.Lockers) { IsDirectoryLocked = true };
    }
}

internal static class WorktreeRegistry
{
    /// <summary>O repositório ainda lista o worktree desta tarefa?</summary>
    public static Task<bool> IsRegisteredAsync(
        IGitClient git,
        TaskDevelopment development,
        CancellationToken cancellationToken) =>
        IsRegisteredAsync(git, development.RepositoryPath, development.WorktreePath, cancellationToken);

    /// <summary>O repositório ainda lista o worktree deste caminho?</summary>
    public static async Task<bool> IsRegisteredAsync(
        IGitClient git,
        string repository,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var worktrees = await git.ListWorktreesAsync(repository, cancellationToken);
        return worktrees.Any(worktree => WorktreePathPlanner.SamePath(worktree.Path, worktreePath));
    }
}
