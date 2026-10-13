using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// O diretório sem pasta neste computador (ADR-060): veio de outro dispositivo,
/// e o caminho de lá nunca é gravado aqui. A lista da etiqueta o mostra; o
/// autocomplete e os comandos rápidos, que precisam do caminho, não.
/// </summary>
public class TagDirectoryUnmappedPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private const string Repository = @"C:\Projects\ecossistema-core";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Upgrading_KeepsEveryDirectoryAndItsPath()
    {
        await using var db = await new TempSqliteDatabase().MigrateToAsync("McpServerSettings", Ct);
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var directory = tag.AddDirectory("@core", Repository, "Core", null, Now, "develop");
        await SeedAsync(db, tag);

        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        var stored = await read.TagDirectories.SingleAsync(Ct);
        stored.Id.Should().Be(directory.Id);
        stored.Path.Should().Be(Repository);
        stored.IsMapped.Should().BeTrue();
        stored.DefaultBranch.Should().Be("develop");
    }

    [Fact]
    public async Task AnUnmappedDirectory_ShowsInTheTag_ButNotInTheTaskOrTheQuickCommands()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var (tag, directory, task) = await SeedUnmappedAsync(db);

        await using var read = db.CreateContext();
        var query = new TagQuery(read);

        var row = (await query.ListDirectoriesAsync(tag.Id, Ct)).Should().ContainSingle().Subject;
        row.Id.Should().Be(directory.Id);
        row.Path.Should().BeNull();
        row.IsMapped.Should().BeFalse();

        (await query.ListDirectoriesForTaskAsync(task.Id, Ct)).Should().BeEmpty();
        (await query.ListCommandDirectoriesAsync(Ct)).Should().BeEmpty();
        (await query.ListDirectoryCommandsAsync(directory.Id, Ct)).Should().ContainSingle("os botões continuam cadastrados");
    }

    [Fact]
    public async Task EditingWithoutAPath_KeepsItUnmapped_AndFillingThePathMapsIt()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var (tag, directory, task) = await SeedUnmappedAsync(db);

        await using (var write = db.CreateContext())
        {
            var loaded = await new TagRepository(write).FindByIdAsync(tag.Id, Ct);
            loaded!.UpdateDirectory(directory.Id, "@core2", "  ", "Core", null, "main");
            await write.SaveChangesAsync(Ct);
        }

        await using (var read = db.CreateContext())
        {
            var stored = await read.TagDirectories.SingleAsync(Ct);
            stored.Alias.Should().Be("@core2");
            stored.Path.Should().BeNull();
        }

        await using (var write = db.CreateContext())
        {
            var loaded = await new TagRepository(write).FindByIdAsync(tag.Id, Ct);
            loaded!.UpdateDirectory(directory.Id, "@core2", @"D:\Code\ecossistema-core\", "Core", null, "main");
            await write.SaveChangesAsync(Ct);
        }

        await using var check = db.CreateContext();
        (await check.TagDirectories.SingleAsync(Ct)).Path.Should().Be(@"D:\Code\ecossistema-core");
        (await new TagQuery(check).ListDirectoriesForTaskAsync(task.Id, Ct)).Should().ContainSingle();
    }

    [Fact]
    public void AMappedDirectory_CannotLoseItsPath()
    {
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var directory = tag.AddDirectory("@core", Repository, null, null, Now);

        var unmap = () => tag.UpdateDirectory(directory.Id, "@core", null, null, null);

        unmap.Should().Throw<DomainException>().WithMessage("*caminho*");
    }

    /// <summary>Uma etiqueta com um diretório que chegou sem caminho, um botão e uma tarefa nela.</summary>
    private static async Task<(Tag Tag, TagDirectory Directory, TaskItem Task)> SeedUnmappedAsync(TempSqliteDatabase db)
    {
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var directory = tag.AddDirectory("@core", Repository, "Core", null, Now);
        var command = DevelopmentCommand.Create("@build", "dotnet build", null, Now);
        tag.AddDirectoryCommand(directory.Id, command.Id, Now);
        var task = TaskItem.Create("Feature X", Now);
        task.SetTags([tag.Id]);
        await SeedAsync(db, command, tag, task);

        // Como a sincronização grava: o caminho de outra máquina nunca chega.
        await using var write = db.CreateContext();
        await write.Database.ExecuteSqlAsync($"UPDATE TagDirectories SET Path = NULL WHERE Id = {directory.Id}", Ct);

        return (tag, directory, task);
    }

    private static async Task SeedAsync(TempSqliteDatabase db, params object[] entities)
    {
        await using var context = db.CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }
}
