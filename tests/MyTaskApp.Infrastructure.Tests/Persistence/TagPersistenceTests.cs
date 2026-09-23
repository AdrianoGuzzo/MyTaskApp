using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// As etiquetas contra SQLite de verdade (ADR-025): a unicidade, as cascatas e
/// as consultas que desenham as bolinhas.
/// </summary>
public class TagPersistenceTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SeedAsync(TempSqliteDatabase db, params object[] entities)
    {
        await using var context = db.CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }

    private static TaskItem TodayTask(string title) =>
        TaskItem.Create(title, Now, schedule: TaskSchedule.On(Today));

    [Fact]
    public async Task TagsSurviveTheRoundTripThroughTheRepository()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var urgent = Tag.Create("Urgente", "#EF4444", Now);
        var finance = Tag.Create("Financeiro", "#3B82F6", Now);
        var task = TodayTask("Pagar boleto");
        await SeedAsync(db, urgent, finance, task);

        await using (var write = db.CreateContext())
        {
            var loaded = await new TaskItemRepository(write).FindByIdAsync(task.Id, Ct);
            loaded!.SetTags([urgent.Id, finance.Id]);
            await write.SaveChangesAsync(Ct);
        }

        await using (var write = db.CreateContext())
        {
            var loaded = await new TaskItemRepository(write).FindByIdAsync(task.Id, Ct);
            loaded!.Tags.Should().HaveCount(2);
            loaded.SetTags([finance.Id]);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var links = await read.TaskItemTags.ToListAsync(Ct);
        links.Should().ContainSingle().Which.TagId.Should().Be(finance.Id);
    }

    [Fact]
    public async Task TheSameTagCannotBeLinkedTwiceToTheSameTask()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var urgent = Tag.Create("Urgente", "#EF4444", Now);
        var task = TodayTask("Pagar boleto");
        task.SetTags([urgent.Id]);
        await SeedAsync(db, urgent, task);

        await using var write = db.CreateContext();
        var duplicate = () => write.Database.ExecuteSqlAsync(
            $"INSERT INTO TaskItemTags (TaskItemId, TagId) VALUES ({task.Id}, {urgent.Id})",
            Ct);

        await duplicate.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
    }

    [Fact]
    public async Task TwoTagsCannotShareANameIgnoringCase()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await SeedAsync(db, Tag.Create("Urgente", "#EF4444", Now));

        var duplicate = () => SeedAsync(db, Tag.Create("URGENTE", "#3B82F6", Now));

        await duplicate.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task NameExists_IgnoresCaseAndTheTagItself()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var urgent = Tag.Create("Urgente", "#EF4444", Now);
        await SeedAsync(db, urgent);

        await using var context = db.CreateContext();
        var repository = new TagRepository(context);

        (await repository.NameExistsAsync("urgente", cancellationToken: Ct)).Should().BeTrue();
        (await repository.NameExistsAsync("urgente", urgent.Id, Ct)).Should().BeFalse();
        (await repository.NameExistsAsync("Financeiro", cancellationToken: Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task DeletingATag_RemovesItFromEveryTask()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var urgent = Tag.Create("Urgente", "#EF4444", Now);
        var finance = Tag.Create("Financeiro", "#3B82F6", Now);
        var first = TodayTask("Pagar boleto");
        var second = TodayTask("Enviar nota");
        first.SetTags([urgent.Id, finance.Id]);
        second.SetTags([urgent.Id]);
        await SeedAsync(db, urgent, finance, first, second);

        await using (var write = db.CreateContext())
        {
            var repository = new TagRepository(write);
            repository.Remove((await repository.FindByIdAsync(urgent.Id, Ct))!);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var links = await read.TaskItemTags.ToListAsync(Ct);
        links.Should().ContainSingle().Which.TagId.Should().Be(finance.Id);
    }

    [Fact]
    public async Task PurgingATask_RemovesItsLinksButKeepsTheTags()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var urgent = Tag.Create("Urgente", "#EF4444", Now);
        var task = TodayTask("Pagar boleto");
        task.SetTags([urgent.Id]);
        await SeedAsync(db, urgent, task);

        await using (var write = db.CreateContext())
        {
            var repository = new TaskItemRepository(write);
            repository.Remove((await repository.FindByIdAsync(task.Id, Ct))!);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await read.TaskItemTags.CountAsync(Ct)).Should().Be(0);
        (await read.Tags.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task TheTodayBoardBringsEachTasksTagsInAlphabeticalOrder()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var urgent = Tag.Create("Urgente", "#EF4444", Now);
        var finance = Tag.Create("Financeiro", "#3B82F6", Now);
        var tagged = TodayTask("Pagar boleto");
        var plain = TodayTask("Ler e-mails");
        tagged.SetTags([urgent.Id, finance.Id]);
        await SeedAsync(db, urgent, finance, tagged, plain);

        await using var context = db.CreateContext();
        var rows = await new TodayQuery(context).GetCandidatesAsync(Today, Ct);

        rows.Single(row => row.Title == "Pagar boleto").Tags!
            .Select(tag => (tag.Name, tag.ColorHex))
            .Should().Equal(("Financeiro", "#3B82F6"), ("Urgente", "#EF4444"));
        rows.Single(row => row.Title == "Ler e-mails").Tags.Should().BeNull();
    }

    [Fact]
    public async Task RenamingATag_ShowsUpOnTheBoardWithoutTouchingTheTask()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var urgent = Tag.Create("Urgente", "#EF4444", Now);
        var task = TodayTask("Pagar boleto");
        task.SetTags([urgent.Id]);
        await SeedAsync(db, urgent, task);

        await using (var write = db.CreateContext())
        {
            (await new TagRepository(write).FindByIdAsync(urgent.Id, Ct))!.Update("Prioridade", "#F97316");
            await write.SaveChangesAsync(Ct);
        }

        await using var context = db.CreateContext();
        var rows = await new TodayQuery(context).GetCandidatesAsync(Today, Ct);

        rows.Single().Tags.Should().ContainSingle()
            .Which.Should().Be(new MyTaskApp.Application.Planning.TagBadge(urgent.Id, "Prioridade", "#F97316"));
    }

    [Fact]
    public async Task TheTagList_IsAlphabeticalAndCountsUsage()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var urgent = Tag.Create("Urgente", "#EF4444", Now);
        var finance = Tag.Create("Financeiro", "#3B82F6", Now);
        var first = TodayTask("Pagar boleto");
        var second = TodayTask("Enviar nota");
        first.SetTags([urgent.Id, finance.Id]);
        second.SetTags([urgent.Id]);
        await SeedAsync(db, urgent, finance, first, second);

        await using var context = db.CreateContext();
        var list = await new TagQuery(context).ListAsync(Ct);

        list.Select(tag => (tag.Name, tag.UsageCount))
            .Should().Equal(("Financeiro", 1), ("Urgente", 2));
    }
}
