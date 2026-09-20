using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

public class TaskItemRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 17, 30, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TaskItem> SeedAsync(TempSqliteDatabase db, TaskItem task)
    {
        await using var context = db.CreateContext();
        await new TaskItemRepository(context).AddAsync(task, Ct);
        await new EfUnitOfWork(context).SaveChangesAsync(Ct);
        return task;
    }

    [Fact]
    public async Task FindByIdAsync_LoadsTheAggregateWithItsOccurrences()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedAsync(db, TaskItem.Create("Deploy", Now));

        await using var context = db.CreateContext();
        var loaded = await new TaskItemRepository(context).FindByIdAsync(task.Id, Ct);

        loaded.Should().NotBeNull();
        loaded!.Occurrences.Should().ContainSingle();
    }

    [Fact]
    public async Task FindByIdAsync_WithUnknownId_ReturnsNull()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using var context = db.CreateContext();
        var loaded = await new TaskItemRepository(context).FindByIdAsync(Guid.CreateVersion7(), Ct);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task FindByOccurrenceIdAsync_ReachesTheAggregateFromTheChild()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedAsync(db, TaskItem.Create("Revisar PR", Now));
        var occurrenceId = task.Occurrences.Single().Id;

        await using var context = db.CreateContext();
        var loaded = await new TaskItemRepository(context).FindByOccurrenceIdAsync(occurrenceId, Ct);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(task.Id);
        loaded.Occurrences.Single().Id.Should().Be(occurrenceId);
    }

    [Fact]
    public async Task FindByOccurrenceIdAsync_PicksTheRightTaskAmongMany()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await SeedAsync(db, TaskItem.Create("Outra tarefa", Now));
        var wanted = await SeedAsync(db, TaskItem.Create("Tarefa procurada", Now));

        await using var context = db.CreateContext();
        var loaded = await new TaskItemRepository(context)
            .FindByOccurrenceIdAsync(wanted.Occurrences.Single().Id, Ct);

        loaded!.Title.Should().Be("Tarefa procurada");
    }

    [Fact]
    public async Task Remove_DeletesTheAggregate()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedAsync(db, TaskItem.Create("Tarefa errada", Now));

        await using (var context = db.CreateContext())
        {
            var repository = new TaskItemRepository(context);
            var loaded = await repository.FindByIdAsync(task.Id, Ct);
            repository.Remove(loaded!);
            await new EfUnitOfWork(context).SaveChangesAsync(Ct);
        }

        await using var verify = db.CreateContext();
        (await verify.Tasks.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Schema_RefusesTwoOccurrencesAtTheSameScheduledInstant()
    {
        // Guarda de idempotência da materialização de recorrências (ADR-003):
        // rodar a geração duas vezes não pode duplicar a ocorrência do dia.
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedAsync(
            db,
            TaskItem.Create("Verificar e-mails", Now,
                schedule: TaskSchedule.At(new DateOnly(2026, 9, 18), new TimeOnly(8, 30))));

        await using var context = db.CreateContext();

        // Copia a linha existente com outra chave primária: mesma série, mesmo
        // instante agendado. Evita depender do formato em que o EF grava Guid.
        var duplicate = async () => await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO TaskOccurrences (Id, TaskItemId, ScheduledDate, ScheduledTime, Status, CompletedAt)
            SELECT 'duplicate-row', TaskItemId, ScheduledDate, ScheduledTime, Status, CompletedAt
            FROM TaskOccurrences
            """,
            Ct);

        await duplicate.Should().ThrowAsync<SqliteException>()
            .Where(exception => exception.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase));
    }
}
