using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// Os diretórios das etiquetas contra SQLite de verdade (ADR-026): unicidade por
/// etiqueta, cascata e a consulta que alimenta o autocomplete da anotação.
/// </summary>
public class TagDirectoryPersistenceTests
{
    private static readonly DateOnly Today = new(2026, 9, 23);
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

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
    public async Task DirectoriesSurviveTheRoundTripThroughTheRepository()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var eco = Tag.Create("ECO CORE", "#22C55E", Now);
        await SeedAsync(db, eco);

        Guid coreId;

        await using (var write = db.CreateContext())
        {
            var tag = await new TagRepository(write).FindByIdAsync(eco.Id, Ct);
            coreId = tag!.AddDirectory("@ecossistema-core", @"C:\Projects\ecossistema-core", "Core", "API", Now).Id;
            tag.AddDirectory("@ecossistema-web", @"C:\Projects\ecossistema-web", null, null, Now);
            await write.SaveChangesAsync(Ct);
        }

        await using (var write = db.CreateContext())
        {
            var tag = await new TagRepository(write).FindByIdAsync(eco.Id, Ct);
            tag!.Directories.Should().HaveCount(2);
            tag.UpdateDirectory(coreId, "@ecossistema-core", @"D:\Projects\ecossistema-core", "Core", "API", "develop");
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var core = await read.TagDirectories.SingleAsync(directory => directory.Id == coreId, Ct);
        core.Path.Should().Be(@"D:\Projects\ecossistema-core");
        core.Name.Should().Be("Core");
        core.Description.Should().Be("API");
        core.DefaultBranch.Should().Be("develop");
        core.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task OneTagCannotHaveTheSameAliasTwice_IgnoringCase()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var eco = Tag.Create("ECO CORE", "#22C55E", Now);
        eco.AddDirectory("@eco", @"C:\eco", null, null, Now);
        await SeedAsync(db, eco);

        await using var write = db.CreateContext();
        var duplicate = () => write.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO TagDirectories (Id, TagId, Alias, Path, CreatedAt)
            VALUES ({Guid.NewGuid()}, {eco.Id}, '@ECO', 'C:\outro', 0)
            """,
            Ct);

        await duplicate.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
    }

    [Fact]
    public async Task TwoTagsMayShareAnAlias()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var eco = Tag.Create("ECO CORE", "#22C55E", Now);
        var app = Tag.Create("MY TASK APP", "#6366F1", Now);
        eco.AddDirectory("@api", @"C:\eco\api", null, null, Now);
        app.AddDirectory("@api", @"C:\mytaskapp\api", null, null, Now);

        var seed = () => SeedAsync(db, eco, app);

        await seed.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeletingATag_TakesItsDirectoriesAlong()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var eco = Tag.Create("ECO CORE", "#22C55E", Now);
        var app = Tag.Create("MY TASK APP", "#6366F1", Now);
        eco.AddDirectory("@eco", @"C:\eco", null, null, Now);
        app.AddDirectory("@mytaskapp", @"C:\mytaskapp", null, null, Now);
        await SeedAsync(db, eco, app);

        await using (var write = db.CreateContext())
        {
            var repository = new TagRepository(write);
            repository.Remove((await repository.FindByIdAsync(eco.Id, Ct))!);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await read.TagDirectories.SingleAsync(Ct)).Alias.Should().Be("@mytaskapp");
    }

    [Fact]
    public async Task TheTasksDirectories_ComeOnlyFromItsOwnTags_InAliasOrder()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var eco = Tag.Create("ECO CORE", "#22C55E", Now);
        var app = Tag.Create("MY TASK APP", "#6366F1", Now);
        var other = Tag.Create("Outra", "#EF4444", Now);
        eco.AddDirectory("@ecossistema-web", @"C:\Projects\ecossistema-web", null, null, Now);
        eco.AddDirectory("@ecossistema-core", @"C:\Projects\ecossistema-core", "Core", null, Now, "develop");
        app.AddDirectory("@mytaskapp", @"C:\Projetos\MyTaskApp", null, null, Now);
        other.AddDirectory("@fora", @"C:\fora", null, null, Now);
        var task = TodayTask("Corrigir problema no processamento dos animais");
        task.SetTags([eco.Id, app.Id]);
        await SeedAsync(db, eco, app, other, task);

        await using var context = db.CreateContext();
        var rows = await new TagQuery(context).ListDirectoriesForTaskAsync(task.Id, Ct);

        rows.Select(row => (row.Alias, row.TagName))
            .Should().Equal(
                ("@ecossistema-core", "ECO CORE"),
                ("@ecossistema-web", "ECO CORE"),
                ("@mytaskapp", "MY TASK APP"));
        rows[0].Path.Should().Be(@"C:\Projects\ecossistema-core");
        rows[0].Name.Should().Be("Core");
        rows[0].TagColorHex.Should().Be("#22C55E");
        rows[0].DefaultBranch.Should().Be("develop");
        rows[1].DefaultBranch.Should().BeNull();
    }

    [Fact]
    public async Task ATaskWithoutTags_HasNoDirectories()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var eco = Tag.Create("ECO CORE", "#22C55E", Now);
        eco.AddDirectory("@eco", @"C:\eco", null, null, Now);
        var task = TodayTask("Sem etiqueta");
        await SeedAsync(db, eco, task);

        await using var context = db.CreateContext();

        (await new TagQuery(context).ListDirectoriesForTaskAsync(task.Id, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheTagList_CountsDirectories_AndListsThemPerTag()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var eco = Tag.Create("ECO CORE", "#22C55E", Now);
        var app = Tag.Create("MY TASK APP", "#6366F1", Now);
        eco.AddDirectory("@b", @"C:\b", null, null, Now);
        eco.AddDirectory("@a", @"C:\a", null, null, Now);
        await SeedAsync(db, eco, app);

        await using var context = db.CreateContext();
        var query = new TagQuery(context);

        (await query.ListAsync(Ct)).Select(tag => (tag.Name, tag.DirectoryCount))
            .Should().Equal(("ECO CORE", 2), ("MY TASK APP", 0));
        (await query.ListDirectoriesAsync(eco.Id, Ct)).Select(row => row.Alias)
            .Should().Equal("@a", "@b");
    }
}
