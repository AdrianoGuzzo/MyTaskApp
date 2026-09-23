using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Tasks;

public class TaskItemCreationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 14, 30, 0, TimeSpan.FromHours(-3));

    [Fact]
    public void Create_WithValidTitle_StartsPending()
    {
        var task = TaskItem.Create("Revisar documentação", Now);

        task.Title.Should().Be("Revisar documentação");
        task.Occurrences.Single().Status.Should().Be(TaskItemStatus.Pending);
        task.Occurrences.Single().CompletedAt.Should().BeNull();
    }

    [Fact]
    public void Create_WithoutExplicitPriority_DefaultsToNormal()
    {
        var task = TaskItem.Create("Daily", Now);

        task.Priority.Should().Be(TaskPriority.Normal);
    }

    [Fact]
    public void Create_UsesSuppliedInstantAsCreatedAt()
    {
        var task = TaskItem.Create("Deploy", Now);

        task.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public void Create_AssignsNonEmptyIdentity()
    {
        var task = TaskItem.Create("Deploy", Now);

        task.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Create_TrimsSurroundingWhitespaceFromTitle()
    {
        var task = TaskItem.Create("   Comprar HD externo   ", Now);

        task.Title.Should().Be("Comprar HD externo");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Create_WithoutMeaningfulTitle_IsRejected(string? title)
    {
        var create = () => TaskItem.Create(title!, Now);

        create.Should().Throw<DomainException>()
            .WithMessage("*título*");
    }

    [Fact]
    public void Create_WithTitleAtMaximumLength_IsAccepted()
    {
        var title = new string('a', TaskItem.MaxTitleLength);

        var task = TaskItem.Create(title, Now);

        task.Title.Should().HaveLength(TaskItem.MaxTitleLength);
    }

    [Fact]
    public void Create_WithTitleBeyondMaximumLength_IsRejected()
    {
        var title = new string('a', TaskItem.MaxTitleLength + 1);

        var create = () => TaskItem.Create(title, Now);

        create.Should().Throw<DomainException>();
    }

    [Fact]
    public void Create_MeasuresTitleLengthAfterTrimming()
    {
        // Espaços em volta não podem consumir o limite útil do título.
        var title = "  " + new string('a', TaskItem.MaxTitleLength) + "  ";

        var create = () => TaskItem.Create(title, Now);

        create.Should().NotThrow();
    }

    [Fact]
    public void Create_WithoutDescription_LeavesItNull()
    {
        var task = TaskItem.Create("Daily", Now);

        task.Description.Should().BeNull();
    }

    [Fact]
    public void Create_WithBlankDescription_NormalizesToNull()
    {
        var task = TaskItem.Create("Daily", Now, description: "   ");

        task.Description.Should().BeNull();
    }

    [Fact]
    public void Create_WithVeryLongDescription_KeepsItWhole()
    {
        var description = new string('d', 100_000);

        var task = TaskItem.Create("Daily", Now, description: description);

        task.Description.Should().Be(description);
    }

    [Fact]
    public void Create_KeepsSuppliedPriority()
    {
        var task = TaskItem.Create("Corrigir bug em produção", Now, priority: TaskPriority.Urgent);

        task.Priority.Should().Be(TaskPriority.Urgent);
    }
}
