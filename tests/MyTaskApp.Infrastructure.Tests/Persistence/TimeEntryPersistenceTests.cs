using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;
using MyTaskApp.Infrastructure.Persistence.Configurations;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// Os períodos de trabalho contra SQLite de verdade (ADR-052): o que sobrevive ao
/// app fechar, o cronômetro único garantido pelo próprio banco e a soma da lista.
/// </summary>
public class TimeEntryPersistenceTests
{
    // Terça, 06/10/2026, 14:00 em São Paulo.
    private static readonly DateTimeOffset At14 = new(2026, 10, 6, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(TaskItem Task, Guid OccurrenceId)> SeedTaskAsync(
        TempSqliteDatabase db,
        string title = "Implementar autenticação")
    {
        var task = TaskItem.Create(title, At14.AddDays(-1), schedule: TaskSchedule.At(Today, new TimeOnly(9, 0)));

        await using var context = db.CreateContext();
        context.Tasks.Add(task);
        await context.SaveChangesAsync(Ct);

        return (task, task.Occurrences.Single().Id);
    }

    private static async Task AddAsync(TempSqliteDatabase db, params TimeEntry[] entries)
    {
        await using var context = db.CreateContext();
        var repository = new TimeEntryRepository(context);

        foreach (var entry in entries)
        {
            await repository.AddAsync(entry, Ct);
        }

        await context.SaveChangesAsync(Ct);
    }

    private static TimeEntry Closed(Guid occurrenceId, int fromMinutes, int toMinutes, string? note = null) =>
        TimeEntry.Manual(occurrenceId, At14.AddMinutes(fromMinutes), At14.AddMinutes(toMinutes), note, At14.AddHours(8));

    /// <summary>14:00 inicia; o app fecha; outro processo abre e encontra o mesmo início, exato.</summary>
    [Fact]
    public async Task ARunningTimer_SurvivesAReopen_WithItsExactStart()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var (task, occurrenceId) = await SeedTaskAsync(db);
        var started = At14.AddTicks(1234567); // sub-segundo: o tique tem de voltar igual

        await AddAsync(db, TimeEntry.StartTimer(occurrenceId, started));

        await using var reopened = db.CreateContext();
        var active = await new TimeEntryRepository(reopened).FindActiveAsync(Ct);
        var view = await new ActiveTimerQuery(reopened).FindAsync(Ct);

        active!.StartedAt.Should().Be(started);
        active.EndedAt.Should().BeNull();
        active.Source.Should().Be(TimeEntrySource.Timer);
        view!.TaskId.Should().Be(task.Id);
        view.OccurrenceId.Should().Be(occurrenceId);
        view.TaskTitle.Should().Be("Implementar autenticação");
        view.StartedAt.Should().Be(started);
    }

    [Fact]
    public async Task Stop_IsAnUpdate_OfTheSameRow()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var (_, occurrenceId) = await SeedTaskAsync(db);
        var entry = TimeEntry.StartTimer(occurrenceId, At14);
        await AddAsync(db, entry);

