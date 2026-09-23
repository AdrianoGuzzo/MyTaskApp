using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Tasks;

public class TaskItemTagsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    private static readonly Guid Urgent = Guid.NewGuid();
    private static readonly Guid Finance = Guid.NewGuid();

    [Fact]
    public void SetTags_LinksEachTagOnce()
    {
        var task = TaskItem.Create("Pagar boleto", Now);

        task.SetTags([Urgent, Finance, Urgent]);

        task.Tags.Select(link => link.TagId).Should().BeEquivalentTo([Urgent, Finance]);
        task.Tags.Should().OnlyContain(link => link.TaskItemId == task.Id);
    }

    [Fact]
    public void SetTags_IsTheFinalSet_RemovingWhatIsMissing()
    {
        var task = TaskItem.Create("Pagar boleto", Now);
        task.SetTags([Urgent, Finance]);

        task.SetTags([Finance]);

        task.Tags.Select(link => link.TagId).Should().Equal(Finance);
    }

    [Fact]
    public void SetTags_KeepsTheLinksThatStay()
    {
        var task = TaskItem.Create("Pagar boleto", Now);
        task.SetTags([Urgent]);
        var kept = task.Tags.Single();

        task.SetTags([Urgent, Finance]);

        task.Tags.Should().Contain(kept);
    }

    [Fact]
    public void SetTags_OnArchivedChecklist_IsRejected()
    {
        var task = TaskItem.Create("Pagar boleto", Now);
        task.Archive(Now);

        var tag = () => task.SetTags([Urgent]);

        tag.Should().Throw<DomainException>();
        task.Tags.Should().BeEmpty();
    }

    [Fact]
    public void SetTags_OnTrashedChecklist_IsRejected()
    {
        var task = TaskItem.Create("Pagar boleto", Now);
        task.MoveToTrash(Now, deletedBy: null);

        var tag = () => task.SetTags([Urgent]);

        tag.Should().Throw<DomainException>();
    }
}
