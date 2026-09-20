using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Tasks;

public class CompleteOccurrenceHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 17, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private CompleteOccurrenceHandler Handler() =>
        new(_repository, _repository, _clock, NullLogger<CompleteOccurrenceHandler>.Instance);

    private TaskOccurrence SeedPendingOccurrence()
    {
        var task = TaskItem.Create("Revisar PR", Now);
        _repository.Seed(task);
        return task.Occurrences.Single();
    }

    [Fact]
    public async Task Handle_MarksTheOccurrenceCompleted()
    {
        var occurrence = SeedPendingOccurrence();

        await Handler().HandleAsync(new CompleteOccurrence(occurrence.Id), Ct);

        occurrence.Status.Should().Be(TaskItemStatus.Completed);
    }

    [Fact]
    public async Task Handle_StampsCompletionWithTheInjectedClock()
    {
        var occurrence = SeedPendingOccurrence();

        await Handler().HandleAsync(new CompleteOccurrence(occurrence.Id), Ct);

        occurrence.CompletedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Handle_SavesExactlyOnce()
    {
        var occurrence = SeedPendingOccurrence();

        await Handler().HandleAsync(new CompleteOccurrence(occurrence.Id), Ct);

        _repository.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_WithUnknownOccurrence_ReportsItAsABusinessFailure()
    {
        // Nunca pode virar NullReferenceException na cara do usuário (§25, §28).
        var handle = async () =>
            await Handler().HandleAsync(new CompleteOccurrence(Guid.CreateVersion7()), Ct);

        await handle.Should().ThrowAsync<DomainException>().WithMessage("*não encontrada*");
        _repository.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_OnAlreadyCompletedOccurrence_DoesNotSaveAgain()
    {
        var occurrence = SeedPendingOccurrence();
        occurrence.Complete(Now);

        var handle = async () => await Handler().HandleAsync(new CompleteOccurrence(occurrence.Id), Ct);

        await handle.Should().ThrowAsync<DomainException>();
        _repository.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_CompletingOneOccurrence_DoesNotTouchTheOthers()
    {
        // Base do histórico de recorrentes (§34): concluir 17/09 não mexe em 18/09.
        var task = TaskItem.Create("Verificar e-mails", Now);
        _repository.Seed(task);
        var first = task.Occurrences.Single();

        await Handler().HandleAsync(new CompleteOccurrence(first.Id), Ct);

        task.Title.Should().Be("Verificar e-mails");
        first.Status.Should().Be(TaskItemStatus.Completed);
    }
}

public class ReopenAndCancelOccurrenceHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 17, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();

    private TaskOccurrence SeedPendingOccurrence()
    {
        var task = TaskItem.Create("Revisar PR", Now);
        _repository.Seed(task);
        return task.Occurrences.Single();
    }

    [Fact]
    public async Task Reopen_ReturnsACompletedOccurrenceToPending()
    {
        var occurrence = SeedPendingOccurrence();
        occurrence.Complete(Now);
        var timeProvider = new FakeTimeProvider(Now);
        var handler = new ReopenOccurrenceHandler(
            _repository,
            _repository,
            TestClock.Over(timeProvider),
            timeProvider,
            NullLogger<ReopenOccurrenceHandler>.Instance);

        await handler.HandleAsync(new ReopenOccurrence(occurrence.Id), Ct);

        occurrence.Status.Should().Be(TaskItemStatus.Pending);
        occurrence.CompletedAt.Should().BeNull();
        _repository.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Cancel_MovesAPendingOccurrenceToCancelled()
    {
        var occurrence = SeedPendingOccurrence();
        var handler = new CancelOccurrenceHandler(
            _repository, _repository, NullLogger<CancelOccurrenceHandler>.Instance);

        await handler.HandleAsync(new CancelOccurrence(occurrence.Id), Ct);

        occurrence.Status.Should().Be(TaskItemStatus.Cancelled);
        _repository.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Cancel_WithUnknownOccurrence_ReportsItAsABusinessFailure()
    {
        var handler = new CancelOccurrenceHandler(
            _repository, _repository, NullLogger<CancelOccurrenceHandler>.Instance);

        var handle = async () => await handler.HandleAsync(new CancelOccurrence(Guid.CreateVersion7()), Ct);

        await handle.Should().ThrowAsync<DomainException>();
    }
}
