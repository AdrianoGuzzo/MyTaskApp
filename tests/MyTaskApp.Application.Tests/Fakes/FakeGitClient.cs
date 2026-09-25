using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Development;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>
/// Um repositório Git em memória, do tamanho que os casos de uso precisam
/// (ADR-027). Guarda cada operação que muda algo em <see cref="Calls"/>, para os
/// testes afirmarem o que <b>não</b> foi feito — nenhum fast-forward sobre
/// alterações locais, nenhum worktree removido com trabalho não commitado.
/// </summary>
internal sealed class FakeGitClient : IGitClient
{
    public const string Repository = @"C:\Projects\ecossistema-core";

    public GitInstallation Installation { get; set; } = new(true, "2.51.0", @"C:\Program Files\Git\cmd\git.exe");

    /// <summary>Pastas que são repositório (ou estão dentro de um), com a raiz de cada uma.</summary>
    public Dictionary<string, string> Repositories { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [Repository] = "C:/Projects/ecossistema-core",
    };

    public List<GitWorktree> Worktrees { get; } =
    [
        new("C:/Projects/ecossistema-core", "refs/heads/main"),
    ];

    public List<GitBranch> Branches { get; } =
    [
        GitBranch.Local("main", "refs/remotes/origin/main", isHead: true),
        GitBranch.Local("develop", "refs/remotes/origin/develop"),
        GitBranch.RemoteTracking("origin", "main"),
        GitBranch.RemoteTracking("origin", "develop"),
    ];

    public string? RemoteDefault { get; set; } = "origin/main";

    public GitCommandResult FetchResult { get; set; } = Ok("git fetch --all --prune");

    public GitDivergence Divergence { get; set; } = new(0, 0);

    /// <summary>Status por pasta; o que não estiver aqui está limpo.</summary>
    public Dictionary<string, GitStatus> Statuses { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Status com branch por pasta; o que não estiver aqui está limpo e sem upstream.</summary>
    public Dictionary<string, GitBranchStatus> BranchStatuses { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Divergência por ref comparada (o lado direito); o que não estiver aqui cai em <see cref="Divergence"/>.</summary>
    public Dictionary<string, GitDivergence> Divergences { get; } = new(StringComparer.OrdinalIgnoreCase);

    public GitCommandResult FastForwardResult { get; set; } = Ok("git merge --ff-only");

    public Func<string, bool> AcceptsBranchName { get; set; } = _ => true;

    public GitCommandResult? AddWorktreeFailure { get; set; }

    public GitCommandResult RemoveResult { get; set; } = Ok("git worktree remove");

    /// <summary>
    /// A falha de quando algo segura a pasta: o Git apaga os arquivos, esquece o
    /// worktree e só então sai com erro (ADR-029).
    /// </summary>
    public bool RemoveForgetsOnFailure { get; set; }

    /// <summary>Faz o método de dado lançar, como o Git saindo com erro.</summary>
    public Dictionary<string, GitCommandResult> Failures { get; } = [];

    public List<string> Calls { get; } = [];

    public static GitCommandResult Ok(string command, string output = "") => new(command, 0, output, string.Empty);

    public static GitCommandResult Failed(string command, string error, int exitCode = 128) =>
        new(command, exitCode, string.Empty, error);

    public Task<GitInstallation> DetectAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("--version");
        return Task.FromResult(Installation);
    }

    public Task<GitRepositoryInfo> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        Throw(nameof(InspectAsync));

        var result = Ok($"git -C {path} rev-parse");

        return Task.FromResult(Repositories.TryGetValue(path, out var topLevel)
            ? new GitRepositoryInfo(true, topLevel, result)
            : new GitRepositoryInfo(false, null, Failed($"git -C {path} rev-parse", "fatal: not a git repository")));
    }

