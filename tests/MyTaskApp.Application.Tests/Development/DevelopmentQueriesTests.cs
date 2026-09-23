using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>O que a aba Desenvolvimento pergunta antes de agir (ADR-027).</summary>
public class DevelopmentQueriesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeGitClient _git = new();
    private readonly FakeDirectoryProbe _disk = new();

    [Fact]
    public async Task InspectDirectory_AnExistingRepository_IsValid()
    {
        var inspection = await new InspectDirectoryHandler(_git, _disk)
            .HandleAsync(new InspectDirectory(FakeGitClient.Repository), Ct);

        inspection.Should().Be(new DirectoryInspection(true, true, FakeGitClient.Repository));
    }

    [Fact]
    public async Task InspectDirectory_AMissingFolder_IsNotAskedToGit()
    {
        var inspection = await new InspectDirectoryHandler(_git, _disk)
            .HandleAsync(new InspectDirectory(@"C:\Nao\Existe"), Ct);

        inspection.Should().Be(DirectoryInspection.Missing);
    }

    [Fact]
    public async Task InspectDirectory_AFolderOutsideGit_IsNotARepository()
    {
        _disk.Existing.Add(@"C:\Projects\docs");

        var inspection = await new InspectDirectoryHandler(_git, _disk)
            .HandleAsync(new InspectDirectory(@"C:\Projects\docs"), Ct);

        inspection.Should().Be(new DirectoryInspection(true, false, null));
    }

    [Fact]
    public async Task InspectDirectory_WithoutGit_DoesNotKnow()
    {
        _git.Failures[nameof(IGitClient.InspectAsync)] = new GitCommandResult("git", -1, "", "Git não encontrado.");

        var inspection = await new InspectDirectoryHandler(_git, _disk)
            .HandleAsync(new InspectDirectory(FakeGitClient.Repository), Ct);

        inspection.Should().Be(new DirectoryInspection(true, null, null));
    }

    [Fact]
    public async Task ListBranches_SuggestsTheLocalCopyOfTheRemoteDefault()
    {
        var list = await new ListBranchesHandler(_git).HandleAsync(new ListBranches(FakeGitClient.Repository), Ct);

        list.Branches.Should().HaveCount(4);
        list.Suggested!.FullRef.Should().Be("refs/heads/main");
    }

    [Fact]
    public void Suggest_FallsBackToConventionalNames_ThenHead()
    {
        ListBranchesHandler.Suggest([GitBranch.Local("x", isHead: true), GitBranch.Local("develop")], null)!
            .ShortName.Should().Be("develop");

        ListBranchesHandler.Suggest([GitBranch.Local("a"), GitBranch.Local("b", isHead: true)], null)!
            .ShortName.Should().Be("b");

        ListBranchesHandler.Suggest([GitBranch.RemoteTracking("origin", "trunk")], "origin/trunk")!
            .FullRef.Should().Be("refs/remotes/origin/trunk");
    }

    [Fact]
    public async Task GetTaskDevelopment_IsNullUntilStarted_ThenShowsTheRecord()
    {
        var tasks = new FakeTaskItemRepository();
        var task = TaskItem.Create("Corrigir", Now);
        tasks.Seed(task);
        var handler = new GetTaskDevelopmentHandler(tasks);

        (await handler.HandleAsync(new GetTaskDevelopment(task.Id), Ct)).Should().BeNull();

        task.BeginDevelopment(FakeGitClient.Repository, "main", "feature/x", @"C:\Projects\ecossistema-core-feature-x", Now);

        var view = await handler.HandleAsync(new GetTaskDevelopment(task.Id), Ct);
        view!.Status.Should().Be(TaskDevelopmentStatus.Creating);
        view.Branch.Should().Be("feature/x");
    }
}
