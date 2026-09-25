using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tests.Fakes;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>A cor da bolinha de worktree na lista, perguntada ao Git (ADR-034).</summary>
public class ProbeWorktreesHandlerTests
{
    private const string Path = @"C:\Projects\ecossistema-core-feature-x";

    private const string Source = "refs/heads/main";

    private const string Remote = "refs/remotes/origin/feature/x";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TaskWorktree Worktree =
        new(Guid.CreateVersion7(), "ecossistema-core", "feature/x", Source, Path);

    private readonly FakeGitClient _git = new();
    private readonly FakeDirectoryProbe _disk = new();

    public ProbeWorktreesHandlerTests() => _disk.Existing.Add(Path);

    private async Task<WorktreeSync> ProbeAsync()
    {
        var handler = new ProbeWorktreesHandler(_git, _disk, NullLogger<ProbeWorktreesHandler>.Instance);
        var result = await handler.HandleAsync(new ProbeWorktrees([Worktree]), Ct);
        return result.Should().ContainSingle().Subject;
    }

    [Fact]
    public async Task AFreshWorktree_IsClean()
    {
        var sync = await ProbeAsync();

        sync.State.Should().Be(WorktreeSyncState.Clean);
        sync.Commits.Should().Be(0);
    }

    [Fact]
    public async Task UncommittedChanges_WinOverEverythingElse()
    {
        _git.BranchStatuses[Path] = new GitBranchStatus([".M src/App.cs", "? novo.txt"]);
        _git.Divergences[Source] = new GitDivergence(2, 0);

        var sync = await ProbeAsync();

        sync.State.Should().Be(WorktreeSyncState.Dirty);
        sync.Changes.Should().Be(2);
        sync.Unpushed.Should().Be(2, "a branch nunca foi enviada");
    }

    [Fact]
    public async Task CommitsOnABranchNeverPushed_AreAllUnpushed()
    {
        _git.Divergences[Source] = new GitDivergence(3, 0);

        var sync = await ProbeAsync();

        sync.State.Should().Be(WorktreeSyncState.Unpushed);
        sync.Unpushed.Should().Be(3);
        sync.IsPublished.Should().BeFalse();
    }

    [Fact]
    public async Task WithAnUpstream_OnlyWhatIsAheadIsUnpushed()
    {
        _git.BranchStatuses[Path] = new GitBranchStatus([], "origin/feature/x", Ahead: 1);
        _git.Divergences[Source] = new GitDivergence(4, 0);

        var sync = await ProbeAsync();

        sync.State.Should().Be(WorktreeSyncState.Unpushed);
        sync.Commits.Should().Be(4);
        sync.Unpushed.Should().Be(1);
    }

    [Fact]
    public async Task EverythingSent_IsPushed()
    {
        _git.BranchStatuses[Path] = new GitBranchStatus([], "origin/feature/x");
        _git.Divergences[Source] = new GitDivergence(4, 0);

        var sync = await ProbeAsync();

        sync.State.Should().Be(WorktreeSyncState.Pushed);
        sync.IsPublished.Should().BeTrue();
    }

    [Fact]
    public async Task PushedWithoutMinusU_IsFoundOnTheRemoteBranch()
    {
        _git.Branches.Add(GitBranch.RemoteTracking("origin", "feature/x"));
        _git.Divergences[Source] = new GitDivergence(2, 0);
        _git.Divergences[Remote] = new GitDivergence(0, 0);

        var sync = await ProbeAsync();

        sync.State.Should().Be(WorktreeSyncState.Pushed);
        sync.Unpushed.Should().Be(0);
    }

    [Fact]
    public async Task AMissingFolder_IsUnknown()
    {
        _disk.Existing.Remove(Path);

        (await ProbeAsync()).State.Should().Be(WorktreeSyncState.Unknown);
    }

    [Fact]
    public async Task GitRefusing_IsUnknown_AndDoesNotThrow()
    {
        _git.Failures[nameof(IGitClient.GetBranchStatusAsync)] = FakeGitClient.Failed("git status", "fatal: not a git repository");

        (await ProbeAsync()).State.Should().Be(WorktreeSyncState.Unknown);
    }
}
