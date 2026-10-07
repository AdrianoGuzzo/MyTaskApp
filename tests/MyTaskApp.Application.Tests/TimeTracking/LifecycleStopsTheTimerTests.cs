using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.Tests.TimeTracking;

/// <summary>
/// Concluir, cancelar, arquivar e mandar para a lixeira param o cronômetro da
/// tarefa no mesmo instante e no mesmo SaveChanges (ADR-052).
/// </summary>
public class LifecycleStopsTheTimerTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 6, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Started.AddMinutes(45);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(Later);
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeTimeEntryRepository _entries = new();

    private (TaskItem Task, Guid OccurrenceId, TimeEntry Running) SeedRunning()
    {
        var task = TaskItem.Create("Implementar autenticação", Started.AddDays(-1));
        _tasks.Seed(task);
        var occurrenceId = task.Occurrences.Single().Id;
        var running = TimeEntry.StartTimer(occurrenceId, Started);
        _entries.Seed(running);
        return (task, occurrenceId, running);
    }

    [Fact]
    public async Task Completing_StopsTheTimer_AtTheCompletion()
    {
        var (_, occurrenceId, running) = SeedRunning();

        await new CompleteOccurrenceHandler(
                _tasks, _tasks, _entries, new FakeTaskAuditLog(), new FakeCurrentUser(), _time,
                NullLogger<CompleteOccurrenceHandler>.Instance)
            .HandleAsync(new CompleteOccurrence(occurrenceId), Ct);

        running.EndedAt.Should().Be(Later);
        _tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Cancelling_StopsTheTimer()
    {
        var (_, occurrenceId, running) = SeedRunning();

        await new CancelOccurrenceHandler(
                _tasks, _tasks, _entries, new FakeTaskAuditLog(), new FakeCurrentUser(), _time,
                NullLogger<CancelOccurrenceHandler>.Instance)
            .HandleAsync(new CancelOccurrence(occurrenceId), Ct);

        running.EndedAt.Should().Be(Later);
    }

    [Fact]
    public async Task Archiving_StopsTheTimer()
    {
        var (task, _, running) = SeedRunning();

        await new ArchiveChecklistHandler(
                _tasks, _tasks, _entries, new FakeTaskAuditLog(), new FakeCurrentUser(), _time,
                NullLogger<ArchiveChecklistHandler>.Instance)
            .HandleAsync(new ArchiveChecklist(task.Id), Ct);

        running.EndedAt.Should().Be(Later);
    }

    [Fact]
    public async Task MovingToTrash_StopsTheTimer()
    {
        var (task, _, running) = SeedRunning();

        await new MoveChecklistToTrashHandler(
                _tasks, _tasks, _entries, new FakeTaskAuditLog(), new FakeCurrentUser(), _time,
                NullLogger<MoveChecklistToTrashHandler>.Instance)
            .HandleAsync(new MoveChecklistToTrash(task.Id), Ct);

        running.EndedAt.Should().Be(Later);
    }

    [Fact]
    public async Task CompletingAnotherTask_LeavesTheTimerRunning()
    {
        var (_, _, running) = SeedRunning();
        var other = TaskItem.Create("Corrigir dashboard", Started.AddDays(-1));
        _tasks.Seed(other);

        await new CompleteOccurrenceHandler(
                _tasks, _tasks, _entries, new FakeTaskAuditLog(), new FakeCurrentUser(), _time,
                NullLogger<CompleteOccurrenceHandler>.Instance)
            .HandleAsync(new CompleteOccurrence(other.Occurrences.Single().Id), Ct);

        running.IsActive.Should().BeTrue();
    }
}
