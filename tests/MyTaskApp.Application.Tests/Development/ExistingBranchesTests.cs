using MyTaskApp.Application.Development;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>
/// "Nunca criar uma branch duplicada" (ADR-045): a local é usada, a remota
/// vira local acompanhando-a, e só sem as duas a branch nasce.
/// </summary>
public class ExistingBranchesTests
{
    private static readonly IReadOnlyList<GitBranch> Branches =
    [
        GitBranch.Local("develop"),
        GitBranch.Local("bug/GAECO-1234"),
        GitBranch.RemoteTracking("origin", "develop"),
        GitBranch.RemoteTracking("origin", "feature/GAECO-1300"),
        GitBranch.RemoteTracking("upstream", "feature/GAECO-1300"),
    ];

    [Fact]
    public void ALocalBranch_IsUsed()
    {
        var (kind, branch) = ExistingBranches.Find(Branches, "bug/GAECO-1234");

        kind.Should().Be(ExistingBranchKind.Local);
        branch!.ShortName.Should().Be("bug/GAECO-1234");
    }

    [Fact]
    public void TheCaseDoesNotMatter_BecauseRefsAreFilesOnWindows()
    {
        ExistingBranches.Find(Branches, "BUG/gaeco-1234").Kind.Should().Be(ExistingBranchKind.Local);
    }

    [Fact]
    public void OnlyOnTheRemote_IsOfferedForCheckout()
    {
        var (kind, branch) = ExistingBranches.Find(Branches, "feature/GAECO-1300");

        kind.Should().Be(ExistingBranchKind.Remote);
        branch!.Remote.Should().Be("origin");
    }

    [Fact]
    public void TheSourceRemote_WinsOverOrigin()
    {
        ExistingBranches.Find(Branches, "feature/GAECO-1300", preferredRemote: "upstream")
            .Branch!.Remote.Should().Be("upstream");
    }

    [Fact]
    public void ALocalBranch_WinsOverTheRemote()
    {
        ExistingBranches.Find(Branches, "develop").Kind.Should().Be(ExistingBranchKind.Local);
    }

    [Fact]
    public void ANewName_IsNone()
    {
        ExistingBranches.Find(Branches, "task/GAECO-9999").Should().Be((ExistingBranchKind.None, (GitBranch?)null));
    }
}
