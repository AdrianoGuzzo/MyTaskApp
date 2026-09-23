using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Development;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Git;

/// <summary>
/// O Git CLI de verdade, sempre por <see cref="IProcessRunner"/> e com os
/// argumentos separados (ADR-027).
/// </summary>
/// <remarks>
/// <para>
/// Todo comando roda com:
/// <list type="bullet">
/// <item><c>GIT_TERMINAL_PROMPT=0</c> e <c>GCM_INTERACTIVE=Never</c> — um fetch
/// que precisasse de senha ficaria esperando alguém digitar num terminal que
/// não existe, até o timeout. Melhor falhar na hora e mostrar o erro.</item>
/// <item><c>LC_ALL=C</c> — as mensagens de erro chegam em inglês, que é o que
/// <see cref="StartDevelopmentHandler"/> sabe traduzir.</item>
/// <item><c>GIT_OPTIONAL_LOCKS=0</c> — o <c>status</c> não disputa o
/// <c>index.lock</c> com a IDE aberta no mesmo repositório.</item>
/// <item><c>-c core.quotepath=false</c> — acentos nos nomes de arquivo chegam
/// como acentos, e não como <c>\303\247</c>.</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class GitClient(
    IProcessRunner runner,
    GitLocator locator,
    ILogger<GitClient> logger) : IGitClient
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(2);

    internal static readonly TimeSpan CheckoutTimeout = TimeSpan.FromMinutes(2);

    internal static readonly IReadOnlyDictionary<string, string?> Environment = new Dictionary<string, string?>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "Never",
        ["LC_ALL"] = "C",
        ["GIT_OPTIONAL_LOCKS"] = "0",
    };

    private readonly Lock _gate = new();

    private string? _executable;

    public async Task<GitInstallation> DetectAsync(CancellationToken cancellationToken = default)
    {
        var executable = locator.Locate();

        lock (_gate)
        {
            _executable = executable;
        }

        if (executable is null)
        {
            return GitInstallation.Missing;
        }

        try
        {
            var result = await RunAsync(null, ["--version"], DefaultTimeout, cancellationToken);

            return result.Succeeded && GitOutputParser.ParseVersion(result.StandardOutput) is { } version
                ? new GitInstallation(true, version, executable)
                : GitInstallation.Missing;
        }
        catch (GitCommandFailedException)
        {
            return GitInstallation.Missing;
        }
    }

    public async Task<GitRepositoryInfo> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(path, ["rev-parse", "--is-inside-work-tree", "--show-toplevel"], DefaultTimeout, cancellationToken);

        if (!result.Succeeded)
        {
            return new GitRepositoryInfo(false, null, result);
        }

        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return lines is ["true", var topLevel, ..]
            ? new GitRepositoryInfo(true, topLevel, result)
            : new GitRepositoryInfo(false, null, result);
    }

    public async Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(
        string repository,
        CancellationToken cancellationToken = default)
    {
        var result = await RequireAsync(repository, ["worktree", "list", "--porcelain"], DefaultTimeout, cancellationToken);
        return GitOutputParser.ParseWorktrees(result.StandardOutput);
    }

    public async Task<IReadOnlyList<GitBranch>> ListBranchesAsync(
        string repository,
        CancellationToken cancellationToken = default)
    {
        var result = await RequireAsync(
            repository,
            ["for-each-ref", "--format=%(refname)%00%(upstream)%00%(HEAD)", "refs/heads", "refs/remotes"],
            DefaultTimeout,
            cancellationToken);

        return GitOutputParser.ParseBranches(result.StandardOutput);
    }

    public async Task<string?> GetRemoteDefaultBranchAsync(
        string repository,
        string remote,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            repository,
            ["symbolic-ref", "--quiet", "--short", $"refs/remotes/{remote}/HEAD"],
            DefaultTimeout,
            cancellationToken);

        var name = result.StandardOutput.Trim();
        return result.Succeeded && name.Length > 0 ? name : null;
    }

    public Task<GitCommandResult> FetchAsync(string repository, CancellationToken cancellationToken = default) =>
        RunAsync(repository, ["fetch", "--all", "--prune"], NetworkTimeout, cancellationToken);

    public async Task<bool> CommitExistsAsync(
        string repository,
        string revision,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            repository,
            ["rev-parse", "--verify", "--quiet", revision + "^{commit}"],
            DefaultTimeout,
            cancellationToken);

        return result.Succeeded;
    }

    public async Task<GitDivergence> CompareAsync(
        string repository,
        string localRef,
        string upstreamRef,
        CancellationToken cancellationToken = default)
    {
        var result = await RequireAsync(
            repository,
            ["rev-list", "--left-right", "--count", $"{localRef}...{upstreamRef}"],
            DefaultTimeout,
            cancellationToken);

        return GitOutputParser.ParseDivergence(result.StandardOutput);
    }

    public async Task<GitStatus> GetStatusAsync(string workingTree, CancellationToken cancellationToken = default)
    {
        var result = await RequireAsync(workingTree, ["status", "--porcelain=v1", "-z"], DefaultTimeout, cancellationToken);
        return new GitStatus(GitOutputParser.ParseStatus(result.StandardOutput));
    }

    public Task<GitCommandResult> FastForwardCheckedOutAsync(
        string workingTree,
        string upstreamRef,
        CancellationToken cancellationToken = default) =>
        RunAsync(workingTree, ["merge", "--ff-only", upstreamRef], CheckoutTimeout, cancellationToken);

    public Task<GitCommandResult> FastForwardBranchAsync(
        string repository,
        string branch,
        string upstreamRef,
        CancellationToken cancellationToken = default) =>
        // Sem "+" na frente do refspec: o Git só aceita se for fast-forward.
        RunAsync(repository, ["fetch", ".", $"{upstreamRef}:{GitBranch.LocalPrefix}{branch}"], DefaultTimeout, cancellationToken);

    public async Task<bool> IsValidBranchNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(null, ["check-ref-format", "--branch", name], DefaultTimeout, cancellationToken);
        return result.Succeeded;
    }

    public Task<GitCommandResult> AddWorktreeAsync(
        string repository,
        string path,
        string newBranch,
        string startRef,
        CancellationToken cancellationToken = default) =>
        // --no-track: partindo de origin/x, o Git faria a branch nova acompanhar
        // origin/x, e o primeiro push iria para a branch errada.
        RunAsync(
            repository,
            ["worktree", "add", "--no-track", "-b", newBranch, path, startRef],
            CheckoutTimeout,
            cancellationToken);

    public async Task<string?> GetCurrentBranchAsync(string workingTree, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workingTree, ["rev-parse", "--abbrev-ref", "HEAD"], DefaultTimeout, cancellationToken);
        var branch = result.StandardOutput.Trim();

        return result.Succeeded && branch.Length > 0 ? branch : null;
    }

    public Task<GitCommandResult> RemoveWorktreeAsync(
        string repository,
        string path,
        CancellationToken cancellationToken = default) =>
        RunAsync(repository, ["worktree", "remove", path], CheckoutTimeout, cancellationToken);

    public Task<GitCommandResult> PruneWorktreesAsync(string repository, CancellationToken cancellationToken = default) =>
        RunAsync(repository, ["worktree", "prune"], DefaultTimeout, cancellationToken);

    private async Task<GitCommandResult> RequireAsync(
        string? directory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(directory, arguments, timeout, cancellationToken);
        return result.Succeeded ? result : throw new GitCommandFailedException(result);
    }

    /// <summary>
    /// Monta <c>git [-c …] [-C dir] …</c> como lista. Git ausente vira
    /// <see cref="GitCommandFailedException"/> com código -1, para quem chama
    /// tratar como qualquer outra recusa.
    /// </summary>
    private async Task<GitCommandResult> RunAsync(
        string? directory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        List<string> fullArguments = ["-c", "core.quotepath=false"];

        if (directory is not null)
        {
            fullArguments.Add("-C");
            fullArguments.Add(directory);
        }

        fullArguments.AddRange(arguments);

        var executable = Executable();
        var request = new ProcessRequest(executable ?? "git", fullArguments, timeout, Environment: Environment);
        var display = "git " + request.Display[(request.Display.IndexOf(' ', StringComparison.Ordinal) + 1)..];

        if (executable is null)
        {
            throw new GitCommandFailedException(new GitCommandResult(display, -1, string.Empty, "Git não encontrado."));
        }

        ProcessResult result;

        try
        {
            result = await runner.RunAsync(request, cancellationToken);
        }
        catch (ProcessStartException exception)
        {
            logger.LogWarning(exception, "GitStartFailed {Executable}", executable);

            lock (_gate)
            {
                _executable = null;
            }

            throw new GitCommandFailedException(
                new GitCommandResult(display, -1, string.Empty, exception.InnerException?.Message ?? exception.Message));
        }

        if (result.ExitCode != 0 || result.TimedOut)
        {
            logger.LogInformation(
                "GitCommandFailed {Command} {ExitCode} {TimedOut} {StandardError}",
                display,
                result.ExitCode,
                result.TimedOut,
                result.StandardError.Trim());
        }

        return new GitCommandResult(display, result.ExitCode, result.StandardOutput, result.StandardError, result.TimedOut);
    }

    private string? Executable()
    {
        lock (_gate)
        {
            return _executable ??= locator.Locate();
        }
    }
}
