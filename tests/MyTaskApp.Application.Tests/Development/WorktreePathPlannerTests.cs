using MyTaskApp.Application.Development;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>Onde o worktree nasce: ao lado do repositório, nunca dentro (ADR-027).</summary>
public class WorktreePathPlannerTests
{
    [Theory]
    [InlineData(@"C:\Projects\ecossistema-core", "feature/123-corrigir-animais", @"C:\Projects\ecossistema-core-feature-123-corrigir-animais")]
    [InlineData(@"C:\Projects\ecossistema-core", "bugfix/456-correcao-vacinacao", @"C:\Projects\ecossistema-core-bugfix-456-correcao-vacinacao")]
    [InlineData(@"C:\Projects\ecossistema-core", "feature/123/corrigir-animais", @"C:\Projects\ecossistema-core-feature-123-corrigir-animais")]
    [InlineData(@"C:\Projects\ecossistema-core\", "main", @"C:\Projects\ecossistema-core-main")]
    public void Plan_IsASiblingOfTheRepository(string repository, string branch, string expected) =>
        WorktreePathPlanner.Plan(repository, branch).Should().Be(expected);

    [Fact]
    public void Plan_NeverNestsInsideTheRepository()
    {
        var path = WorktreePathPlanner.Plan(@"C:\Projects\ecossistema-core", "feature/x");

        Path.GetDirectoryName(path).Should().Be(@"C:\Projects");
        path.Should().NotStartWith(@"C:\Projects\ecossistema-core\");
    }

    [Fact]
    public void Plan_RefusesARepositoryAtTheDriveRoot() =>
        FluentActions.Invoking(() => WorktreePathPlanner.Plan(@"C:\", "feature/x"))
            .Should().Throw<DomainException>();

    [Theory]
    [InlineData("feature/123-x", "feature-123-x")]
    [InlineData(@"feature\123", "feature-123")]
    [InlineData("a<b>c:d\"e|f?g*h", "a-b-c-d-e-f-g-h")]
    [InlineData("feature//x", "feature-x")]
    [InlineData("feature/../x", "feature-.-x")]
    [InlineData("release/6.2.", "release-6.2")]
    [InlineData("  feature com espaço  ", "feature-com-espaço")]
    [InlineData("-/-", "worktree")]
    [InlineData("CON", "CON-wt")]
    [InlineData("nul.txt", "nul.txt-wt")]
    public void SanitizeSegment_MakesASafeFolderName(string branch, string expected) =>
        WorktreePathPlanner.SanitizeSegment(branch).Should().Be(expected);

    [Fact]
    public void SanitizeSegment_NeverLeavesDotDot()
    {
        WorktreePathPlanner.SanitizeSegment("a....b").Should().NotContain("..");
    }

    [Fact]
    public void SanitizeSegment_CapsTheLength()
    {
        var segment = WorktreePathPlanner.SanitizeSegment("feature/" + new string('x', 200));

        segment.Length.Should().BeLessThanOrEqualTo(WorktreePathPlanner.MaxSegmentLength);
    }

    [Fact]
    public async Task NextFree_SkipsTakenSuffixes()
    {
        var taken = new HashSet<string> { @"C:\P\x-2", @"C:\P\x-3" };

        var free = await WorktreePathPlanner.NextFreeAsync(@"C:\P\x", path => Task.FromResult(taken.Contains(path)));

        free.Should().Be(@"C:\P\x-4");
    }

    [Theory]
    [InlineData("C:/Projects/eco", @"C:\Projects\eco")]
    [InlineData(@"c:\projects\ECO\", @"C:\Projects\eco")]
    public void SamePath_IgnoresSlashStyleAndCaseOnWindows(string left, string right)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Regra de caminho do Windows.");

        WorktreePathPlanner.SamePath(left, right).Should().BeTrue();
    }
}