    public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string repository, CancellationToken cancellationToken = default)
    {
        Throw(nameof(ListWorktreesAsync));
        return Task.FromResult<IReadOnlyList<GitWorktree>>([.. Worktrees]);
    }

    public Task<IReadOnlyList<GitBranch>> ListBranchesAsync(string repository, CancellationToken cancellationToken = default)
    {
        Throw(nameof(ListBranchesAsync));
        return Task.FromResult<IReadOnlyList<GitBranch>>([.. Branches]);
    }

    public Task<string?> GetRemoteDefaultBranchAsync(string repository, string remote, CancellationToken cancellationToken = default) =>
        Task.FromResult(RemoteDefault);

    public Task<GitCommandResult> FetchAsync(string repository, CancellationToken cancellationToken = default)
    {
        // A rede é onde o cancelamento de verdade acontece.
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("fetch");
        return Task.FromResult(FetchResult);
    }

    public Task<bool> CommitExistsAsync(string repository, string revision, CancellationToken cancellationToken = default) =>
        Task.FromResult(Branches.Any(branch => branch.FullRef == revision));

    public Task<GitDivergence> CompareAsync(
        string repository,
        string localRef,
        string upstreamRef,
        CancellationToken cancellationToken = default)
    {
        Throw(nameof(CompareAsync));
        return Task.FromResult(Divergences.TryGetValue(upstreamRef, out var divergence) ? divergence : Divergence);
    }

    public Task<GitStatus> GetStatusAsync(string workingTree, CancellationToken cancellationToken = default)
    {
        Throw(nameof(GetStatusAsync));

        var key = Statuses.Keys.FirstOrDefault(path => WorktreePathPlanner.SamePath(path, workingTree));
        return Task.FromResult(key is null ? GitStatus.Clean : Statuses[key]);
    }

    public Task<GitBranchStatus> GetBranchStatusAsync(string workingTree, CancellationToken cancellationToken = default)
    {
        Throw(nameof(GetBranchStatusAsync));

        var key = BranchStatuses.Keys.FirstOrDefault(path => WorktreePathPlanner.SamePath(path, workingTree));
        return Task.FromResult(key is null ? GitBranchStatus.Clean : BranchStatuses[key]);
    }

    public Task<GitCommandResult> FastForwardCheckedOutAsync(
        string workingTree,
        string upstreamRef,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"merge --ff-only {upstreamRef} @ {workingTree}");
        return Task.FromResult(FastForwardResult);
    }

    public Task<GitCommandResult> FastForwardBranchAsync(
        string repository,
        string branch,
        string upstreamRef,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"fetch . {upstreamRef}:refs/heads/{branch}");
        return Task.FromResult(FastForwardResult);
    }

    public Task<bool> IsValidBranchNameAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(AcceptsBranchName(name));

    public Task<GitCommandResult> AddWorktreeAsync(
        string repository,
        string path,
        string newBranch,
        string startRef,
        CancellationToken cancellationToken = default)
    {
        var command = $"worktree add --no-track -b {newBranch} {path} {startRef}";
        Calls.Add(command);

        if (AddWorktreeFailure is { } failure)
        {
            return Task.FromResult(failure);
        }

        Worktrees.Add(new GitWorktree(path.Replace('\\', '/'), GitBranch.LocalPrefix + newBranch));
        Branches.Add(GitBranch.Local(newBranch));
        Repositories[path] = path.Replace('\\', '/');

        return Task.FromResult(Ok("git " + command));
    }

    public Task<GitCommandResult> AddWorktreeForBranchAsync(
        string repository,
        string path,
        string branch,
        CancellationToken cancellationToken = default)
    {
        var command = $"worktree add {path} {branch}";
        Calls.Add(command);

        if (AddWorktreeFailure is { } failure)
        {
            return Task.FromResult(failure);
        }

        Worktrees.Add(new GitWorktree(path.Replace('\\', '/'), GitBranch.LocalPrefix + branch));
        Repositories[path] = path.Replace('\\', '/');

        return Task.FromResult(Ok("git " + command));
    }

    public Task<GitCommandResult> AddWorktreeTrackingAsync(
        string repository,
        string path,
        string newBranch,
        string remoteBranch,
        CancellationToken cancellationToken = default)
    {
        var command = $"worktree add --track -b {newBranch} {path} {remoteBranch}";
        Calls.Add(command);

        if (AddWorktreeFailure is { } failure)
        {
            return Task.FromResult(failure);
        }

        Worktrees.Add(new GitWorktree(path.Replace('\\', '/'), GitBranch.LocalPrefix + newBranch));
        Branches.Add(GitBranch.Local(newBranch, upstreamRef: remoteBranch));
        Repositories[path] = path.Replace('\\', '/');

        return Task.FromResult(Ok("git " + command));
    }

    public Task<string?> GetCurrentBranchAsync(string workingTree, CancellationToken cancellationToken = default) =>
        Task.FromResult(Worktrees.FirstOrDefault(worktree => WorktreePathPlanner.SamePath(worktree.Path, workingTree))?.BranchName);

    public Task<GitCommandResult> RemoveWorktreeAsync(string repository, string path, CancellationToken cancellationToken = default)
    {
        Calls.Add($"worktree remove {path}");

        if (RemoveResult.Succeeded || RemoveForgetsOnFailure)
        {
            Worktrees.RemoveAll(worktree => WorktreePathPlanner.SamePath(worktree.Path, path));
        }

        return Task.FromResult(RemoveResult);
    }

    public Task<GitCommandResult> PruneWorktreesAsync(string repository, CancellationToken cancellationToken = default)
    {
        Calls.Add("worktree prune");
        return Task.FromResult(Ok("git worktree prune"));
    }

    private void Throw(string method)
    {
        if (Failures.TryGetValue(method, out var result))
        {
            throw new GitCommandFailedException(result);
        }
    }
}

/// <summary>O disco, em memória: existe o que estiver listado.</summary>
internal sealed class FakeDirectoryProbe : IDirectoryProbe
{
    public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase) { FakeGitClient.Repository };

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Existing.Contains(WorktreePathPlanner.Canonical(path)));

    public Task<bool> PathExistsAsync(string path, CancellationToken cancellationToken = default) =>
        ExistsAsync(path, cancellationToken);
}

/// <summary>
/// Apagar pasta, em memória: devolve o que o teste enfileirou (ou "apagou") e
/// guarda quem mandou encerrar.
/// </summary>
internal sealed class FakeDirectoryRemover : IDirectoryRemover
{
    public Queue<DirectoryRemoval> Results { get; } = [];

    public List<(string Path, IReadOnlyCollection<DirectoryLocker> Terminate)> Calls { get; } = [];

    public Task<DirectoryRemoval> RemoveAsync(
        string path,
        IReadOnlyCollection<DirectoryLocker> terminate,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((path, terminate));
        return Task.FromResult(Results.TryDequeue(out var result) ? result : DirectoryRemoval.Done);
    }
}

/// <summary>Guarda cada aviso de progresso, na ordem, sem trocar de thread.</summary>
internal sealed class RecordingProgress : IProgress<DevelopmentProgress>
{
    public List<DevelopmentProgress> Reports { get; } = [];

    public void Report(DevelopmentProgress value) => Reports.Add(value);

    public DevelopmentProgress? Last(DevelopmentStep step) => Reports.LastOrDefault(report => report.Step == step);
}
