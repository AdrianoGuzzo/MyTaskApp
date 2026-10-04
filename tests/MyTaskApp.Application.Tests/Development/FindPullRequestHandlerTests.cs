using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tests.Fakes;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>A PR aberta da branch, pelo remoto <c>origin</c> e o GitHub CLI (ADR-047).</summary>
public class FindPullRequestHandlerTests
{
    private const string Repository = FakeGitClient.Repository;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PullRequestInfo Open =
        new(12, "Corrige o login", new Uri("https://github.com/acme/eco-core/pull/12"), IsDraft: false);

    private readonly FakeGitClient _git = new();

    private readonly FakePullRequestClient _github = new();

    public FindPullRequestHandlerTests()
    {
        _git.RemoteUrls[$"{Repository}|origin"] = "git@github.com:acme/eco-core.git";
        _github.Open["acme/eco-core|bug/GAECO-1"] = Open;
    }

    private FindPullRequestHandler Handler() =>
        new(_git, _github, NullLogger<FindPullRequestHandler>.Instance);

    [Fact]
    public async Task FindsTheOpenPullRequestOfTheBranch()
    {
        var lookup = await Handler().HandleAsync(new FindPullRequest(Repository, "bug/GAECO-1"), Ct);

        lookup.Should().Be(new PullRequestLookup(PullRequestSupport.Ready, Open));
        _github.Calls.Should().Equal("pr acme/eco-core bug/GAECO-1");
    }

    [Fact]
    public async Task ABranchWithoutPullRequest_IsReadyWithoutOne()
    {
        var lookup = await Handler().HandleAsync(new FindPullRequest(Repository, "feature/x"), Ct);

        lookup.Should().Be(new PullRequestLookup(PullRequestSupport.Ready));
    }

    /// <summary>Sem branch, só confere se o <c>gh</c> está pronto: é o que decide o tutorial.</summary>
    [Fact]
    public async Task WithoutBranch_OnlyChecksTheCli()
    {
        _github.Support = PullRequestSupport.CliMissing;

        var lookup = await Handler().HandleAsync(new FindPullRequest(Repository, null), Ct);

        lookup.Support.Should().Be(PullRequestSupport.CliMissing);
        lookup.NeedsCli.Should().BeTrue();
        _github.Calls.Should().Equal("check acme/eco-core");
    }

    [Fact]
    public async Task ARemoteOutsideGitHub_NeverAsksTheCli()
    {
        _git.RemoteUrls[$"{Repository}|origin"] = "https://gitlab.com/acme/eco-core.git";

        var lookup = await Handler().HandleAsync(new FindPullRequest(Repository, "bug/GAECO-1"), Ct);

        lookup.Support.Should().Be(PullRequestSupport.NotGitHub);
        lookup.NeedsCli.Should().BeFalse();
        _github.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task NoOrigin_IsNotGitHub()
    {
        _git.RemoteUrls.Clear();

        (await Handler().HandleAsync(new FindPullRequest(Repository, "bug/GAECO-1"), Ct))
            .Support.Should().Be(PullRequestSupport.NotGitHub);
    }

    [Fact]
    public async Task Refresh_ForgetsWhatWasCached()
    {
        await Handler().HandleAsync(new FindPullRequest(Repository, null, Refresh: true), Ct);

        _github.Resets.Should().Be(1);
    }

    /// <summary>É um aviso: nem o Git nem o <c>gh</c> com defeito derrubam quem pergunta.</summary>
    [Fact]
    public async Task Failures_NeverThrow()
    {
        _git.Failures[nameof(IGitClient.GetRemoteUrlAsync)] = FakeGitClient.Failed("git remote get-url origin", "fatal");

        (await Handler().HandleAsync(new FindPullRequest(Repository, "bug/GAECO-1"), Ct))
            .Support.Should().Be(PullRequestSupport.Failed);

        _git.Failures.Clear();
        _github.Failure = new InvalidOperationException("gh quebrado");

        (await Handler().HandleAsync(new FindPullRequest(Repository, "bug/GAECO-1"), Ct))
            .Support.Should().Be(PullRequestSupport.Failed);
    }

    [Fact]
    public async Task Worktrees_OnlyTheOnesWithAnOpenPullRequestComeBack()
    {
        const string withPr = @"C:\Projects\ecossistema-core-bug-GAECO-1";
        const string withoutPr = @"C:\Projects\ecossistema-core-feature-x";

        _git.RemoteUrls[$"{withPr}|origin"] = "https://github.com/acme/eco-core.git";
        _git.RemoteUrls[$"{withoutPr}|origin"] = "https://github.com/acme/eco-core.git";

        TaskWorktree first = new(Guid.CreateVersion7(), "ecossistema-core", "bug/GAECO-1", "main", withPr);
        TaskWorktree second = new(Guid.CreateVersion7(), "ecossistema-core", "feature/x", "main", withoutPr);

        var handler = new FindWorktreePullRequestsHandler(Handler());
        var result = await handler.HandleAsync(new FindWorktreePullRequests([first, second]), Ct);

        result.Should().ContainSingle().Which.Should().Be(new KeyValuePair<Guid, PullRequestInfo>(first.DevelopmentId, Open));
    }
}
