using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// O comando criado direto no diretório da etiqueta contra SQLite de verdade
/// (ADR-054): fora da lista de globais, sem apelido, e indo embora com o diretório.
/// </summary>
public class DirectoryOnlyCommandsPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private const string Repository = @"C:\Projects\ecossistema-core";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SeedAsync(TempSqliteDatabase db, params object[] entities)
    {
        await using var context = db.CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }

    private static DevelopmentCommand Own(Guid directoryId, string name, string command = "npm run dev -- --port {port}") =>
        DevelopmentCommand.CreateForDirectory(
            directoryId,
            command,
            null,
            new DevelopmentCommandSettings(
                name,
                CommandMode.Terminal,
                "web",
                Parameters: [new CommandParameterSpec("port", "Porta", CommandParameterType.Number, "5173", true)]),
            Now);

    [Fact]
    public async Task OwnCommands_RoundTrip_WithoutAlias_AndStayOutOfTheGlobals()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var directory = tag.AddDirectory("@eco", Repository, null, null, Now);
        var global = DevelopmentCommand.Create("@run", "dotnet run", null, Now);
        // Dois sem apelido: o índice único do Alias não os confunde.
        var front = Own(directory.Id, "Front-end");
        var storybook = Own(directory.Id, "Storybook", "npm run storybook");
        tag.AddDirectoryCommand(directory.Id, global.Id, Now);
        tag.AddDirectoryCommand(directory.Id, front.Id, Now);
        tag.AddDirectoryCommand(directory.Id, storybook.Id, Now);
        await SeedAsync(db, tag, global, front, storybook);

        await using var read = db.CreateContext();
        var commands = new DevelopmentCommandRepository(read);

        (await commands.ListAsync(Ct)).Select(command => command.Id).Should().Equal(global.Id);

        var own = await commands.ListForDirectoriesAsync([directory.Id], Ct);
        own.Select(command => command.DisplayName).Should().BeEquivalentTo("Front-end", "Storybook");
        var reloaded = own.Single(command => command.Id == front.Id);
        reloaded.TagDirectoryId.Should().Be(directory.Id);
        reloaded.Alias.Should().BeNull();
        reloaded.WorkingDirectory.Should().Be("web");
        reloaded.Parameters.Should().ContainSingle().Which.Label.Should().Be("Porta");

        (await commands.ListForDirectoriesAsync([Guid.CreateVersion7()], Ct)).Should().BeEmpty();
        (await commands.AliasExistsAsync("@run", null, Ct)).Should().BeTrue();

        var rows = await new TagQuery(read).ListDirectoryCommandsAsync(directory.Id, Ct);
        rows.Select(row => (row.DisplayName, row.IsDirectoryOnly)).Should().Equal(
            ("@run", false),
            ("Front-end", true),
            ("Storybook", true));
        rows[1].Alias.Should().BeNull();
    }

    [Fact]
    public async Task DeletingTheDirectory_TakesItsOwnCommands_ButNotTheGlobals()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var directory = tag.AddDirectory("@eco", Repository, null, null, Now);
        var global = DevelopmentCommand.Create("@run", "dotnet run", null, Now);
        var front = Own(directory.Id, "Front-end");
        tag.AddDirectoryCommand(directory.Id, global.Id, Now);
        var binding = tag.AddDirectoryCommand(directory.Id, front.Id, Now);
        var task = TaskItem.Create("Feature X", Now);
        var execution = CommandExecution.Create(
            task.Id, null, front.Id, binding.Id, "Front-end", "npm run dev", Repository, CommandMode.Terminal, true, Now);
        await SeedAsync(db, tag, global, front, task, execution);

        await using (var edit = db.CreateContext())
        {
            var stored = await new TagRepository(edit).FindByIdAsync(tag.Id, Ct);
            stored!.RemoveDirectory(directory.Id);
            await edit.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await read.DevelopmentCommands.Select(command => command.Id).ToListAsync(Ct)).Should().Equal(global.Id);
        (await read.DevelopmentCommandParameters.CountAsync(Ct)).Should().Be(0);
        (await read.TagDirectoryCommands.CountAsync(Ct)).Should().Be(0);
        var kept = await read.CommandExecutions.SingleAsync(Ct);
        kept.DevelopmentCommandId.Should().BeNull("o histórico fica, sem o vínculo");
        kept.CommandName.Should().Be("Front-end");
    }

    [Fact]
    public async Task DeletingTheTag_TakesTheOwnCommandsOfAllItsDirectories()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var core = tag.AddDirectory("@eco", Repository, null, null, Now);
        var web = tag.AddDirectory("@eco-web", @"C:\Projects\eco-web", null, null, Now);
        var front = Own(web.Id, "Front-end");
        var api = Own(core.Id, "API", "dotnet watch");
        tag.AddDirectoryCommand(web.Id, front.Id, Now);
        tag.AddDirectoryCommand(core.Id, api.Id, Now);
        await SeedAsync(db, tag, front, api);

        await using (var delete = db.CreateContext())
        {
            var tags = new TagRepository(delete);
            tags.Remove((await tags.FindByIdAsync(tag.Id, Ct))!);
            await delete.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await read.DevelopmentCommands.CountAsync(Ct)).Should().Be(0);
    }

    /// <summary>
    /// A migration reconstrói a tabela dos comandos (o SQLite não altera coluna):
    /// os globais, os parâmetros, os botões e o histórico de antes ficam.
    /// </summary>
    [Fact]
    public async Task Upgrading_KeepsTheGlobalsTheirParametersBindingsAndHistory()
    {
        await using var db = await new TempSqliteDatabase().MigrateToAsync("TimeEntries", Ct);
        var commandId = Guid.CreateVersion7();

        // Só a tabela que a migration muda vai por SQL: as outras já têm a forma final.
        await using (var write = db.CreateContext())
        {
            await write.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO DevelopmentCommands (Id, Alias, Command, Description, CreatedAt, UpdatedAt, Name, Mode, WorkingDirectory, KeepTerminalOpen, RequiresConfirmation)
                 VALUES ({commandId}, {"@run"}, {"dotnet run --launch-profile {profile}"}, NULL, {Now.UtcTicks}, {Now.UtcTicks}, {"Executar"}, 1, {"src/Eco.Web"}, 0, 1);
                 """,
                Ct);

            await write.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO DevelopmentCommandParameters (Id, DevelopmentCommandId, Name, Label, Type, DefaultValue, IsRequired, Options, "Order")
                 VALUES ({Guid.CreateVersion7()}, {commandId}, {"profile"}, {"Perfil"}, 0, {"Development"}, 1, NULL, 0);
                 """,
                Ct);
        }

        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var directory = tag.AddDirectory("@eco", Repository, null, null, Now);
        var binding = tag.AddDirectoryCommand(directory.Id, commandId, Now);
        var task = TaskItem.Create("Feature X", Now);
        var execution = CommandExecution.Create(
            task.Id, null, commandId, binding.Id, "Executar", "dotnet run", Repository, CommandMode.Execute, false, Now);
        await SeedAsync(db, tag, task, execution);

        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        var command = (await new DevelopmentCommandRepository(read).ListAsync(Ct)).Single();
        command.Id.Should().Be(commandId);
        command.IsGlobal.Should().BeTrue();
        command.Alias.Should().Be("@run");
        command.Name.Should().Be("Executar");
        command.Mode.Should().Be(CommandMode.Terminal);
        command.WorkingDirectory.Should().Be("src/Eco.Web");
        command.KeepTerminalOpen.Should().BeFalse();
        command.RequiresConfirmation.Should().BeTrue();
        command.Parameters.Should().ContainSingle().Which.Label.Should().Be("Perfil");

        var stored = (await new TagRepository(read).FindByIdAsync(tag.Id, Ct))!.Directories.Single().Commands.Single();
        stored.Id.Should().Be(binding.Id);
        stored.DevelopmentCommandId.Should().Be(commandId);

        var history = await read.CommandExecutions.SingleAsync(Ct);
        history.Id.Should().Be(execution.Id);
        history.DevelopmentCommandId.Should().Be(commandId, "a reconstrução da tabela não pode soltar o histórico");
        history.TagDirectoryCommandId.Should().Be(binding.Id);
    }
}