        await using (var stop = db.CreateContext())
        {
            var stored = await new TimeEntryRepository(stop).FindActiveAsync(Ct);
            stored!.Stop(At14.AddMinutes(76));
            await stop.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var reloaded = await new TimeEntryRepository(read).FindByIdAsync(entry.Id, Ct);
        reloaded!.EndedAt.Should().Be(At14.AddMinutes(76));
        reloaded.UpdatedAt.Should().Be(At14.AddMinutes(76));
        (await new TimeEntryRepository(read).FindActiveAsync(Ct)).Should().BeNull();
        (await new ActiveTimerQuery(read).FindAsync(Ct)).Should().BeNull();
        (await read.TimeEntries.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task AManualPeriod_RoundTrips_AndCanBeEditedAndDeleted()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var (_, occurrenceId) = await SeedTaskAsync(db);
        var entry = Closed(occurrenceId, 0, 90, "Corrigi problema na API");
        await AddAsync(db, entry);

        await using (var edit = db.CreateContext())
        {
            var stored = await new TimeEntryRepository(edit).FindByIdAsync(entry.Id, Ct);
            stored!.Source.Should().Be(TimeEntrySource.Manual);
            stored.Note.Should().Be("Corrigi problema na API");
            stored.Change(At14.AddMinutes(10), At14.AddMinutes(105), "revisado", At14.AddHours(8));
            await edit.SaveChangesAsync(Ct);
        }

        await using (var check = db.CreateContext())
        {
            var stored = await new TimeEntryRepository(check).FindByIdAsync(entry.Id, Ct);
            stored!.StartedAt.Should().Be(At14.AddMinutes(10));
            stored.EndedAt.Should().Be(At14.AddMinutes(105));
            stored.Note.Should().Be("revisado");
            stored.UpdatedAt.Should().Be(At14.AddHours(8));

            new TimeEntryRepository(check).Remove(stored);
            await check.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await read.TimeEntries.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task TheHistory_IsPerOccurrence_OldestFirst()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var (_, first) = await SeedTaskAsync(db);
        var (_, second) = await SeedTaskAsync(db, "Corrigir dashboard");
        await AddAsync(db, Closed(first, 120, 180), Closed(first, 0, 60), Closed(second, 0, 30));

        await using var read = db.CreateContext();
        var history = await new TimeEntryRepository(read).ListForOccurrenceAsync(first, Ct);

        history.Select(entry => entry.StartedAt).Should().Equal(At14, At14.AddMinutes(120));
    }

    /// <summary>O caso de uso recusa o segundo cronômetro; o banco também, para o que escapar.</summary>
    [Fact]
    public async Task TwoRunningTimers_AreRefusedByTheDatabase_EvenOnDifferentTasks()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var (_, first) = await SeedTaskAsync(db);
        var (_, second) = await SeedTaskAsync(db, "Corrigir dashboard");
        await AddAsync(db, TimeEntry.StartTimer(first, At14));

        var add = () => AddAsync(db, TimeEntry.StartTimer(second, At14.AddMinutes(1)));

        await add.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ClosedPeriods_DoNotCollide_WithEachOtherOrWithTheRunningOne()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var (_, first) = await SeedTaskAsync(db);
        var (_, second) = await SeedTaskAsync(db, "Corrigir dashboard");

        var add = () => AddAsync(
            db,
            Closed(first, 0, 30),
            Closed(first, 30, 60),
            Closed(second, 0, 30),
            TimeEntry.StartTimer(second, At14.AddMinutes(60)));

        await add.Should().NotThrowAsync();
    }

    /// <summary>
    /// O índice do cronômetro único é SQL cru: o EF não sabe dele, e uma
    /// migration que reconstrua a tabela o perderia calada. É este teste que
    /// avisa.
    /// </summary>
    [Fact]
    public async Task TheSingleActiveIndex_ExistsAfterEveryMigration()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await using var context = db.CreateContext();

        var definitions = await context.Database
            .SqlQuery<string>(
                $"SELECT sql AS Value FROM sqlite_master WHERE type = 'index' AND name = {TimeEntryConfiguration.SingleActiveIndex}")
            .ToListAsync(Ct);

        definitions.Should().ContainSingle()
            .Which.Should().Contain("UNIQUE").And.Contain("\"EndedAt\" IS NULL");
    }

    /// <summary>
    /// "Parar e iniciar" num SaveChanges só: o UPDATE do anterior tem de chegar
    /// ao banco antes do INSERT do novo, ou o índice recusa a troca.
    /// </summary>
    [Fact]
    public async Task SwitchingTasks_InOneSave_DoesNotTripTheIndex()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var (_, first) = await SeedTaskAsync(db);
        var (_, second) = await SeedTaskAsync(db, "Corrigir dashboard");
        await AddAsync(db, TimeEntry.StartTimer(first, At14));

