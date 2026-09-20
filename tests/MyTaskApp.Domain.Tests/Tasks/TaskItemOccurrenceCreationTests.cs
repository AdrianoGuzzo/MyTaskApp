using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Tasks;

/// <summary>
/// ADR-001: toda tarefa — recorrente ou não — é lida e concluída através de
/// ocorrências. Estes testes travam essa invariante na criação.
/// </summary>
public class TaskItemOccurrenceCreationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 14, 30, 0, TimeSpan.FromHours(-3));
    private static readonly DateOnly Date = new(2026, 9, 17);
    private static readonly TimeOnly Time = new(9, 0);

    [Fact]
    public void Create_AlwaysProducesExactlyOneOccurrence()
    {
        var task = TaskItem.Create("Daily", Now);

        task.Occurrences.Should().ContainSingle();
    }

    [Fact]
    public void Create_WithoutSchedule_LeavesTheOccurrenceUndated()
    {
        // É o caso do Inbox: registrei agora, decido quando depois.
        var task = TaskItem.Create("Comprar HD externo", Now);

        var occurrence = task.Occurrences.Single();
        occurrence.ScheduledDate.Should().BeNull();
        occurrence.ScheduledTime.Should().BeNull();
    }

    [Fact]
    public void Create_WithDateOnlySchedule_ProducesOccurrenceWithoutTime()
    {
        var task = TaskItem.Create("Organizar documentação", Now, schedule: TaskSchedule.On(Date));

        var occurrence = task.Occurrences.Single();
        occurrence.ScheduledDate.Should().Be(Date);
        occurrence.ScheduledTime.Should().BeNull();
    }

    [Fact]
    public void Create_WithDateAndTime_CarriesBothToTheOccurrence()
    {
        var task = TaskItem.Create("Daily", Now, schedule: TaskSchedule.At(Date, Time));

        var occurrence = task.Occurrences.Single();
        occurrence.ScheduledDate.Should().Be(Date);
        occurrence.ScheduledTime.Should().Be(Time);
    }

    [Fact]
    public void Create_LinksTheOccurrenceBackToItsTask()
    {
        var task = TaskItem.Create("Deploy", Now, schedule: TaskSchedule.On(Date));

        task.Occurrences.Single().TaskItemId.Should().Be(task.Id);
    }

    [Fact]
    public void Create_GivesTheOccurrenceItsOwnIdentity()
    {
        var task = TaskItem.Create("Deploy", Now);

        task.Occurrences.Single().Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Occurrences_CannotBeMutatedThroughTheExposedCollection()
    {
        // Expor a List interna deixaria qualquer chamador furar as invariantes do agregado.
        var task = TaskItem.Create("Deploy", Now);
        var exposed = task.Occurrences as ICollection<TaskOccurrence>;

        var add = () => exposed!.Add(task.Occurrences.Single());

        add.Should().Throw<NotSupportedException>();
        task.Occurrences.Should().ContainSingle();
    }

    [Fact]
    public void CompletingOneOccurrence_DoesNotChangeTheTaskDefinition()
    {
        // A definição da série é estável; quem tem estado é a ocorrência (§34).
        var task = TaskItem.Create("Verificar e-mails", Now, schedule: TaskSchedule.At(Date, Time));

        task.Occurrences.Single().Complete(Now);

        task.Title.Should().Be("Verificar e-mails");
        task.CreatedAt.Should().Be(Now);
    }
}
