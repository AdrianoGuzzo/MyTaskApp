using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.External;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.External;

public class ExternalTaskHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _tasks = new();

    private readonly FakeExternalTasks _jira = new();

    private readonly FakeBranchConventionStore _conventions = new();

    private readonly FakeTimeProvider _time = new(Now);

    private ExternalTaskSearch Search() => new([_jira], [_jira], _time, NullLogger<ExternalTaskSearch>.Instance);

    private TaskItem Seeded(string title = "Corrigir erro de sincronização")
    {
        var task = TaskItem.Create(title, Now);
        _tasks.Seed(task);
        return task;
    }

    private TaskItem Linked(string status = "A fazer")
    {
        var task = Seeded();
        task.LinkExternal(FakeExternalTasks.Issue("GAECO-1234", "Corrigir erro de sincronização", status: status).ToLink(Now));
        return task;
    }

    [Fact]
    public async Task Link_GivesTheTaskTheSnapshot()
    {
        var task = Seeded("corrigir sync");

        await new LinkTaskToExternalHandler(_tasks, _tasks, _time)
            .HandleAsync(new LinkTaskToExternal(task.Id, FakeExternalTasks.Issue("GAECO-1234", "Corrigir erro")), Ct);

        task.External!.Id.Should().Be("GAECO-1234");
        task.Title.Should().Be("corrigir sync", "vincular uma tarefa que já existe não troca o título que o usuário escreveu");
        _tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Unlink_KeepsTheTask()
    {
        var task = Linked();

        await new UnlinkTaskFromExternalHandler(_tasks, _tasks).HandleAsync(new UnlinkTaskFromExternal(task.Id), Ct);

        task.External.Should().BeNull();
        _tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Refresh_ReadsTheIssueAgain()
    {
        var task = Linked(status: "A fazer");
        _jira.Issues.Add(FakeExternalTasks.Issue("GAECO-1234", "Corrigir erro de sincronização", status: "Em revisão"));
        _time.Advance(TimeSpan.FromHours(3));

        var link = await new RefreshExternalTaskHandler(_tasks, _tasks, Search(), _time)
            .HandleAsync(new RefreshExternalTask(task.Id), Ct);

        link.Status.Should().Be("Em revisão");
        task.External!.SyncedAt.Should().Be(Now.AddHours(3));
        _tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Refresh_Offline_KeepsTheSnapshot_AndExplains()
    {
        var task = Linked(status: "A fazer");
        _jira.Failure = ExternalTaskFailure.Unavailable;

        var refresh = () => new RefreshExternalTaskHandler(_tasks, _tasks, Search(), _time)
            .HandleAsync(new RefreshExternalTask(task.Id), Ct);

        (await refresh.Should().ThrowAsync<DomainException>()).Which.Message.Should().Contain("Jira");
        task.External!.Status.Should().Be("A fazer");
        _tasks.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Refresh_OfAnIssueThatIsGone_SaysSo()
    {
        var task = Linked();

        var refresh = () => new RefreshExternalTaskHandler(_tasks, _tasks, Search(), _time)
            .HandleAsync(new RefreshExternalTask(task.Id), Ct);

        (await refresh.Should().ThrowAsync<DomainException>()).Which.Message.Should().Contain("GAECO-1234");
        task.External.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_OfALocalTask_IsRefused()
    {
        var task = Seeded();

        var refresh = () => new RefreshExternalTaskHandler(_tasks, _tasks, Search(), _time)
            .HandleAsync(new RefreshExternalTask(task.Id), Ct);

        await refresh.Should().ThrowAsync<DomainException>();
        _jira.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task Context_OfALinkedTask_SuggestsTheConventionBranch()
    {
        var task = Linked();

        var context = await new GetTaskExternalContextHandler(_tasks, _conventions)
            .HandleAsync(new GetTaskExternalContext(task.Id), Ct);

        context.Link!.Id.Should().Be("GAECO-1234");
        context.SuggestedBranch.Should().Be("bug/GAECO-1234");
    }

    [Fact]
    public async Task Context_FollowsTheUsersConventions()
    {
        var task = Linked();
        _conventions.Conventions = BranchConventions.Parse("Bug = fix/{id}");

        var context = await new GetTaskExternalContextHandler(_tasks, _conventions)
            .HandleAsync(new GetTaskExternalContext(task.Id), Ct);

        context.SuggestedBranch.Should().Be("fix/GAECO-1234");
    }

    [Fact]
    public async Task Context_OfALocalTask_HasNoSuggestion_SoTheTitleSlugStays()
    {
        var task = Seeded();

        var context = await new GetTaskExternalContextHandler(_tasks, _conventions)
            .HandleAsync(new GetTaskExternalContext(task.Id), Ct);

        context.Link.Should().BeNull();
        context.SuggestedBranch.Should().BeNull();
    }

    [Fact]
    public async Task Conventions_RoundTripAsText()
    {
        await new UpdateBranchConventionsHandler(_conventions)
            .HandleAsync(new UpdateBranchConventions("Bug = fix/{id}"), Ct);

        var text = await new GetBranchConventionsHandler(_conventions).HandleAsync(new GetBranchConventions(), Ct);

        text.Should().Be("Bug = fix/{id}");
    }

    [Fact]
    public async Task Conventions_BlankText_GoesBackToTheDefaults()
    {
        _conventions.Conventions = BranchConventions.Parse("Bug = fix/{id}");

        await new UpdateBranchConventionsHandler(_conventions).HandleAsync(new UpdateBranchConventions("  "), Ct);

        _conventions.Conventions.Should().Be(BranchConventions.Default);
    }

    [Fact]
    public async Task Conventions_AnInvalidLine_IsRefusedBeforeSaving()
    {
        var update = () => new UpdateBranchConventionsHandler(_conventions)
            .HandleAsync(new UpdateBranchConventions("Bug = fix/"), Ct);

        await update.Should().ThrowAsync<DomainException>();
        _conventions.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task SearchHandler_GoesThroughTheSharedCache()
    {
        _jira.Issues.Add(FakeExternalTasks.Issue("GAECO-1234", "Corrigir erro de sincronização"));
        var search = Search();

        await new SearchExternalTasksHandler(search).HandleAsync(new SearchExternalTasks("corrigir erro"), Ct);
        var result = await new SearchExternalTasksHandler(search).HandleAsync(new SearchExternalTasks("corrigir erro"), Ct);

        result.Items.Should().ContainSingle();
        _jira.Searches.Should().ContainSingle();
    }
}
