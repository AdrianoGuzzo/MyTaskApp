using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Tasks;

public class CreateTaskHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 17, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeReminderSettingsStore _settings = new();

    private CreateTaskHandler Handler() =>
        new(
            _repository,
            _repository,
            _settings,
            TestClock.Over(_clock),
            _clock,
            NullLogger<CreateTaskHandler>.Instance);

    [Fact]
    public async Task Handle_PersistsTheTask()
    {
        await Handler().HandleAsync(new CreateTask("Comprar HD externo"), Ct);

        _repository.Tasks.Should().ContainSingle()
            .Which.Title.Should().Be("Comprar HD externo");
    }

    [Fact]
    public async Task Handle_SavesExactlyOnce()
    {
        await Handler().HandleAsync(new CreateTask("Daily"), Ct);

        _repository.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_ReturnsTheIdentifiersOfWhatWasCreated()
    {
        var result = await Handler().HandleAsync(new CreateTask("Daily"), Ct);

        var created = _repository.Tasks.Single();
        result.TaskId.Should().Be(created.Id);
        result.OccurrenceId.Should().Be(created.Occurrences.Single().Id);
    }

    [Fact]
    public async Task Handle_StampsCreationWithTheInjectedClock()
    {
        await Handler().HandleAsync(new CreateTask("Daily"), Ct);

        _repository.Tasks.Single().CreatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Handle_CarriesScheduleToTheOccurrence()
    {
        var command = new CreateTask(
            "Deploy",
            ScheduledDate: new DateOnly(2026, 9, 17),
            ScheduledTime: new TimeOnly(15, 30));

        await Handler().HandleAsync(command, Ct);

        var occurrence = _repository.Tasks.Single().Occurrences.Single();
        occurrence.ScheduledDate.Should().Be(new DateOnly(2026, 9, 17));
        occurrence.ScheduledTime.Should().Be(new TimeOnly(15, 30));
    }

    [Fact]
    public async Task Handle_WithoutSchedule_CreatesAnUndatedTask()
    {
        // Captura rápida no Inbox: registra agora, decide quando depois (§14).
        await Handler().HandleAsync(new CreateTask("Estudar OpenTelemetry"), Ct);

        _repository.Tasks.Single().Occurrences.Single().ScheduledDate.Should().BeNull();
    }

    [Fact]
    public async Task Handle_KeepsTheRequestedPriority()
    {
        await Handler().HandleAsync(new CreateTask("Corrigir bug", Priority: TaskPriority.Urgent), Ct);

        _repository.Tasks.Single().Priority.Should().Be(TaskPriority.Urgent);
    }

    [Fact]
    public async Task Handle_WithBlankTitle_PersistsNothing()
    {
        var handle = async () => await Handler().HandleAsync(new CreateTask("   "), Ct);

        await handle.Should().ThrowAsync<DomainException>();
        _repository.Tasks.Should().BeEmpty();
        _repository.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_WithTimeButNoDate_PersistsNothing()
    {
        var command = new CreateTask("Deploy", ScheduledTime: new TimeOnly(15, 30));

        var handle = async () => await Handler().HandleAsync(command, Ct);

        await handle.Should().ThrowAsync<DomainException>().WithMessage("*sem data*");
        _repository.Tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenSavingFails_LetsTheFailureSurface()
    {
        // A UI precisa saber que não gravou para oferecer "Tentar novamente" (§25).
        _repository.SaveFailure = new InvalidOperationException("banco indisponível");

        var handle = async () => await Handler().HandleAsync(new CreateTask("Daily"), Ct);

        await handle.Should().ThrowAsync<InvalidOperationException>();
    }
}
