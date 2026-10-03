using MyTaskApp.Domain.External;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Tasks;

public class TaskItemExternalLinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    private static ExternalLink Link(
        string title = "Corrigir erro de sincronização",
        string? status = "Em andamento",
        string id = "GAECO-1234") =>
        ExternalLink.Create(
            "Jira",
            id,
            title,
            $"https://empresa.atlassian.net/browse/{id}",
            "Bug",
            status,
            Now);

    [Fact]
    public void ANewTask_IsNotLinked()
    {
        TaskItem.Create("Comprar pão", Now).External.Should().BeNull();
    }

    [Fact]
    public void LinkExternal_KeepsTheSnapshot()
    {
        var task = TaskItem.Create("Corrigir erro de sincronização", Now);
        var link = Link();

        task.LinkExternal(link);

        task.External.Should().Be(link);
    }

    [Fact]
    public void LinkExternal_ReplacesAPreviousLink()
    {
        var task = TaskItem.Create("Corrigir erro", Now);
        task.LinkExternal(Link(id: "GAECO-1"));

        task.LinkExternal(Link(id: "GAECO-2"));

        task.External!.Id.Should().Be("GAECO-2");
    }

    [Fact]
    public void UnlinkExternal_KeepsTheTask_AndForgetsTheLink()
    {
        var task = TaskItem.Create("Corrigir erro", Now);
        task.LinkExternal(Link());

        task.UnlinkExternal();

        task.External.Should().BeNull();
        task.Title.Should().Be("Corrigir erro");
    }

    [Fact]
    public void LinkExternal_OnArchivedChecklist_IsRejected()
    {
        var task = TaskItem.Create("Corrigir erro", Now);
        task.Archive(Now);

        var link = () => task.LinkExternal(Link());

        link.Should().Throw<DomainException>();
        task.External.Should().BeNull();
    }

    [Fact]
    public void RefreshExternal_UpdatesTheSnapshot()
    {
        var task = TaskItem.Create("Corrigir erro de sincronização", Now);
        task.LinkExternal(Link(status: "A fazer"));

        task.RefreshExternal(Link(status: "Em revisão"));

        task.External!.Status.Should().Be("Em revisão");
    }

    [Fact]
    public void RefreshExternal_FollowsTheNewTitle_WhenTheUserNeverRenamedTheTask()
    {
        var task = TaskItem.Create("Corrigir erro de sincronização", Now);
        task.LinkExternal(Link());

        task.RefreshExternal(Link(title: "Corrigir erro de sincronização no login"));

        task.Title.Should().Be("Corrigir erro de sincronização no login");
    }

    [Fact]
    public void RefreshExternal_KeepsATitleTheUserChose()
    {
        // O título local é do usuário: o Jira atualiza o retrato, não a escolha dele.
        var task = TaskItem.Create("Corrigir erro de sincronização", Now);
        task.LinkExternal(Link());
        task.Rename("Sync: olhar o retry primeiro");

        task.RefreshExternal(Link(title: "Corrigir erro de sincronização no login"));

        task.Title.Should().Be("Sync: olhar o retry primeiro");
        task.External!.Title.Should().Be("Corrigir erro de sincronização no login");
    }

    [Fact]
    public void RefreshExternal_WithAnotherIssue_IsRejected()
    {
        var task = TaskItem.Create("Corrigir erro", Now);
        task.LinkExternal(Link(id: "GAECO-1"));

        var refresh = () => task.RefreshExternal(Link(id: "GAECO-2"));

        refresh.Should().Throw<DomainException>();
        task.External!.Id.Should().Be("GAECO-1");
    }

    [Fact]
    public void RefreshExternal_WithoutALink_IsRejected()
    {
        var task = TaskItem.Create("Corrigir erro", Now);

        var refresh = () => task.RefreshExternal(Link());

        refresh.Should().Throw<DomainException>();
    }

    [Fact]
    public void TitleFor_CutsAnIssueSummaryToTheTaskLimit()
    {
        var summary = new string('a', TaskItem.MaxTitleLength + 30);

        ExternalLink.TaskTitleFor(summary).Should().HaveLength(TaskItem.MaxTitleLength);
    }
}
