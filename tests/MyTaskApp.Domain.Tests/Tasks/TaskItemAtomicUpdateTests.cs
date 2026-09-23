using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Tasks;

/// <summary>
/// Edição precisa ser tudo-ou-nada: uma alteração recusada não pode deixar a
/// entidade meio-modificada, senão um SaveChanges posterior persiste o meio-termo.
/// </summary>
public class TaskItemAtomicUpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 14, 30, 0, TimeSpan.FromHours(-3));

    private static TaskItem NewTask() =>
        TaskItem.Create("Investigar estoque", Now, "descrição original", TaskPriority.Low);

    [Fact]
    public void Update_AppliesTitleDescriptionAndPriorityTogether()
    {
        var task = NewTask();

        task.Update("Investigar entrada", "nova descrição", TaskPriority.High);

        task.Title.Should().Be("Investigar entrada");
        task.Description.Should().Be("nova descrição");
        task.Priority.Should().Be(TaskPriority.High);
    }

    [Fact]
    public void Update_WithInvalidTitle_DoesNotApplyTheNewDescription()
    {
        var task = NewTask();

        var update = () => task.Update("   ", "descrição nova", TaskPriority.Urgent);

        update.Should().Throw<DomainException>();
        task.Description.Should().Be("descrição original");
        task.Priority.Should().Be(TaskPriority.Low);
    }

    [Fact]
    public void Update_WithTitleBeyondMaximumLength_DoesNotApplyTheNewDescription()
    {
        var task = NewTask();
        var tooLong = new string('t', TaskItem.MaxTitleLength + 1);

        var update = () => task.Update(tooLong, "descrição nova", TaskPriority.Urgent);

        update.Should().Throw<DomainException>();
        task.Title.Should().Be("Investigar estoque");
        task.Description.Should().Be("descrição original");
        task.Priority.Should().Be(TaskPriority.Low);
    }
}
