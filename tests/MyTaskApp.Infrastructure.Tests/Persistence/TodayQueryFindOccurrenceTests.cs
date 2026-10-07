using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;
using MyTaskApp.Infrastructure.Persistence.Queries;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// Uma ocorrência com a linha inteira, fora do quadro de hoje (ADR-053): é o que
/// o histórico usa para abrir a tarefa de dias atrás na mesma janela.
/// </summary>
public class TodayQueryFindOccurrenceTests
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(-3));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TaskItem> SeedAsync(TempSqliteDatabase db, Action<TaskItem>? then = null)
    {
        var task = TaskItem.Create("Configurar ambiente", Monday.AddDays(-10), description: "Passo a passo");
        task.CompleteOccurrence(task.Occurrences.Single().Id, Monday.AddHours(8));
        then?.Invoke(task);

        await using var context = db.CreateContext();
        context.Tasks.Add(task);
        context.TimeEntries.Add(TimeEntry.Manual(
            task.Occurrences.Single().Id, Monday, Monday.AddMinutes(80), null, Monday.AddDays(1)));
        await context.SaveChangesAsync(Ct);

        return task;
    }

    [Fact]
    public async Task ACompletedTaskFromDaysAgo_ComesWithItsWholeLine()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedAsync(db);
        var occurrenceId = task.Occurrences.Single().Id;

        await using var context = db.CreateContext();
        var row = await new TodayQuery(context).FindOccurrenceAsync(occurrenceId, Ct);

        row.Should().NotBeNull();
        row!.OccurrenceId.Should().Be(occurrenceId);
        row.TaskId.Should().Be(task.Id);
        row.Title.Should().Be("Configurar ambiente");
        row.Description.Should().Be("Passo a passo");
        row.Status.Should().Be(TaskItemStatus.Completed);
        row.Logged.Should().Be(TimeSpan.FromMinutes(80));
    }

    [Fact]
    public async Task AnArchivedTask_StillOpens()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedAsync(db, item => item.Archive(Monday.AddDays(1)));

        await using var context = db.CreateContext();
        var row = await new TodayQuery(context).FindOccurrenceAsync(task.Occurrences.Single().Id, Ct);

        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ATaskInTheTrash_DoesNotOpen()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedAsync(db, item => item.MoveToTrash(Monday.AddDays(1), "eu"));

        await using var context = db.CreateContext();
        var row = await new TodayQuery(context).FindOccurrenceAsync(task.Occurrences.Single().Id, Ct);

        row.Should().BeNull();
    }

    [Fact]
    public async Task AnUnknownOccurrence_IsNull()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using var context = db.CreateContext();
        var row = await new TodayQuery(context).FindOccurrenceAsync(Guid.CreateVersion7(), Ct);

        row.Should().BeNull();
    }
}
