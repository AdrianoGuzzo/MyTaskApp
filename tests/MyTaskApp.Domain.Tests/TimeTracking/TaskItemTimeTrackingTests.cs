using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.TimeTracking;

/// <summary>O que a tarefa diz sobre ser cronometrada (ADR-052).</summary>
public class TaskItemTimeTrackingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 14, 0, 0, TimeSpan.FromHours(-3));

    private static (TaskItem Task, Guid OccurrenceId) NewTask()
    {
        var task = TaskItem.Create("Implementar autenticação", Now);
        return (task, task.Occurrences.Single().Id);
    }

    [Fact]
    public void APendingTask_CanBeTimed()
    {
        var (task, occurrenceId) = NewTask();

        var ensure = () => task.EnsureTimerCanStart(occurrenceId);

        ensure.Should().NotThrow();
    }

    [Fact]
    public void ACompletedTask_CannotBeTimed_ButCanStillBeLogged()
    {
        var (task, occurrenceId) = NewTask();
        task.CompleteOccurrence(occurrenceId, Now);

        var start = () => task.EnsureTimerCanStart(occurrenceId);
        var log = () => task.EnsureTimeCanBeLogged(occurrenceId);

        start.Should().Throw<DomainException>().WithMessage("*pendente*");
        log.Should().NotThrow("esquecer de iniciar o cronômetro é justamente o caso do lançamento manual");
    }

    [Fact]
    public void AnArchivedTask_IsReadOnly()
    {
        var (task, occurrenceId) = NewTask();
        task.Archive(Now);

        var start = () => task.EnsureTimerCanStart(occurrenceId);
        var log = () => task.EnsureTimeCanBeLogged(occurrenceId);

        start.Should().Throw<DomainException>().WithMessage("*arquivado*");
        log.Should().Throw<DomainException>().WithMessage("*arquivado*");
    }

    [Fact]
    public void ATrashedTask_IsReadOnly()
    {
        var (task, occurrenceId) = NewTask();
        task.MoveToTrash(Now, "adriano");

        var log = () => task.EnsureTimeCanBeLogged(occurrenceId);

        log.Should().Throw<DomainException>().WithMessage("*lixeira*");
    }

    [Fact]
    public void AnUnknownOccurrence_IsRefused()
    {
        var (task, _) = NewTask();

        var start = () => task.EnsureTimerCanStart(Guid.CreateVersion7());

        start.Should().Throw<DomainException>();
    }
}
