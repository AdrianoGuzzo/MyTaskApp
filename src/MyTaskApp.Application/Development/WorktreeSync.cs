using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;

namespace MyTaskApp.Application.Development;

/// <summary>
/// A cor da bolinha de worktree na lista (ADR-032), em ordem de gravidade: com
/// vários repositórios, a linha mostra o pior.
/// </summary>
public enum WorktreeSyncState
{
    /// <summary>Ainda não conferido, pasta sumiu ou o Git recusou.</summary>
    Unknown = 0,

    /// <summary>Sem alteração e sem commit próprio: o worktree acabou de nascer.</summary>
    Clean = 1,

    /// <summary>Tem commits, e todos já estão no remoto.</summary>
    Pushed = 2,

    /// <summary>Tem commits que só existem nesta máquina.</summary>
    Unpushed = 3,

    /// <summary>Tem alterações não commitadas.</summary>
    Dirty = 4,
}

/// <summary>
/// O que o Git disse de um worktree. <see cref="Commits"/> conta o que a branch
/// tem além da origem de onde nasceu; <see cref="Unpushed"/>, o que ainda não
/// chegou ao remoto — tudo, se a branch nunca foi enviada.
/// </summary>
public sealed record WorktreeSync(
    Guid DevelopmentId,
    WorktreeSyncState State,
    int Changes = 0,
    int Commits = 0,
    int Unpushed = 0,
    bool IsPublished = false)
{
    public static WorktreeSync Unknown(Guid developmentId) => new(developmentId, WorktreeSyncState.Unknown);
}

/// <summary>Confere os worktrees da lista no Git.</summary>
public sealed record ProbeWorktrees(IReadOnlyList<TaskWorktree> Worktrees);

/// <summary>
/// Pergunta ao Git, worktree por worktree, se há alteração ou commit por
/// enviar. Só leitura, e nunca lança: é um indicador, e um repositório com
/// problema não pode derrubar a lista inteira — vira <see cref="WorktreeSyncState.Unknown"/>.
/// </summary>
/// <remarks>
/// Branch sem upstream ainda pode ter sido enviada com <c>git push origin x</c>,
/// sem <c>-u</c>. Por isso, sem upstream, a comparação é com
/// <c>refs/remotes/origin/{branch}</c> quando ela existe.
/// </remarks>
public sealed class ProbeWorktreesHandler(
    IGitClient git,
    IDirectoryProbe directories,
    ILogger<ProbeWorktreesHandler> logger)
{
    /// <summary>Cada conferência são até quatro processos do Git; mais que isso em paralelo só disputa disco.</summary>
    private const int MaxParallel = 4;

    private const string DefaultRemote = "origin";

    public async Task<IReadOnlyList<WorktreeSync>> HandleAsync(
        ProbeWorktrees query,
        CancellationToken cancellationToken = default)
    {
        using var gate = new SemaphoreSlim(MaxParallel, MaxParallel);

        var probes = query.Worktrees.Select(async worktree =>
        {
            await gate.WaitAsync(cancellationToken);

            try
            {
                return await ProbeAsync(worktree, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });

        return await Task.WhenAll(probes);
    }

    private async Task<WorktreeSync> ProbeAsync(TaskWorktree worktree, CancellationToken cancellationToken)
    {
        try
        {
            if (!await directories.ExistsAsync(worktree.WorktreePath, cancellationToken))
            {
                return WorktreeSync.Unknown(worktree.DevelopmentId);
            }

            var status = await git.GetBranchStatusAsync(worktree.WorktreePath, cancellationToken);
            var commits = await CountCommitsAsync(worktree, cancellationToken);

            var (isPublished, unpushed) = await UnpushedAsync(worktree, status, commits, cancellationToken);

            var state = status.Changes.Count > 0 ? WorktreeSyncState.Dirty
                : unpushed > 0 ? WorktreeSyncState.Unpushed
                : isPublished && commits > 0 ? WorktreeSyncState.Pushed
                : WorktreeSyncState.Clean;

            return new WorktreeSync(worktree.DevelopmentId, state, status.Changes.Count, commits, unpushed, isPublished);
        }
        catch (GitCommandFailedException exception)
        {
            logger.LogWarning(
                "WorktreeProbeFailed {DevelopmentId} {Command} {StandardError}",
                worktree.DevelopmentId,
                exception.Result.Command,
                exception.Result.StandardError);

            return WorktreeSync.Unknown(worktree.DevelopmentId);
        }
    }

    /// <summary>
    /// Os commits da branch que a origem não tem. Origem apagada depois de o
    /// worktree nascer não é erro: só não dá para contar.
    /// </summary>
    private async Task<int> CountCommitsAsync(TaskWorktree worktree, CancellationToken cancellationToken)
    {
        if (!await git.CommitExistsAsync(worktree.WorktreePath, worktree.SourceBranch, cancellationToken))
        {
            return 0;
        }

        var divergence = await git.CompareAsync(worktree.WorktreePath, "HEAD", worktree.SourceBranch, cancellationToken);
        return divergence.Ahead;
    }

    private async Task<(bool IsPublished, int Unpushed)> UnpushedAsync(
        TaskWorktree worktree,
        GitBranchStatus status,
        int commits,
        CancellationToken cancellationToken)
    {
        if (status.Upstream is not null)
        {
            return (true, status.Ahead);
        }

        var remoteRef = $"{GitBranch.RemotePrefix}{DefaultRemote}/{worktree.Branch}";

        if (!await git.CommitExistsAsync(worktree.WorktreePath, remoteRef, cancellationToken))
        {
            return (false, commits);
        }

        var divergence = await git.CompareAsync(worktree.WorktreePath, "HEAD", remoteRef, cancellationToken);
        return (true, divergence.Ahead);
    }
}
