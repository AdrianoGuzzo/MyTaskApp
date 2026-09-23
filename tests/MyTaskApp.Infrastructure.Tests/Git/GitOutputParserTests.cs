using MyTaskApp.Application.Development;
using MyTaskApp.Infrastructure.Git;

namespace MyTaskApp.Infrastructure.Tests.Git;

/// <summary>A saída do Git virando dado, formato por formato (ADR-027).</summary>
public class GitOutputParserTests
{
    [Theory]
    [InlineData("git version 2.51.0\n", "2.51.0")]
    [InlineData("git version 2.40.1.windows.1\r\n", "2.40.1.windows.1")]
    [InlineData("não é git", null)]
    public void Version(string output, string? expected) =>
        GitOutputParser.ParseVersion(output).Should().Be(expected);

    [Fact]
    public void Branches_LocalAndRemote_WithoutTheSymbolicHead()
    {
        const string output =
            "refs/heads/develop\0refs/remotes/origin/develop\0 \n"
            + "refs/heads/feature/x\0\0 \n"
            + "refs/heads/main\0refs/remotes/origin/main\0*\n"
            + "refs/remotes/origin/HEAD\0\0 \n"
            + "refs/remotes/origin/develop\0\0 \n"
            + "refs/remotes/origin/release/6.2\0\0 \n"
            + "refs/remotes/upstream/main\0\0 \n";

        var branches = GitOutputParser.ParseBranches(output);

        branches.Select(branch => branch.ShortName).Should().Equal(
            "develop", "feature/x", "main", "origin/develop", "origin/release/6.2", "upstream/main");

        branches[0].Should().Be(GitBranch.Local("develop", "refs/remotes/origin/develop"));
        branches[1].UpstreamRef.Should().BeNull();
        branches[2].IsHead.Should().BeTrue();
        branches[4].Should().Be(GitBranch.RemoteTracking("origin", "release/6.2"));
        branches[5].Remote.Should().Be("upstream");
    }

    [Fact]
    public void Worktrees_InOrder_WithTheirFlags()
    {
        const string output = """
            worktree C:/Projects/ecossistema-core
            HEAD 1111111111111111111111111111111111111111
            branch refs/heads/main

            worktree C:/Projects/ecossistema-core-feature-x
            HEAD 2222222222222222222222222222222222222222
            branch refs/heads/feature/x
            locked

            worktree C:/Projects/ecossistema-core-old
            HEAD 3333333333333333333333333333333333333333
            detached
            prunable gitdir file points to non-existent location

            """;

        var worktrees = GitOutputParser.ParseWorktrees(output);

        worktrees.Should().HaveCount(3);
        worktrees[0].Should().Be(new GitWorktree("C:/Projects/ecossistema-core", "refs/heads/main"));
        worktrees[0].BranchName.Should().Be("main");
        worktrees[1].IsLocked.Should().BeTrue();
        worktrees[1].BranchName.Should().Be("feature/x");
        worktrees[2].IsDetached.Should().BeTrue();
        worktrees[2].IsPrunable.Should().BeTrue();
        worktrees[2].BranchRef.Should().BeNull();
    }

    [Fact]
    public void Worktrees_ABareMain()
    {
        var worktrees = GitOutputParser.ParseWorktrees("worktree /srv/repo.git\nbare\n\n");

        worktrees.Should().ContainSingle().Which.IsBare.Should().BeTrue();
    }

    [Fact]
    public void Status_SkipsTheOriginalNameOfARename()
    {
        const string output = " M src/App.cs\0R  novo.cs\0velho.cs\0?? ação.txt\0";

        GitOutputParser.ParseStatus(output).Should().Equal(" M src/App.cs", "R  novo.cs", "?? ação.txt");
    }

    [Fact]
    public void Status_Clean() => GitOutputParser.ParseStatus(string.Empty).Should().BeEmpty();

    [Theory]
    [InlineData("0\t0\n", 0, 0)]
    [InlineData("2\t5\n", 2, 5)]
    public void Divergence(string output, int ahead, int behind) =>
        GitOutputParser.ParseDivergence(output).Should().Be(new GitDivergence(ahead, behind));

    [Fact]
    public void Divergence_Garbage_Throws() =>
        FluentActions.Invoking(() => GitOutputParser.ParseDivergence("fatal"))
            .Should().Throw<FormatException>();
}
