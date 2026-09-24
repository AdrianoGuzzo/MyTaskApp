using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Infrastructure.Git;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Git;

/// <summary>
/// O cliente Git sem Git nenhum (ADR-027): o que ele manda executar — sempre
/// argumentos separados, nunca uma linha de shell — e como lê a resposta.
/// </summary>
public class GitClientTests
{
    private const string GitExe = @"C:\Program Files\Git\cmd\git.exe";
    private const string Repository = @"C:\Projects\eco core";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeProcessRunner _runner = new();

    private GitClient Client(string? executable = GitExe) =>
        new(
            _runner,
            new GitLocator(
                (name, _) => name == "ProgramFiles" ? @"C:\Program Files" : null,
                path => path == executable,
                isWindows: true),
            NullLogger<GitClient>.Instance);

    [Fact]
    public async Task Detect_FindsTheVersion()
    {
        _runner.Respond("--version", new ProcessResult(0, "git version 2.51.0.windows.1\n", "", false));

        var installation = await Client().DetectAsync(Ct);

        installation.Should().Be(new GitInstallation(true, "2.51.0.windows.1", GitExe));
        _runner.Requests.Single().FileName.Should().Be(GitExe);
    }

    [Fact]
    public async Task Detect_NoExecutable_IsMissing_WithoutRunningAnything()
    {
        var installation = await Client(executable: null).DetectAsync(Ct);

        installation.IsInstalled.Should().BeFalse();
        _runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Detect_AnExecutableThatWillNotStart_IsMissing()
    {
        _runner.StartFailure = new ProcessStartException(GitExe, new System.ComponentModel.Win32Exception(2));

        (await Client().DetectAsync(Ct)).IsInstalled.Should().BeFalse();
    }

    [Fact]
    public async Task EveryCommand_RunsWithoutAPromptAndInEnglish()
    {
        _runner.Respond("fetch", new ProcessResult(0, "", "", false));

        await Client().FetchAsync(Repository, Ct);

        var request = _runner.Requests.Single();
        request.Arguments.Should().Equal("-c", "core.quotepath=false", "-C", Repository, "fetch", "--all", "--prune");
        request.Environment!["GIT_TERMINAL_PROMPT"].Should().Be("0");
        request.Environment["GCM_INTERACTIVE"].Should().Be("Never");
        request.Environment["LC_ALL"].Should().Be("C");
        request.Timeout.Should().Be(GitClient.NetworkTimeout);
    }

    /// <summary>
    /// O comando do pedido, com cada parte num argumento: um caminho com espaço
    /// ou uma branch com caractere estranho não tem como virar outro comando.
    /// </summary>
    [Fact]
    public async Task AddWorktree_PassesEachPartAsItsOwnArgument()
    {
        _runner.Respond("worktree", new ProcessResult(0, "", "Preparing worktree", false));

        var result = await Client().AddWorktreeAsync(
            Repository,
            @"C:\Projects\eco core-feature-x & calc",
            "feature/x&calc",
            "refs/remotes/origin/develop",
            Ct);

        _runner.Requests.Single().Arguments.Should().Equal(
            "-c", "core.quotepath=false",
            "-C", Repository,
            "worktree", "add", "--no-track", "-b", "feature/x&calc",
            @"C:\Projects\eco core-feature-x & calc",
            "refs/remotes/origin/develop");

        result.Succeeded.Should().BeTrue();
        result.Command.Should().StartWith("git -c core.quotepath=false -C \"C:\\Projects\\eco core\" worktree add");
    }

    [Fact]
    public async Task AddWorktreeForBranch_ChecksOutTheShortName_WithoutCreatingABranch()
    {
        _runner.Respond("worktree", new ProcessResult(0, "", "Preparing worktree", false));

        await Client().AddWorktreeForBranchAsync(Repository, @"C:\Projects\eco core-feature-x", "feature/x", Ct);

        _runner.Requests.Single().Arguments.Should().Equal(
            "-c", "core.quotepath=false",
            "-C", Repository,
            "worktree", "add", @"C:\Projects\eco core-feature-x", "feature/x");
    }

    [Fact]
    public async Task AddWorktreeTracking_CreatesTheLocalBranchFollowingTheRemote()
    {
        _runner.Respond("worktree", new ProcessResult(0, "", "Preparing worktree", false));

        await Client().AddWorktreeTrackingAsync(
            Repository,
            @"C:\Projects\eco core-feature-x",
            "feature/x",
            "refs/remotes/origin/feature/x",
            Ct);

        _runner.Requests.Single().Arguments.Should().Equal(
            "-c", "core.quotepath=false",
            "-C", Repository,
            "worktree", "add", "--track", "-b", "feature/x",
            @"C:\Projects\eco core-feature-x",
            "refs/remotes/origin/feature/x");
    }

    [Fact]
    public async Task RemoveWorktree_NeverForces()
    {
        _runner.Respond("worktree", new ProcessResult(0, "", "", false));

        await Client().RemoveWorktreeAsync(Repository, @"C:\Projects\wt", Ct);

        _runner.Requests.Single().Arguments.Should().NotContain(argument => argument.StartsWith("--force", StringComparison.Ordinal) || argument == "-f");
    }

    [Fact]
    public async Task FastForwardBranch_UsesARefspecWithoutPlus()
    {
        _runner.Respond("fetch", new ProcessResult(0, "", "", false));

        await Client().FastForwardBranchAsync(Repository, "develop", "refs/remotes/origin/develop", Ct);

        _runner.Requests.Single().Arguments.Should().EndWith(["fetch", ".", "refs/remotes/origin/develop:refs/heads/develop"]);
    }

    [Fact]
    public async Task Inspect_OutsideARepository_IsNotARepository()
    {
        _runner.Respond("rev-parse", new ProcessResult(128, "", "fatal: not a git repository", false));

        var info = await Client().InspectAsync(@"C:\Temp", Ct);

        info.IsRepository.Should().BeFalse();
        info.Result.ExitCode.Should().Be(128);
    }

    [Fact]
    public async Task Inspect_InsideARepository_GivesTheTopLevel()
    {
        _runner.Respond("rev-parse", new ProcessResult(0, "true\nC:/Projects/eco core\n", "", false));

        var info = await Client().InspectAsync(Repository, Ct);

        info.IsRepository.Should().BeTrue();
        info.TopLevel.Should().Be("C:/Projects/eco core");
    }

    [Fact]
    public async Task Branches_AreReadFromForEachRef()
    {
        _runner.Respond("for-each-ref", new ProcessResult(0, "refs/heads/main\0refs/remotes/origin/main\0*\n", "", false));

        var branches = await Client().ListBranchesAsync(Repository, Ct);

        branches.Should().ContainSingle().Which.IsHead.Should().BeTrue();
        _runner.Requests.Single().Arguments.Should().Contain("--format=%(refname)%00%(upstream)%00%(HEAD)");
    }

    [Fact]
    public async Task ADataCommandThatFails_ThrowsWithTheWholeResult()
    {
        _runner.Respond("status", new ProcessResult(128, "", "fatal: bad", false));

        var failure = (await FluentActions.Awaiting(() => Client().GetStatusAsync(Repository, Ct))
            .Should().ThrowAsync<GitCommandFailedException>()).Which;

        failure.Result.ExitCode.Should().Be(128);
        failure.Result.StandardError.Should().Be("fatal: bad");
    }

    [Fact]
    public async Task Status_UsesThePorcelainFormat()
    {
        _runner.Respond("status", new ProcessResult(0, " M a.cs\0", "", false));

        var status = await Client().GetStatusAsync(Repository, Ct);

        status.Entries.Should().Equal(" M a.cs");
        _runner.Requests.Single().Arguments.Should().EndWith(["status", "--porcelain=v1", "-z"]);
    }

    [Fact]
    public async Task ATimeout_IsReportedAsSuch()
    {
        _runner.Respond("fetch", new ProcessResult(-1, "", "", true));

        var result = await Client().FetchAsync(Repository, Ct);

        result.Succeeded.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
    }

    [Fact]
    public async Task WithoutGit_ACommandFailsAsGitMissing()
    {
        var failure = (await FluentActions.Awaiting(() => Client(executable: null).ListWorktreesAsync(Repository, Ct))
            .Should().ThrowAsync<GitCommandFailedException>()).Which;

        failure.Result.ExitCode.Should().Be(-1);
        _runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelling_Propagates()
    {
        _runner.Cancels = true;

        await FluentActions.Awaiting(() => Client().FetchAsync(Repository, Ct))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CheckBranchName_AsksGit()
    {
        _runner.Respond("check-ref-format", new ProcessResult(1, "", "fatal: 'x..y' is not a valid branch name", false));

        (await Client().IsValidBranchNameAsync("x..y", Ct)).Should().BeFalse();
        _runner.Requests.Single().Arguments.Should().EndWith(["check-ref-format", "--branch", "x..y"]);
    }

    /// <summary>Responde pelo subcomando do Git; guarda tudo o que foi pedido.</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Dictionary<string, ProcessResult> _responses = [];

        public List<ProcessRequest> Requests { get; } = [];

        public ProcessStartException? StartFailure { get; set; }

        public bool Cancels { get; set; }

        public void Respond(string subcommand, ProcessResult result) => _responses[subcommand] = result;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            if (Cancels)
            {
                throw new OperationCanceledException();
            }

            // git -c core.quotepath=false [-C dir] <subcomando> …
            var arguments = request.Arguments.ToList();
            var directory = arguments.IndexOf("-C");
            var subcommand = directory >= 0 ? arguments[directory + 2] : arguments[2];

            return Task.FromResult(_responses.TryGetValue(subcommand, out var result)
                ? result
                : new ProcessResult(0, string.Empty, string.Empty, false));
        }
    }
}
