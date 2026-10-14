using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tags;
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
    public void FindConfigured_PrefersTheExactName_ThenTheRemoteCopy()
    {
        IReadOnlyList<GitBranch> branches =
        [
            GitBranch.Local("main"),
            GitBranch.RemoteTracking("upstream", "release"),
            GitBranch.RemoteTracking("origin", "release"),
            GitBranch.RemoteTracking("origin", "main"),
        ];

        ListBranchesHandler.FindConfigured(branches, "main")!.FullRef.Should().Be("refs/heads/main");
        ListBranchesHandler.FindConfigured(branches, " origin/main ")!.FullRef.Should().Be("refs/remotes/origin/main");
        ListBranchesHandler.FindConfigured(branches, "release")!.FullRef.Should().Be("refs/remotes/origin/release");
        ListBranchesHandler.FindConfigured(branches, "Main")!.FullRef.Should().Be("refs/heads/main");
        ListBranchesHandler.FindConfigured(branches, "develop").Should().BeNull();
        ListBranchesHandler.FindConfigured(branches, null).Should().BeNull();
    }

    private static readonly IReadOnlyList<GitBranch> Candidates =
    [
        GitBranch.Local("main", "refs/remotes/origin/main", isHead: true),
        GitBranch.Local("develop", "refs/remotes/origin/develop"),
        GitBranch.Local("release"),
        GitBranch.RemoteTracking("origin", "main"),
        GitBranch.RemoteTracking("origin", "develop"),
    ];

    private static string? Chosen(
        string? chosen = null,
        string? previousAttempt = null,
        GitBranch? configured = null,
        string? otherEnvironments = null,
        GitBranch? suggested = null) =>
        ListBranchesHandler.ChooseSource(Candidates, chosen, previousAttempt, configured, otherEnvironments, suggested)?.FullRef;

    /// <summary>Cada elo da cadeia vence os que vêm depois dele.</summary>
    [Fact]
    public void ChooseSource_FollowsThePriority_OneLinkAtATime()
    {
        var release = Candidates[2];
        var main = Candidates[0];

        Chosen("refs/remotes/origin/develop", "release", release, "develop", main).Should().Be("refs/remotes/origin/develop");
        Chosen(null, "origin/main", release, "develop", main).Should().Be("refs/remotes/origin/main");
        Chosen(null, null, release, "develop", main).Should().Be("refs/heads/release");
        Chosen(null, null, null, "origin/develop", main).Should().Be("refs/remotes/origin/develop");
        Chosen(null, null, null, null, release).Should().Be("refs/heads/release");
        Chosen().Should().Be("refs/heads/main", "sem nada, a primeira da lista");
    }

    /// <summary>Um elo que não existe entre as candidatas passa a vez, e não escolhe nada.</summary>
    [Fact]
    public void ChooseSource_SkipsALinkThatIsNotAmongTheCandidates()
    {
        Chosen("refs/heads/sumiu", "sumiu", GitBranch.Local("sumiu"), "sumiu", Candidates[1]).Should().Be("refs/heads/develop");
        ListBranchesHandler.ChooseSource([], "refs/heads/main", "main", null, null, null).Should().BeNull();
    }

    private static TagDirectoryRow Directory(string? path, string? defaultBranch) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "ECO", "#22C55E", "@eco", path, null, null, defaultBranch);

    /// <summary>A pasta digitada vence o worktree principal, e a primeira etiqueta com padrão vence as outras.</summary>
    [Fact]
    public void ConfiguredDefault_ComesFromTheDirectoryThatIsThisFolder()
    {
        var eco = Path.Combine(Path.GetTempPath(), "eco");
        var worktree = Path.Combine(Path.GetTempPath(), "eco-wt");
        var other = Path.Combine(Path.GetTempPath(), "outro");

        TagDirectoryRow[] directories =
        [
            Directory(eco, null),
            Directory(worktree, " develop "),
            Directory(eco, "main"),
            Directory(eco, "release"),
        ];

        ListBranchesHandler.ConfiguredDefault(directories, worktree, eco).Should().Be("develop");
        ListBranchesHandler.ConfiguredDefault(directories, Path.Combine(eco, "sub"), eco).Should().Be("main");
        ListBranchesHandler.ConfiguredDefault(directories, other, other).Should().BeNull();
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
