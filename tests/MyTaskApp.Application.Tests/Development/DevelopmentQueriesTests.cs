using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task GetTaskDevelopments_IsEmptyUntilStarted_ThenListsOnePerRepository()
    {
        var tasks = new FakeTaskItemRepository();
        var task = TaskItem.Create("Corrigir", Now);
        tasks.Seed(task);
        var handler = new GetTaskDevelopmentsHandler(tasks);

        (await handler.HandleAsync(new GetTaskDevelopments(task.Id), Ct)).Should().BeEmpty();

        var first = task.BeginDevelopment(
            null, FakeGitClient.Repository, "main", "feature/x", @"C:\Projects\ecossistema-core-feature-x", Now);
        var second = task.BeginDevelopment(
            null, @"C:\Projects\ecossistema-api", "main", "feature/x", @"C:\Projects\ecossistema-api-feature-x", Now);

        var views = await handler.HandleAsync(new GetTaskDevelopments(task.Id), Ct);

        views.Select(view => view.Id).Should().Equal(first.Id, second.Id);
        views.Should().AllSatisfy(view =>
        {
            view.TaskId.Should().Be(task.Id);
            view.Status.Should().Be(TaskDevelopmentStatus.Creating);
            view.Branch.Should().Be("feature/x");
        });
    }

    [Fact]
    public async Task ForgetDevelopment_TakesAFailedEnvironmentOffTheList()
    {
        var tasks = new FakeTaskItemRepository();
        var task = TaskItem.Create("Corrigir", Now);
        tasks.Seed(task);
        var development = task.BeginDevelopment(
            null, FakeGitClient.Repository, "main", "feature/x", @"C:\Projects\ecossistema-core-feature-x", Now);
        task.MarkDevelopmentFailed(development.Id, "falhou", Now);
        await new ForgetDevelopmentHandler(tasks, tasks, NullLogger<ForgetDevelopmentHandler>.Instance)
            .HandleAsync(new ForgetDevelopment(task.Id, development.Id), Ct);

        task.Developments.Should().BeEmpty();
        tasks.SaveCount.Should().Be(1);
    }
}
