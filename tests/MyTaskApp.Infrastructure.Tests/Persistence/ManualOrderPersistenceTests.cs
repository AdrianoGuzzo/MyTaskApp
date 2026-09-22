using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// A ordem manual contra o SQLite de verdade (ADR-022). Uma lista que o usuário
/// arrumou e que volta embaralhada na próxima abertura seria pior do que não ter
/// arrasto nenhum.
/// </summary>
public class ManualOrderPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SeedAsync(TempSqliteDatabase db, params TaskItem[] tasks)
    {
        await using var context = db.CreateContext();
        context.Tasks.AddRange(tasks);
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task APlacedOccurrence_KeepsItsSlotAcrossAReopen()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Comprar pão", Now);
        await SeedAsync(db, task);

        await using (var write = db.CreateContext())
        {
            var loaded = await new TaskItemRepository(write).FindByIdAsync(task.Id, Ct);
            loaded!.PlaceOccurrence(loaded.Occurrences.Single().Id, 7);
            await new EfUnitOfWork(write).SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var occurrence = await read.Occurrences.SingleAsync(Ct);

        occurrence.Position.Should().Be(7);
    }

    [Fact]
    public async Task AnOccurrenceThatWasNeverDragged_StaysNull()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await SeedAsync(db, TaskItem.Create("Nunca arrastada", Now));

        await using var read = db.CreateContext();
        var occurrence = await read.Occurrences.SingleAsync(Ct);

        occurrence.Position.Should().BeNull();
    }

    [Fact]
    public async Task FindByOccurrenceIdsAsync_BringsEveryOwnerInOneGo()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        var tasks = Enumerable
            .Range(0, 4)
            .Select(index => TaskItem.Create($"Tarefa {index}", Now.AddSeconds(index)))
            .ToArray();

        await SeedAsync(db, tasks);

        var wanted = tasks.Take(3).Select(task => task.Occurrences.Single().Id).ToList();

        await using var context = db.CreateContext();
        var owners = await new TaskItemRepository(context).FindByOccurrenceIdsAsync(wanted, Ct);

        owners.Select(owner => owner.Id).Should().BeEquivalentTo(tasks.Take(3).Select(t => t.Id));
    }

    /// <summary>
    /// Agregado carrega inteiro: uma raiz trazida com meia coleção faria as
    /// invariantes raciocinarem sobre ocorrências que não estão ali.
    /// </summary>
    [Fact]
    public async Task FindByOccurrenceIdsAsync_LoadsTheWholeAggregate()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        var task = TaskItem.Create(
            "Série",
            Now,
            schedule: TaskSchedule.At(new DateOnly(2026, 9, 21), new TimeOnly(9, 0)));

        await SeedAsync(db, task);

        await using var context = db.CreateContext();
        var owners = await new TaskItemRepository(context)
            .FindByOccurrenceIdsAsync([task.Occurrences.Single().Id], Ct);

        owners.Single().Occurrences.Should().HaveCount(task.Occurrences.Count);
    }

    [Fact]
    public async Task FindByOccurrenceIdsAsync_WithNoMatches_ReturnsEmpty()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using var context = db.CreateContext();
        var owners = await new TaskItemRepository(context)
            .FindByOccurrenceIdsAsync([Guid.CreateVersion7()], Ct);

        owners.Should().BeEmpty();
    }
}
