using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Tasks;

public class TaskItemEditingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 14, 30, 0, TimeSpan.FromHours(-3));

    private static TaskItem NewTask() => TaskItem.Create("Investigar estoque", Now);

    [Fact]
    public void Rename_ReplacesTheTitle()
    {
        var task = NewTask();

        task.Rename("Investigar entrada de estoque");

        task.Title.Should().Be("Investigar entrada de estoque");
    }

    [Fact]
    public void Rename_AppliesTheSameNormalizationAsCreation()
    {
        var task = NewTask();

        task.Rename("   Revisar PR   ");

        task.Title.Should().Be("Revisar PR");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_WithoutMeaningfulTitle_IsRejected(string? title)
    {
        var task = NewTask();

        var rename = () => task.Rename(title!);

        rename.Should().Throw<DomainException>();
        task.Title.Should().Be("Investigar estoque");
    }

    [Fact]
    public void Rename_BeyondMaximumLength_IsRejected()
    {
        var task = NewTask();

        var rename = () => task.Rename(new string('a', TaskItem.MaxTitleLength + 1));

        rename.Should().Throw<DomainException>();
    }

    [Fact]
    public void ChangeDescription_ReplacesTheDescription()
    {
        var task = NewTask();

        task.ChangeDescription("Verificar como a integração processa a entrada.");

        task.Description.Should().Be("Verificar como a integração processa a entrada.");
    }

    [Fact]
    public void ChangeDescription_WithBlankText_ClearsIt()
    {
        var task = NewTask();
        task.ChangeDescription("algo");

        task.ChangeDescription("   ");

        task.Description.Should().BeNull();
    }

    [Fact]
    public void ChangeDescription_BeyondMaximumLength_IsRejected()
    {
        var task = NewTask();

        var change = () => task.ChangeDescription(new string('d', TaskItem.MaxDescriptionLength + 1));

        change.Should().Throw<DomainException>();
    }

    [Fact]
    public void ChangePriority_ReplacesThePriority()
    {
        var task = NewTask();

        task.ChangePriority(TaskPriority.Urgent);

        task.Priority.Should().Be(TaskPriority.Urgent);
    }

    [Fact]
    public void GetOccurrence_ReturnsTheOccurrenceById()
    {
        var task = NewTask();
        var expected = task.Occurrences.Single();

        task.GetOccurrence(expected.Id).Should().BeSameAs(expected);
    }

    [Fact]
    public void GetOccurrence_WithUnknownId_IsRejected()
    {
        var task = NewTask();

        var get = () => task.GetOccurrence(Guid.CreateVersion7());

        get.Should().Throw<DomainException>().WithMessage("*não encontrada*");
    }
}

public class TaskOccurrenceReschedulingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 14, 30, 0, TimeSpan.FromHours(-3));
    private static readonly DateOnly Date = new(2026, 9, 17);
    private static readonly TimeOnly Time = new(9, 0);

    private static TaskOccurrence UndatedOccurrence() =>
        TaskItem.Create("Organizar documentação", Now).Occurrences.Single();

    [Fact]
    public void Reschedule_FromUndatedToDateAndTime()
    {
        var occurrence = UndatedOccurrence();

        occurrence.Reschedule(TaskSchedule.At(Date, Time));

        occurrence.ScheduledDate.Should().Be(Date);
        occurrence.ScheduledTime.Should().Be(Time);
    }

    [Fact]
    public void Reschedule_ToUnscheduled_ClearsDateAndTime()
    {
        var occurrence = UndatedOccurrence();
        occurrence.Reschedule(TaskSchedule.At(Date, Time));

        occurrence.Reschedule(TaskSchedule.Unscheduled);

        occurrence.ScheduledDate.Should().BeNull();
        occurrence.ScheduledTime.Should().BeNull();
    }

    [Fact]
    public void Reschedule_ToDateOnly_DropsThePreviousTime()
    {
        var occurrence = UndatedOccurrence();
        occurrence.Reschedule(TaskSchedule.At(Date, Time));

        occurrence.Reschedule(TaskSchedule.On(Date.AddDays(1)));

        occurrence.ScheduledDate.Should().Be(Date.AddDays(1));
        occurrence.ScheduledTime.Should().BeNull();
    }

    [Fact]
    public void Reschedule_OnCompletedOccurrence_IsRejected()
    {
        // Histórico não se reescreve: para mudar, reabra a ocorrência antes.
        var occurrence = UndatedOccurrence();
        occurrence.Complete(Now);

        var reschedule = () => occurrence.Reschedule(TaskSchedule.On(Date));

        reschedule.Should().Throw<DomainException>();
    }

    [Fact]
    public void Reschedule_OnCancelledOccurrence_IsRejected()
    {
        var occurrence = UndatedOccurrence();
        occurrence.Cancel();

        var reschedule = () => occurrence.Reschedule(TaskSchedule.On(Date));

        reschedule.Should().Throw<DomainException>();
    }
}
