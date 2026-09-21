using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Tasks;

public class UpdateTaskHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 17, 30, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();

    private UpdateTaskHandler Handler() =>
        new(_repository, _repository, NullLogger<UpdateTaskHandler>.Instance);

    private TaskItem SeedTask()
    {
        var task = TaskItem.Create("Investigar estoque", Now);
        _repository.Seed(task);
        return task;
    }

    [Fact]
    public async Task Handle_UpdatesTitleDescriptionAndPriority()
    {
        var task = SeedTask();

        await Handler().HandleAsync(
            new UpdateTask(task.Id, "Investigar entrada de estoque", "Comparar com a API", TaskPriority.High),
            Ct);

        task.Title.Should().Be("Investigar entrada de estoque");
        task.Description.Should().Be("Comparar com a API");
        task.Priority.Should().Be(TaskPriority.High);
        _repository.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_WithUnknownTask_ReportsItAsABusinessFailure()
    {
        var handle = async () => await Handler().HandleAsync(
            new UpdateTask(Guid.CreateVersion7(), "Qualquer", null, TaskPriority.Normal), Ct);

        await handle.Should().ThrowAsync<DomainException>().WithMessage("*não encontrada*");
    }

    [Fact]
    public async Task Handle_WithInvalidTitle_LeavesTheTaskUntouched()
    {
        var task = SeedTask();

        var handle = async () => await Handler().HandleAsync(
            new UpdateTask(task.Id, "   ", null, TaskPriority.Normal), Ct);

        await handle.Should().ThrowAsync<DomainException>();
        task.Title.Should().Be("Investigar estoque");
        _repository.SaveCount.Should().Be(0);
    }
}

public class RescheduleOccurrenceHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 17, 30, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();

    private readonly FakeTimeProvider _timeProvider = new(Now);

    private RescheduleOccurrenceHandler Handler() =>
        new(
            _repository,
            _repository,
            TestClock.Over(_timeProvider),
            _timeProvider,
            NullLogger<RescheduleOccurrenceHandler>.Instance);

    private TaskOccurrence SeedOccurrence()
    {
        var task = TaskItem.Create("Deploy", Now);
        _repository.Seed(task);
        return task.Occurrences.Single();
    }

    [Fact]
    public async Task Handle_MovesTheOccurrenceToTheNewDateAndTime()
    {
        var occurrence = SeedOccurrence();

        await Handler().HandleAsync(
            new RescheduleOccurrence(occurrence.Id, new DateOnly(2026, 9, 18), new TimeOnly(15, 30)), Ct);

        occurrence.ScheduledDate.Should().Be(new DateOnly(2026, 9, 18));
        occurrence.ScheduledTime.Should().Be(new TimeOnly(15, 30));
        _repository.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_WithoutDate_SendsTheTaskBackToTheUndatedPile()
    {
        var occurrence = SeedOccurrence();
        await Handler().HandleAsync(
            new RescheduleOccurrence(occurrence.Id, new DateOnly(2026, 9, 18), null), Ct);

        await Handler().HandleAsync(new RescheduleOccurrence(occurrence.Id, null, null), Ct);

        occurrence.ScheduledDate.Should().BeNull();
    }

    [Fact]
    public async Task Handle_OnCompletedOccurrence_ChangesNothing()
    {
        var occurrence = SeedOccurrence();
        occurrence.Complete(Now);

        var handle = async () => await Handler().HandleAsync(
            new RescheduleOccurrence(occurrence.Id, new DateOnly(2026, 9, 18), null), Ct);

        await handle.Should().ThrowAsync<DomainException>();
        occurrence.ScheduledDate.Should().BeNull();
        _repository.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_WithTimeButNoDate_IsRejected()
    {
        var occurrence = SeedOccurrence();

        var handle = async () => await Handler().HandleAsync(
            new RescheduleOccurrence(occurrence.Id, null, new TimeOnly(9, 0)), Ct);

        await handle.Should().ThrowAsync<DomainException>().WithMessage("*sem data*");
    }
}
