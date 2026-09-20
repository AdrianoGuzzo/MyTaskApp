using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Tasks;

public class TaskOccurrenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 14, 30, 0, TimeSpan.FromHours(-3));
    private static readonly DateTimeOffset Later = Now.AddHours(1);

    private static TaskOccurrence PendingOccurrence() =>
        TaskItem.Create("Fazer backup", Now, schedule: TaskSchedule.At(new DateOnly(2026, 9, 17), new TimeOnly(14, 0)))
            .Occurrences.Single();

    [Fact]
    public void NewOccurrence_IsPendingAndNotCompleted()
    {
        var occurrence = PendingOccurrence();

        occurrence.Status.Should().Be(TaskItemStatus.Pending);
        occurrence.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void Complete_MarksCompletedAndStampsTheInstant()
    {
        var occurrence = PendingOccurrence();

        occurrence.Complete(Later);

        occurrence.Status.Should().Be(TaskItemStatus.Completed);
        occurrence.CompletedAt.Should().Be(Later);
    }

    [Fact]
    public void Complete_OnAlreadyCompletedOccurrence_IsRejected()
    {
        var occurrence = PendingOccurrence();
        occurrence.Complete(Later);

        var completeAgain = () => occurrence.Complete(Later.AddMinutes(5));

        completeAgain.Should().Throw<DomainException>().WithMessage("*já foi concluída*");
    }

    [Fact]
    public void Complete_DoesNotOverwriteTheOriginalCompletionInstant()
    {
        var occurrence = PendingOccurrence();
        occurrence.Complete(Later);

        var completeAgain = () => occurrence.Complete(Later.AddHours(3));

        completeAgain.Should().Throw<DomainException>();
        occurrence.CompletedAt.Should().Be(Later);
    }

    [Fact]
    public void Complete_OnCancelledOccurrence_IsRejected()
    {
        var occurrence = PendingOccurrence();
        occurrence.Cancel();

        var complete = () => occurrence.Complete(Later);

        complete.Should().Throw<DomainException>().WithMessage("*cancelada*");
    }

    [Fact]
    public void Cancel_MovesOccurrenceOutOfThePendingSet()
    {
        var occurrence = PendingOccurrence();

        occurrence.Cancel();

        occurrence.Status.Should().Be(TaskItemStatus.Cancelled);
        occurrence.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void Cancel_OnCompletedOccurrence_IsRejected()
    {
        var occurrence = PendingOccurrence();
        occurrence.Complete(Later);

        var cancel = () => occurrence.Cancel();

        cancel.Should().Throw<DomainException>();
    }

    [Fact]
    public void Reopen_ReturnsCompletedOccurrenceToPendingAndClearsCompletion()
    {
        var occurrence = PendingOccurrence();
        occurrence.Complete(Later);

        occurrence.Reopen();

        occurrence.Status.Should().Be(TaskItemStatus.Pending);
        occurrence.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void Reopen_ReturnsCancelledOccurrenceToPending()
    {
        var occurrence = PendingOccurrence();
        occurrence.Cancel();

        occurrence.Reopen();

        occurrence.Status.Should().Be(TaskItemStatus.Pending);
    }

    [Fact]
    public void Reopen_OnPendingOccurrence_IsRejected()
    {
        var occurrence = PendingOccurrence();

        var reopen = () => occurrence.Reopen();

        reopen.Should().Throw<DomainException>();
    }

    [Fact]
    public void Complete_AfterReopen_IsAllowedAgain()
    {
        var occurrence = PendingOccurrence();
        occurrence.Complete(Later);
        occurrence.Reopen();

        occurrence.Complete(Later.AddDays(1));

        occurrence.Status.Should().Be(TaskItemStatus.Completed);
        occurrence.CompletedAt.Should().Be(Later.AddDays(1));
    }
}
