using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

public class TaskPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 17, 30, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_CreateAUsableSchema()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await using var context = db.CreateContext();

        var applied = await context.Database.GetAppliedMigrationsAsync(Ct);

        applied.Should().NotBeEmpty();
        (await context.Tasks.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Aggregate_SurvivesARoundTripThroughTheDatabase()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create(
            "Investigar estoque",
            Now,
            "Verificar como a integração processa a entrada.",
            TaskPriority.High,
            TaskSchedule.At(new DateOnly(2026, 9, 17), new TimeOnly(15, 30)));

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(task);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var loaded = await read.Tasks.Include(t => t.Occurrences).SingleAsync(Ct);

        loaded.Id.Should().Be(task.Id);
        loaded.Title.Should().Be("Investigar estoque");
        loaded.Description.Should().Be("Verificar como a integração processa a entrada.");
        loaded.Priority.Should().Be(TaskPriority.High);
        loaded.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Occurrence_PreservesDateAndTimeExactly()
    {
        // DateOnly/TimeOnly viram TEXT no SQLite; o round-trip precisa ser exato.
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create(
            "Daily", Now, schedule: TaskSchedule.At(new DateOnly(2026, 9, 17), new TimeOnly(9, 0)));

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(task);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var occurrence = await read.Occurrences.SingleAsync(Ct);

        occurrence.ScheduledDate.Should().Be(new DateOnly(2026, 9, 17));
        occurrence.ScheduledTime.Should().Be(new TimeOnly(9, 0));
    }

    [Fact]
    public async Task UndatedTask_RoundTripsWithNullSchedule()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Comprar HD externo", Now);

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(task);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var occurrence = await read.Occurrences.SingleAsync(Ct);

        occurrence.ScheduledDate.Should().BeNull();
        occurrence.ScheduledTime.Should().BeNull();
    }

    [Fact]
    public async Task Completion_IsPersistedWithItsInstant()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Revisar PR", Now);
        var completedAt = Now.AddHours(2);

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(task);
            await write.SaveChangesAsync(Ct);
        }

        await using (var update = db.CreateContext())
        {
            var loaded = await update.Tasks.Include(t => t.Occurrences).SingleAsync(Ct);
            loaded.Occurrences.Single().Complete(completedAt);
            await update.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var occurrence = await read.Occurrences.SingleAsync(Ct);

        occurrence.Status.Should().Be(TaskItemStatus.Completed);
        occurrence.CompletedAt.Should().Be(completedAt);
    }

    [Fact]
    public async Task DeletingATask_AlsoRemovesItsOccurrences()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Tarefa errada", Now);

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(task);
            await write.SaveChangesAsync(Ct);
        }

        await using (var delete = db.CreateContext())
        {
            delete.Tasks.Remove(await delete.Tasks.SingleAsync(Ct));
            await delete.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await read.Occurrences.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task PrivateConstructors_AreEnoughForMaterialization()
    {
        // O domínio não expõe construtor público; o mapeamento não pode exigir um.
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Daily", Now, schedule: TaskSchedule.On(new DateOnly(2026, 9, 17)));

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(task);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var loaded = await read.Tasks.Include(t => t.Occurrences).SingleAsync(Ct);

        loaded.Occurrences.Should().ContainSingle();
        loaded.Occurrences.Single().TaskItemId.Should().Be(loaded.Id);
    }
}
