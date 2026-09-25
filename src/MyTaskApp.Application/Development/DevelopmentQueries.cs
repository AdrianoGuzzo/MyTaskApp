using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Development;

/// <summary>
/// Os ambientes da tarefa, um por repositório, na ordem em que foram criados
/// (ADR-031). Vazio = a implementação não foi iniciada.
/// </summary>
public sealed record GetTaskDevelopments(Guid TaskId);

public sealed class GetTaskDevelopmentsHandler(ITaskItemRepository tasks)
{
    public async Task<IReadOnlyList<TaskDevelopmentView>> HandleAsync(
        GetTaskDevelopments query,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(query.TaskId, cancellationToken);

        return task.Developments.Select(TaskDevelopmentView.From).ToList();
    }
}

/// <summary>
/// Tira da lista um ambiente sem worktree — que falhou ou foi removido
/// (ADR-031). A branch e o histórico de sessões ficam; só o registro sai.
/// </summary>
public sealed record ForgetDevelopment(Guid TaskId, Guid DevelopmentId);

public sealed class ForgetDevelopmentHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ILogger<ForgetDevelopmentHandler> logger)
{
    public async Task HandleAsync(ForgetDevelopment command, CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        task.ForgetDevelopment(command.DevelopmentId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("DevelopmentForgotten {TaskId} {DevelopmentId}", task.Id, command.DevelopmentId);
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

    /// <summary>
    /// A branch que o usuário escreveu como padrão do diretório: o nome curto
    /// exato (<c>develop</c> é a local, <c>origin/develop</c> a remota); sem
    /// local, <c>develop</c> ainda acha a remota — de <c>origin</c> primeiro.
    /// <c>null</c> quando o repositório não tem nenhuma com esse nome.
    /// </summary>
    public static GitBranch? FindConfigured(IReadOnlyList<GitBranch> branches, string? name)
    {
        var wanted = name?.Trim();

        if (string.IsNullOrEmpty(wanted))
        {
            return null;
        }

        return branches.FirstOrDefault(branch => branch.ShortName == wanted)
            ?? branches.FirstOrDefault(branch =>
                string.Equals(branch.ShortName, wanted, StringComparison.OrdinalIgnoreCase))
            ?? branches
                .Where(branch => branch.IsRemote
                    && string.Equals(branch.ShortName, $"{branch.Remote}/{wanted}", StringComparison.OrdinalIgnoreCase))
                .OrderBy(branch => branch.Remote == "origin" ? 0 : 1)
                .FirstOrDefault();
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
