using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Development;

/// <summary>O ambiente da tarefa, ou <c>null</c> se a implementação não foi iniciada.</summary>
public sealed record GetTaskDevelopment(Guid TaskId);

public sealed class GetTaskDevelopmentHandler(ITaskItemRepository tasks)
{
    public async Task<TaskDevelopmentView?> HandleAsync(
        GetTaskDevelopment query,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(query.TaskId, cancellationToken);

        return task.Development is null ? null : TaskDevelopmentView.From(task.Development);
    }
}

/// <summary>O Git está instalado? Cada chamada procura de novo (ver <see cref="IGitClient.DetectAsync"/>).</summary>
public sealed record DetectGit;

public sealed class DetectGitHandler(IGitClient git)
{
    public Task<GitInstallation> HandleAsync(DetectGit query, CancellationToken cancellationToken = default) =>
        git.DetectAsync(cancellationToken);
}

/// <summary>
/// O que a tela mostra ao lado do campo Diretório. <see cref="IsRepository"/> é
/// <c>null</c> quando não deu para perguntar (pasta inexistente, Git ausente).
/// </summary>
public sealed record DirectoryInspection(
    bool Exists,
    bool? IsRepository,
    string? RepositoryPath)
{
    public static readonly DirectoryInspection Missing = new(false, null, null);
}

/// <summary>A pasta existe e é um repositório Git? Qual é o worktree principal dele?</summary>
public sealed record InspectDirectory(string Path);

public sealed class InspectDirectoryHandler(IGitClient git, IDirectoryProbe directories)
{
    public async Task<DirectoryInspection> HandleAsync(
        InspectDirectory query,
        CancellationToken cancellationToken = default)
    {
        var path = query.Path.Trim().Trim('"').Trim();

        if (path.Length == 0
            || !Path.IsPathFullyQualified(path)
            || !await directories.ExistsAsync(path, cancellationToken))
        {
            return DirectoryInspection.Missing;
        }

        try
        {
            var repository = await git.InspectAsync(path, cancellationToken);

            if (!repository.IsRepository)
            {
                return new DirectoryInspection(true, false, null);
            }

            var main = await RepositoryPaths.MainWorktreeAsync(git, repository, cancellationToken);

            return new DirectoryInspection(true, true, main);
        }
        catch (GitCommandFailedException)
        {
            return new DirectoryInspection(true, null, null);
        }
    }
}

/// <summary>As branches do repositório e qual delas sugerir como origem.</summary>
public sealed record BranchList(IReadOnlyList<GitBranch> Branches, GitBranch? Suggested);

public sealed record ListBranches(string RepositoryPath);

public sealed class ListBranchesHandler(IGitClient git)
{
    public async Task<BranchList> HandleAsync(ListBranches query, CancellationToken cancellationToken = default)
    {
        var branches = await git.ListBranchesAsync(query.RepositoryPath, cancellationToken);
        var remoteDefault = await git.GetRemoteDefaultBranchAsync(query.RepositoryPath, "origin", cancellationToken);

        return new BranchList(branches, Suggest(branches, remoteDefault));
    }

    /// <summary>
    /// A branch padrão do remoto (local, se existir), senão main/master/develop,
    /// senão a que está em checkout. Uma sugestão; a escolha é do usuário.
    /// </summary>
    internal static GitBranch? Suggest(IReadOnlyList<GitBranch> branches, string? remoteDefault)
    {
        if (remoteDefault is not null)
        {
            var name = remoteDefault.Contains('/', StringComparison.Ordinal)
                ? remoteDefault[(remoteDefault.IndexOf('/', StringComparison.Ordinal) + 1)..]
                : remoteDefault;

            var local = branches.FirstOrDefault(branch => !branch.IsRemote && branch.ShortName == name);
            var remote = branches.FirstOrDefault(branch => branch.IsRemote && branch.ShortName == remoteDefault);

            if ((local ?? remote) is { } preferred)
            {
                return preferred;
            }
        }

        foreach (var name in (string[])["main", "master", "develop"])
        {
            if (branches.FirstOrDefault(branch => !branch.IsRemote && branch.ShortName == name) is { } conventional)
            {
                return conventional;
            }
        }

        return branches.FirstOrDefault(branch => branch.IsHead) ?? branches.FirstOrDefault();
    }
}

internal static class RepositoryPaths
{
    /// <summary>
    /// O worktree principal: o primeiro de <c>git worktree list</c>. É dele que
    /// sai o nome do projeto, mesmo quando o usuário digitou uma subpasta ou
    /// outro worktree ligado.
    /// </summary>
    public static async Task<string> MainWorktreeAsync(
        IGitClient git,
        GitRepositoryInfo repository,
        CancellationToken cancellationToken)
    {
        var topLevel = WorktreePathPlanner.Canonical(repository.TopLevel!);
        var worktrees = await git.ListWorktreesAsync(topLevel, cancellationToken);

        return worktrees.FirstOrDefault() is { IsBare: false } main
            ? WorktreePathPlanner.Canonical(main.Path)
            : topLevel;
    }
}
