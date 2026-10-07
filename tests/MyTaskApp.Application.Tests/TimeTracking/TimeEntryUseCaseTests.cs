using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.Tests.TimeTracking;

/// <summary>Lançar, corrigir e excluir períodos à mão, e o histórico da aba "Tempo" (ADR-052).</summary>
public class TimeEntryUseCaseTests
{
    // Terça, 06/10/2026, 18:00 em São Paulo.
    private static readonly DateTimeOffset Evening = new(2026, 10, 6, 21, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(Evening);
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeTimeEntryRepository _entries = new();
    private readonly FakeTaskAuditLog _audit = new();
    private readonly FakeCurrentUser _user = new("adriano");

    private AddTimeEntryHandler Add() =>
        new(_tasks, _entries, _tasks, _audit, _user, TestClock.Over(_time), _time, NullLogger<AddTimeEntryHandler>.Instance);

    private UpdateTimeEntryHandler Update() =>
        new(_tasks, _entries, _tasks, _audit, _user, TestClock.Over(_time), _time, NullLogger<UpdateTimeEntryHandler>.Instance);

    private DeleteTimeEntryHandler Delete() =>
        new(_tasks, _entries, _tasks, _audit, _user, TestClock.Over(_time), _time, NullLogger<DeleteTimeEntryHandler>.Instance);

    private GetTaskTimeLogHandler Log() => new(_tasks, _entries, TestClock.Over(_time), _time);

    private (TaskItem Task, Guid OccurrenceId) Seed(string title = "Implementar autenticação")
    {
        var task = TaskItem.Create(title, Evening.AddDays(-3));
        _tasks.Seed(task);
        return (task, task.Occurrences.Single().Id);
    }

    private static AddTimeEntry Manual(Guid occurrenceId, int fromHour, int fromMinute, int toHour, int toMinute, string? note = null, DateOnly? day = null) =>
        new(occurrenceId, day ?? Today, new TimeOnly(fromHour, fromMinute), day ?? Today, new TimeOnly(toHour, toMinute), note);

    [Fact]
    public async Task Add_StoresTheWallClockPeriod_AsInstants_AndAudits()
    {
        var (task, occurrenceId) = Seed();

        await Add().HandleAsync(Manual(occurrenceId, 14, 0, 15, 30, "Corrigi problema na API"), Ct);

        var entry = _entries.Entries.Single();
        entry.Source.Should().Be(TimeEntrySource.Manual);
        entry.StartedAt.Should().Be(new DateTimeOffset(2026, 10, 6, 14, 0, 0, TimeSpan.FromHours(-3)));
        entry.Duration(Evening).Should().Be(TimeSpan.FromMinutes(90));
        entry.Note.Should().Be("Corrigi problema na API");
        _tasks.SaveCount.Should().Be(1);

        _audit.Last!.Operation.Should().Be(TaskAuditOperation.TimeEntryAdded);
        _audit.Last.TaskId.Should().Be(task.Id);
        _audit.Last.ActorName.Should().Be("adriano");
        _audit.Last.Details.Should().Be("06/10 14:00 → 15:30 (1h 30min) · Manual");
    }

    [Fact]
    public async Task Add_AcrossMidnight_UsesTheEndDate()
    {
        var (_, occurrenceId) = Seed();

        await Add().HandleAsync(
            new AddTimeEntry(occurrenceId, Today.AddDays(-1), new TimeOnly(23, 0), Today, new TimeOnly(1, 30), null),
            Ct);

        _entries.Entries.Single().Duration(Evening).Should().Be(TimeSpan.FromMinutes(150));
    }

    [Theory]
    [InlineData(15, 30, 14, 0)]
    [InlineData(14, 0, 14, 0)]
    public async Task Add_WithTheEndNotAfterTheStart_IsRefused_AndNothingIsSaved(int fromHour, int fromMinute, int toHour, int toMinute)
    {
        var (_, occurrenceId) = Seed();

        var add = () => Add().HandleAsync(Manual(occurrenceId, fromHour, fromMinute, toHour, toMinute), Ct);

        await add.Should().ThrowAsync<DomainException>()
            .WithMessage("O horário final deve ser posterior ao horário inicial.");
        _entries.Entries.Should().BeEmpty();
        _audit.Entries.Should().BeEmpty();
        _tasks.SaveCount.Should().Be(0);
    }

    [Theory]
    [InlineData(14, 45, 16, 0)]
    [InlineData(13, 0, 14, 30)]
    [InlineData(13, 0, 16, 0)]
    [InlineData(14, 30, 15, 0)]
    public async Task Add_OverlappingAnotherPeriodOfTheTask_IsRefused(int fromHour, int fromMinute, int toHour, int toMinute)
    {
        var (_, occurrenceId) = Seed();
        await Add().HandleAsync(Manual(occurrenceId, 14, 0, 15, 30), Ct);

        var add = () => Add().HandleAsync(Manual(occurrenceId, fromHour, fromMinute, toHour, toMinute), Ct);

        await add.Should().ThrowAsync<DomainException>().WithMessage("*sobrepõe*06/10 14:00 → 15:30*");
        _entries.Entries.Should().ContainSingle();
        _tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Add_OnAnotherTask_AtTheSameTime_IsAllowed()
    {
        var (_, first) = Seed();
        var (_, second) = Seed("Corrigir dashboard");
        await Add().HandleAsync(Manual(first, 14, 0, 15, 30), Ct);

        await Add().HandleAsync(Manual(second, 14, 0, 15, 30), Ct);

        _entries.Entries.Should().HaveCount(2, "a regra de sobreposição é por tarefa");
    }

    [Fact]
    public async Task Add_OverlappingTheRunningTimer_IsRefused()
    {
        var (_, occurrenceId) = Seed();
        _entries.Seed(TimeEntry.StartTimer(occurrenceId, Evening.AddHours(-1))); // desde 17:00

        var add = () => Add().HandleAsync(Manual(occurrenceId, 16, 30, 17, 30), Ct);

        await add.Should().ThrowAsync<DomainException>().WithMessage("*sobrepõe*");
    }

    [Fact]
    public async Task Add_OnACompletedTask_IsAllowed_ButNotOnAnArchivedOne()
    {
        var (completed, completedOccurrence) = Seed();
        completed.CompleteOccurrence(completedOccurrence, Evening);
        var (archived, archivedOccurrence) = Seed("Guardada");
        archived.Archive(Evening);

        await Add().HandleAsync(Manual(completedOccurrence, 14, 0, 15, 0), Ct);
        var addToArchived = () => Add().HandleAsync(Manual(archivedOccurrence, 14, 0, 15, 0), Ct);

        await addToArchived.Should().ThrowAsync<DomainException>().WithMessage("*arquivado*");
        _entries.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Update_RewritesThePeriod_KeepsTheSource_AndAuditsBeforeAndAfter()
    {
        var (_, occurrenceId) = Seed();
        var timer = TimeEntry.StartTimer(occurrenceId, new DateTimeOffset(2026, 10, 6, 14, 0, 0, TimeSpan.FromHours(-3)));
        timer.Stop(new DateTimeOffset(2026, 10, 6, 15, 30, 0, TimeSpan.FromHours(-3)));
        _entries.Seed(timer);

        await Update().HandleAsync(
            new UpdateTimeEntry(timer.Id, Today, new TimeOnly(14, 10), Today, new TimeOnly(15, 45), "revisado"),
            Ct);

        timer.Duration(Evening).Should().Be(TimeSpan.FromMinutes(95));
        timer.Source.Should().Be(TimeEntrySource.Timer);
        timer.UpdatedAt.Should().Be(Evening);
        _audit.Last!.Operation.Should().Be(TaskAuditOperation.TimeEntryChanged);
        _audit.Last.Details.Should().Be(
            "06/10 14:00 → 15:30 (1h 30min) · Timer → 06/10 14:10 → 15:45 (1h 35min) · Timer");
        _tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Update_DoesNotOverlapItself_ButDoesOverlapItsSiblings()
    {
        var (_, occurrenceId) = Seed();
        await Add().HandleAsync(Manual(occurrenceId, 14, 0, 15, 0), Ct);
        await Add().HandleAsync(Manual(occurrenceId, 16, 0, 17, 0), Ct);
        var first = _entries.Entries.MinBy(entry => entry.StartedAt)!;

        await Update().HandleAsync(new UpdateTimeEntry(first.Id, Today, new TimeOnly(13, 30), Today, new TimeOnly(15, 30), null), Ct);
        var overlap = () => Update().HandleAsync(
            new UpdateTimeEntry(first.Id, Today, new TimeOnly(13, 30), Today, new TimeOnly(16, 30), null), Ct);

        await overlap.Should().ThrowAsync<DomainException>().WithMessage("*sobrepõe*16:00 → 17:00*");
        first.EndedAt.Should().Be(new DateTimeOffset(2026, 10, 6, 15, 30, 0, TimeSpan.FromHours(-3)),
            "uma edição recusada não deixa o período meio editado");
    }

    [Fact]
    public async Task Update_OfTheRunningTimer_IsRefused()
    {
        var (_, occurrenceId) = Seed();
        var running = TimeEntry.StartTimer(occurrenceId, Evening.AddHours(-1));
        _entries.Seed(running);

        var update = () => Update().HandleAsync(
            new UpdateTimeEntry(running.Id, Today, new TimeOnly(14, 0), Today, new TimeOnly(15, 0), null), Ct);

        await update.Should().ThrowAsync<DomainException>().WithMessage("*Pare o cronômetro*");
        running.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Update_OfAnUnknownPeriod_IsABusinessFailure()
    {
        var update = () => Update().HandleAsync(
            new UpdateTimeEntry(Guid.CreateVersion7(), Today, new TimeOnly(14, 0), Today, new TimeOnly(15, 0), null), Ct);

        await update.Should().ThrowAsync<DomainException>().WithMessage("Período não encontrado.");
    }

    [Fact]
    public async Task Delete_RemovesThePeriod_AndAudits()
    {
        var (task, occurrenceId) = Seed();
        await Add().HandleAsync(Manual(occurrenceId, 14, 0, 15, 30), Ct);
        var entry = _entries.Entries.Single();

        await Delete().HandleAsync(new DeleteTimeEntry(entry.Id), Ct);

        _entries.Entries.Should().BeEmpty();
        _audit.Last!.Operation.Should().Be(TaskAuditOperation.TimeEntryDeleted);
        _audit.Last.TaskId.Should().Be(task.Id);
        _audit.Last.Details.Should().Be("06/10 14:00 → 15:30 (1h 30min) · Manual");
    }

    [Fact]
    public async Task Delete_OfTheRunningTimer_DiscardsIt()
    {
        var (_, occurrenceId) = Seed();
        var running = TimeEntry.StartTimer(occurrenceId, Evening.AddMinutes(-10));
        _entries.Seed(running);

        await Delete().HandleAsync(new DeleteTimeEntry(running.Id), Ct);

        _entries.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task TheLog_GroupsByLocalDay_NewestDayFirst_WithTotalsAndTheEstimate()
    {
        var (task, occurrenceId) = Seed();
        task.ChangePlan(null, TimeSpan.FromHours(6));
        await Add().HandleAsync(Manual(occurrenceId, 9, 20, 10, 35, day: Today.AddDays(-1)), Ct);
        await Add().HandleAsync(Manual(occurrenceId, 16, 10, 16, 52), Ct);
        await Add().HandleAsync(Manual(occurrenceId, 14, 0, 15, 30, "Corrigi problema na API"), Ct);
        _entries.Seed(TimeEntry.StartTimer(occurrenceId, Evening.AddMinutes(-27)));

        var log = await Log().HandleAsync(new GetTaskTimeLog(occurrenceId), Ct);

        log.TaskId.Should().Be(task.Id);
        log.Logged.Should().Be(TimeSpan.FromMinutes(90 + 42 + 75));
        log.RunningSince.Should().Be(Evening.AddMinutes(-27));
        log.IsRunning.Should().BeTrue();
        log.SpentAt(Evening).Should().Be(TimeSpan.FromMinutes(90 + 42 + 75 + 27));
        log.Estimate.Should().Be(TimeSpan.FromHours(6));

        log.Days.Select(day => day.Label).Should().Equal("Hoje", "Ontem");
        var today = log.Days[0];
        today.Total.Should().Be(TimeSpan.FromMinutes(132), "o que corre não entra no total do dia");
        today.Entries.Select(entry => entry.RangeLabel).Should().Equal("14:00 → 15:30", "16:10 → 16:52", "17:33 → agora");
        today.Entries[0].DurationLabel.Should().Be("1h 30min");
        today.Entries[0].SourceLabel.Should().Be("Manual");
        today.Entries[0].Note.Should().Be("Corrigi problema na API");
        today.Entries[0].StartTime.Should().Be(new TimeOnly(14, 0));
        today.Entries[0].EndDate.Should().Be(Today);
        today.Entries[2].IsActive.Should().BeTrue();
        today.Entries[2].SourceLabel.Should().Be("Timer");
    }

    [Fact]
    public async Task TheLog_OfAnOlderDay_UsesTheDate()
    {
        var (_, occurrenceId) = Seed();
        await Add().HandleAsync(Manual(occurrenceId, 9, 20, 10, 35, day: new DateOnly(2026, 10, 3)), Ct);

        var log = await Log().HandleAsync(new GetTaskTimeLog(occurrenceId), Ct);

        log.Days.Single().Label.Should().Be("03/10/2026");
        log.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task TheLog_OfAnUntrackedTask_IsEmpty()
    {
        var (_, occurrenceId) = Seed();

        var log = await Log().HandleAsync(new GetTaskTimeLog(occurrenceId), Ct);

        log.IsEmpty.Should().BeTrue();
        log.Logged.Should().Be(TimeSpan.Zero);
    }
}
