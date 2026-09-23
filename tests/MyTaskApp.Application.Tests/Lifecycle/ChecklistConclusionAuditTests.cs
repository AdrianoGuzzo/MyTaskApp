using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Lifecycle;

/// <summary>
/// "Checklist criado", "concluído" e "reaberto" na trilha (§8). O que se afirma
/// aqui é que a auditoria segue a <b>mudança de estado da série</b>, e não o
/// clique: é essa distinção que impede a trilha de encher de eventos que não
/// aconteceram.
/// </summary>
public class ChecklistConclusionAuditTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 9, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();
    private readonly FakeTaskAuditLog _audit = new();
    private readonly FakeCurrentUser _user = new("adriano");
    private readonly FakeReminderSettingsStore _settings = new();
    private readonly FakeTimeProvider _time = new(Now);

    private CreateTaskHandler Create() =>
        new(
            _repository,
            _repository,
            _settings,
            _audit,
            _user,
            TestClock.Over(_time),
            _time,
            NullLogger<CreateTaskHandler>.Instance);

    private CompleteOccurrenceHandler Complete() =>
        new(
            _repository,
            _repository,
            _audit,
            _user,
            _time,
            NullLogger<CompleteOccurrenceHandler>.Instance);

    private ReopenOccurrenceHandler Reopen() =>
        new(
            _repository,
            _repository,
            _audit,
            _user,
            TestClock.Over(_time),
            _time,
            NullLogger<ReopenOccurrenceHandler>.Instance);

    private QuickCaptureHandler Capture() =>
        new(
            _repository,
            new FakeTagRepository(),
            _repository,
            _settings,
            _audit,
            _user,
            TestClock.Over(_time),
            _time,
            NullLogger<QuickCaptureHandler>.Instance);

    [Fact]
    public async Task CreatingAChecklist_IsRecorded()
    {
        await Create().HandleAsync(new CreateTask("Fechar o mês"), Ct);

        _audit.Last!.Operation.Should().Be(TaskAuditOperation.Created);
        _audit.Last.TaskTitle.Should().Be("Fechar o mês");
        _audit.Last.ActorName.Should().Be("adriano");
    }

    [Fact]
    public async Task AQuickCapture_RecordsOneCreationPerLine()
    {
        await Capture().HandleAsync(new QuickCapture("comprar pão\nligar pro dentista"), Ct);

        _audit.Operations.Should().Equal(
            TaskAuditOperation.Created,
            TaskAuditOperation.Created);
    }

    [Fact]
    public async Task ConcludingAChecklist_IsRecorded()
    {
        var task = TaskItem.Create("Fechar o mês", Now);
        _repository.Seed(task);

        await Complete().HandleAsync(
            new CompleteOccurrence(task.Occurrences.Single().Id),
            Ct);

        _audit.Last!.Operation.Should().Be(TaskAuditOperation.Completed);
        _audit.Last.OccurredAt.Should().Be(Now);
    }

    [Fact]
    public async Task ReopeningAConcludedChecklist_IsRecorded()
    {
        var task = TaskItem.Create("Fechar o mês", Now);
        _repository.Seed(task);

        var occurrenceId = task.Occurrences.Single().Id;

        await Complete().HandleAsync(new CompleteOccurrence(occurrenceId), Ct);
        await Reopen().HandleAsync(new ReopenOccurrence(occurrenceId), Ct);

        _audit.Operations.Should().Equal(
            TaskAuditOperation.Completed,
            TaskAuditOperation.Reopened);
    }

    [Fact]
    public async Task CompletingSomethingArchived_IsRefusedAndRecordsNothing()
    {
        var task = TaskItem.Create("Fechar o mês", Now);
        task.Archive(Now);
        _repository.Seed(task);

        var complete = async () => await Complete().HandleAsync(
            new CompleteOccurrence(task.Occurrences.Single().Id),
            Ct);

        await complete.Should().ThrowAsync<MyTaskApp.Domain.DomainException>();
        _audit.Entries.Should().BeEmpty();
        _repository.SaveCount.Should().Be(0);
    }
}
