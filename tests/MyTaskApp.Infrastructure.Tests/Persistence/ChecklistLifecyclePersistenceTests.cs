using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// O ciclo de vida contra o SQLite de verdade, pelas migrations de verdade. Os
/// instantes viram ticks (ADR-011) e as marcas do §9 são colunas — é o tipo de
/// coisa que quebra em silêncio se não for exercitada contra o banco.
/// </summary>
public class ChecklistLifecyclePersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 13, 45, 30, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheLifecycleMarks_SurviveARoundTrip()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        var task = TaskItem.Create("Fechar o mês", Now.AddDays(-60));
        task.CompleteOccurrence(task.Occurrences.Single().Id, Now.AddDays(-40));
        task.Archive(Now.AddDays(-5));
        task.MoveToTrash(Now, "adriano");

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(task);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var stored = await read.Tasks.SingleAsync(Ct);

        stored.ConcludedAt.Should().Be(Now.AddDays(-40));
        stored.ArchivedAt.Should().Be(Now.AddDays(-5));
        stored.DeletedAt.Should().Be(Now);
        stored.DeletedBy.Should().Be("adriano");
        stored.Lifecycle.Should().Be(TaskLifecycle.Trashed);
    }

    /// <summary>
    /// A garantia central do §8, e a única que o esquema — não o código — tem
    /// de sustentar: sem chave estrangeira, o <c>DELETE</c> do checklist não
    /// leva junto o registro da própria exclusão.
    /// </summary>
    [Fact]
    public async Task TheAuditTrail_SurvivesThePermanentDeletionItRecords()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        var task = TaskItem.Create("Fechar o mês", Now.AddDays(-10));
        task.MoveToTrash(Now, "adriano");

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(task);
            await write.SaveChangesAsync(Ct);
        }

        await using (var purge = db.CreateContext())
        {
            var stored = await purge.Tasks.SingleAsync(Ct);

            // Auditoria e remoção no mesmo SaveChanges, como faz o caso de uso.
            purge.TaskAudit.Add(TaskAuditEntry.ByUser(
                stored.Id,
                stored.Title,
                TaskAuditOperation.PermanentlyDeleted,
                Now,
                "adriano"));

            purge.Tasks.Remove(stored);

            await purge.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        (await read.Tasks.CountAsync(Ct)).Should().Be(0);

        var trail = await read.TaskAudit.SingleAsync(Ct);

        trail.TaskId.Should().Be(task.Id);
        trail.TaskTitle.Should().Be("Fechar o mês");
        trail.Operation.Should().Be(TaskAuditOperation.PermanentlyDeleted);
        trail.OccurredAt.Should().Be(Now);
    }

    /// <summary>
    /// O outro lado da exclusão definitiva: o que <b>deve</b> sair sai. Sem a
    /// cascata, as ocorrências ficariam órfãs apontando para um checklist que
    /// não existe mais.
    /// </summary>
    [Fact]
    public async Task PermanentDeletion_TakesTheOccurrencesWithItAndLeavesNoOrphans()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        var task = TaskItem.Create("Fechar o mês", Now);

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(task);
            await write.SaveChangesAsync(Ct);
        }

        await using (var purge = db.CreateContext())
        {
            purge.Tasks.Remove(await purge.Tasks.SingleAsync(Ct));
            await purge.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        (await read.Occurrences.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task TheTrail_IsReadBackNewestFirstAndKeepsTheActor()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var taskId = Guid.CreateVersion7();

        await using (var write = db.CreateContext())
        {
            write.TaskAudit.AddRange(
                TaskAuditEntry.ByUser(
                    taskId, "Fechar o mês", TaskAuditOperation.Created, Now.AddDays(-3), "adriano"),
                TaskAuditEntry.BySystem(
                    taskId, "Fechar o mês", TaskAuditOperation.Archived, Now, "30 dias."));

            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        var trail = await new EfTaskAuditLog(read).GetForTaskAsync(taskId, Ct);

        trail.Select(entry => entry.Operation).Should().Equal(
            TaskAuditOperation.Archived,
            TaskAuditOperation.Created);

        trail[0].Actor.Should().Be(AuditActor.System);
        trail[0].ActorName.Should().BeNull();
        trail[0].Details.Should().Be("30 dias.");
        trail[1].ActorName.Should().Be("adriano");
    }

    /// <summary>
    /// Um id que não existe mais é o caso normal depois de uma exclusão
    /// definitiva, não um erro.
    /// </summary>
    [Fact]
    public async Task TheTrail_AnswersForAChecklistThatNoLongerExists()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var goneId = Guid.CreateVersion7();

        await using (var write = db.CreateContext())
        {
            write.TaskAudit.Add(TaskAuditEntry.BySystem(
                goneId, "Sumiu", TaskAuditOperation.PermanentlyDeleted, Now));

            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        (await new EfTaskAuditLog(read).GetForTaskAsync(goneId, Ct))
            .Should().ContainSingle();
    }
}