        await using (var swap = db.CreateContext())
        {
            var repository = new TimeEntryRepository(swap);
            var running = await repository.FindActiveAsync(Ct);
            running!.Stop(At14.AddMinutes(40));
            await repository.AddAsync(TimeEntry.StartTimer(second, At14.AddMinutes(40)), Ct);

            var save = () => swap.SaveChangesAsync(Ct);

            await save.Should().NotThrowAsync();
        }

        await using var read = db.CreateContext();
        (await new ActiveTimerQuery(read).FindAsync(Ct))!.OccurrenceId.Should().Be(second);
    }

    [Fact]
    public async Task PurgingTheTask_TakesItsPeriodsAlong()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var (task, occurrenceId) = await SeedTaskAsync(db);
        var (_, other) = await SeedTaskAsync(db, "Corrigir dashboard");
        await AddAsync(db, Closed(occurrenceId, 0, 60), Closed(other, 0, 60));

        await using (var purge = db.CreateContext())
        {
            var stored = await new TaskItemRepository(purge).FindByIdAsync(task.Id, Ct);
            new TaskItemRepository(purge).Remove(stored!);
            await purge.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await read.TimeEntries.Select(entry => entry.TaskOccurrenceId).ToListAsync(Ct)).Should().Equal(other);
    }

    [Fact]
    public async Task TheTodayList_SumsTheClosedPeriods_AndKnowsTheRunningOne()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var (_, tracked) = await SeedTaskAsync(db);
        var (_, untracked) = await SeedTaskAsync(db, "Comprar pão");
        await AddAsync(db, Closed(tracked, -300, -210), Closed(tracked, -120, -78), TimeEntry.StartTimer(tracked, At14));

        await using var read = db.CreateContext();
        var rows = await new TodayQuery(read).GetCandidatesAsync(Today, Ct);

        var row = rows.Single(candidate => candidate.OccurrenceId == tracked);
        row.Logged.Should().Be(TimeSpan.FromMinutes(90 + 42));
        row.TimerStartedAt.Should().Be(At14);

        var plain = rows.Single(candidate => candidate.OccurrenceId == untracked);
        plain.Logged.Should().Be(TimeSpan.Zero);
        plain.TimerStartedAt.Should().BeNull();
    }

    /// <summary>
    /// Quem atualiza encontra as tarefas de sempre, sem período nenhum, e a lista
    /// sai como antes.
    /// </summary>
    [Fact]
    public async Task TheUpgrade_KeepsEveryTask_WithNoTimeLogged()
    {
        await using var db = await new TempSqliteDatabase().MigrateToAsync("QuickCommands", Ct);
        var taskId = Guid.CreateVersion7();
        var occurrenceId = Guid.CreateVersion7();

        await using (var write = db.CreateContext())
        {
            await write.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO Tasks (Id, Title, Description, Priority, CreatedAt)
                 VALUES ({taskId}, {"Renovar o contrato"}, NULL, 0, {At14.UtcTicks});
                 """,
                Ct);

            await write.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO TaskOccurrences
                     (Id, TaskItemId, ScheduledDate, ScheduledTime, Status, CompletedAt)
                 VALUES ({occurrenceId}, {taskId}, '2026-10-06', NULL, 0, NULL);
                 """,
                Ct);
        }

        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        var loaded = await new TaskItemRepository(read).FindByIdAsync(taskId, Ct);
        var rows = await new TodayQuery(read).GetCandidatesAsync(Today, Ct);

        loaded!.Title.Should().Be("Renovar o contrato");
        (await new TimeEntryRepository(read).ListForOccurrenceAsync(occurrenceId, Ct)).Should().BeEmpty();
        (await new ActiveTimerQuery(read).FindAsync(Ct)).Should().BeNull();
        rows.Single().Logged.Should().Be(TimeSpan.Zero);
        rows.Single().TimerStartedAt.Should().BeNull();
    }
}
