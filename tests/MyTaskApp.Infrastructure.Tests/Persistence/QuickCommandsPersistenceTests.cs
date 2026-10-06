using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// Comandos rápidos contra SQLite de verdade (ADR-051): as configurações do
/// global, a associação ao diretório, o histórico, as cascatas e o upgrade.
/// </summary>
public class QuickCommandsPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private const string Repository = @"C:\Projects\ecossistema-core";
    private const string Worktree = @"C:\Projects\ecossistema-core-feature-x";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SeedAsync(TempSqliteDatabase db, params object[] entities)
    {
        await using var context = db.CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }

    private static DevelopmentCommand Run() =>
        DevelopmentCommand.Create(
            "@run",
            "dotnet run --launch-profile {profile}",
            null,
            new DevelopmentCommandSettings(
                "Executar aplicação",
                CommandMode.Terminal,
                "src/Eco.Web",
                KeepTerminalOpen: false,
                RequiresConfirmation: true,
                [new CommandParameterSpec("profile", "Perfil", CommandParameterType.Choice, "Development", true, ["Development", "Staging"])]),
            Now);

    private static CommandExecution Execution(Guid taskId, Guid? developmentId, Guid? commandId, Guid? bindingId, DateTimeOffset at) =>
        CommandExecution.Create(taskId, developmentId, commandId, bindingId, "Executar", "dotnet run", Worktree, CommandMode.Execute, false, at);

    [Fact]
    public async Task ACommand_RoundTripsModeFolderFlagsAndParameters()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var run = Run();
        await SeedAsync(db, run);

        await using (var edit = db.CreateContext())
        {
            var stored = await new DevelopmentCommandRepository(edit).FindByIdAsync(run.Id, Ct);
            stored!.Update(
                "@run",
                "dotnet run --launch-profile {profile} --urls {urls}",
                null,
                stored.Settings with
                {
                    Parameters = [.. stored.Settings.Parameters!, new CommandParameterSpec("urls", IsRequired: false)],
                },
                Now.AddHours(1));
            await edit.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var reloaded = (await new DevelopmentCommandRepository(read).ListAsync(Ct)).Single();

        reloaded.Name.Should().Be("Executar aplicação");
        reloaded.Mode.Should().Be(CommandMode.Terminal);
        reloaded.WorkingDirectory.Should().Be("src/Eco.Web");
        reloaded.KeepTerminalOpen.Should().BeFalse();
        reloaded.RequiresConfirmation.Should().BeTrue();
        reloaded.Parameters.Select(parameter => parameter.ToSpec()).Should().BeEquivalentTo(
            [
                new CommandParameterSpec("profile", "Perfil", CommandParameterType.Choice, "Development", true, ["Development", "Staging"]),
                new CommandParameterSpec("urls", IsRequired: false),
            ],
            options => options.WithStrictOrdering());
        (await read.DevelopmentCommandParameters.CountAsync(Ct)).Should().Be(2);
    }

    [Fact]
    public async Task TheDirectoryCommands_RoundTripThroughTheTag_AndTheQueries()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var run = Run();
        var test = DevelopmentCommand.Create("@test", "dotnet test", null, Now);
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var directory = tag.AddDirectory("@ecossistema-core", Repository, null, null, Now);
        await SeedAsync(db, run, test, tag);

        await using (var edit = db.CreateContext())
        {
            var stored = await new TagRepository(edit).FindByIdAsync(tag.Id, Ct);
            var binding = stored!.AddDirectoryCommand(directory.Id, run.Id, Now);
            var second = stored.AddDirectoryCommand(directory.Id, test.Id, Now);
            stored.CustomizeDirectoryCommand(directory.Id, binding.Id, "dotnet run --project src/Eco.Web", ".");
            stored.SetDirectoryCommandEnabled(directory.Id, second.Id, false);
            stored.MoveDirectoryCommand(directory.Id, second.Id, -1);
            await edit.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var query = new TagQuery(read);

        var commands = await query.ListDirectoryCommandsAsync(directory.Id, Ct);
        commands.Select(row => row.Alias).Should().Equal("@test", "@run");
        commands[0].IsEnabled.Should().BeFalse();
        commands[1].EffectiveCommand.Should().Be("dotnet run --project src/Eco.Web");
        commands[1].WorkingDirectoryOverride.Should().Be(CommandWorkingDirectory.Root);
        commands[1].DisplayName.Should().Be("Executar aplicação");

        var directories = await query.ListCommandDirectoriesAsync(Ct);
        directories.Should().ContainSingle().Which.Path.Should().Be(Repository);
        directories[0].TagName.Should().Be("ECO CORE");
        directories[0].Commands.Should().HaveCount(2);

        (await query.ListDirectoriesAsync(tag.Id, Ct)).Single().CommandCount.Should().Be(2);

        var counts = await new DevelopmentCommandRepository(read).CountBindingsAsync(Ct);
        counts.Should().BeEquivalentTo(new Dictionary<Guid, int> { [run.Id] = 1, [test.Id] = 1 });
    }

    [Fact]
    public async Task AGlobalInTheSameDirectoryTwice_IsRefusedByTheDatabase()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var run = Run();
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var directory = tag.AddDirectory("@eco", Repository, null, null, Now);
        tag.AddDirectoryCommand(directory.Id, run.Id, Now);
        await SeedAsync(db, run, tag);

        await using var write = db.CreateContext();

        var insert = () => write.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO TagDirectoryCommands (Id, TagDirectoryId, DevelopmentCommandId, "Order", IsEnabled, CreatedAt)
             VALUES ({Guid.CreateVersion7()}, {directory.Id}, {run.Id}, 1, 1, {Now.UtcTicks});
             """,
            Ct);

        await insert.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
    }

    [Fact]
    public async Task DeletingAGlobal_RemovesItsBindings_AndUnlinksItsHistory()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var run = Run();
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var directory = tag.AddDirectory("@eco", Repository, null, null, Now);
        var binding = tag.AddDirectoryCommand(directory.Id, run.Id, Now);
        var task = TaskItem.Create("Feature X", Now);
        var execution = Execution(task.Id, null, run.Id, binding.Id, Now);
        await SeedAsync(db, run, tag, task, execution);

        await using (var delete = db.CreateContext())
        {
            var commands = new DevelopmentCommandRepository(delete);
            commands.Remove((await commands.FindByIdAsync(run.Id, Ct))!);
            await delete.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await read.TagDirectoryCommands.CountAsync(Ct)).Should().Be(0);
        (await read.DevelopmentCommandParameters.CountAsync(Ct)).Should().Be(0);
        var kept = await read.CommandExecutions.SingleAsync(Ct);
        kept.DevelopmentCommandId.Should().BeNull();
        kept.TagDirectoryCommandId.Should().BeNull();
        kept.CommandLine.Should().Be("dotnet run");
    }

    [Fact]
    public async Task DeletingADirectory_RemovesItsBindings_ButNotTheGlobal()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var run = Run();
        var tag = Tag.Create("ECO CORE", "#22C55E", Now);
        var directory = tag.AddDirectory("@eco", Repository, null, null, Now);
        tag.AddDirectoryCommand(directory.Id, run.Id, Now);
        await SeedAsync(db, run, tag);

        await using (var edit = db.CreateContext())
        {
            var stored = await new TagRepository(edit).FindByIdAsync(tag.Id, Ct);
            stored!.RemoveDirectory(directory.Id);
            await edit.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await read.TagDirectoryCommands.CountAsync(Ct)).Should().Be(0);
        (await read.DevelopmentCommands.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Executions_RoundTrip_AndListNewestFirst_AndTheActiveOnes()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Feature X", Now);
        var development = task.BeginDevelopment(null, Repository, "main", "feature/x", Worktree, Now);
        task.MarkDevelopmentReady(development.Id, Now);

        var finished = Execution(task.Id, development.Id, null, null, Now);
        finished.MarkRunning();
        finished.Finish(1, "saída", "erro", Now.AddSeconds(5));

        var terminal = CommandExecution.Create(
            task.Id, development.Id, null, null, "Executar", "dotnet run", Worktree, CommandMode.Terminal, true, Now.AddMinutes(1));
        terminal.MarkRunning(4242, Now.AddMinutes(1));

        await SeedAsync(db, task, finished, terminal);

        await using var read = db.CreateContext();
        var repository = new CommandExecutionRepository(read);

        var listed = await repository.ListForDevelopmentAsync(development.Id, Ct);
        listed.Select(execution => execution.Id).Should().Equal(terminal.Id, finished.Id);
        listed[1].ExitCode.Should().Be(1);
        listed[1].Output.Should().Be("saída");
        listed[1].ErrorOutput.Should().Be("erro");
        listed[1].FinishedAt.Should().Be(Now.AddSeconds(5));
        listed[0].ProcessId.Should().Be(4242);
        listed[0].ProcessStartedAt.Should().Be(Now.AddMinutes(1));
        listed[0].KeepTerminalOpen.Should().BeTrue();

        (await repository.ListActiveAsync(Ct)).Should().ContainSingle().Which.Id.Should().Be(terminal.Id);
    }

    [Fact]
    public async Task ForgettingADevelopment_KeepsItsExecutionsUnlinked_AndPurgingTheTaskRemovesThem()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Feature X", Now);
        var development = task.BeginDevelopment(null, Repository, "main", "feature/x", Worktree, Now);
        task.MarkDevelopmentReady(development.Id, Now);
        task.MarkDevelopmentRemoved(development.Id, Now);
        var execution = Execution(task.Id, development.Id, null, null, Now);
        await SeedAsync(db, task, execution);

        await using (var edit = db.CreateContext())
        {
            var stored = await new TaskItemRepository(edit).FindByIdAsync(task.Id, Ct);
            stored!.ForgetDevelopment(development.Id);
            await edit.SaveChangesAsync(Ct);
        }

        await using (var read = db.CreateContext())
        {
            (await read.CommandExecutions.SingleAsync(Ct)).TaskDevelopmentId.Should().BeNull();
        }

        await using (var purge = db.CreateContext())
        {
            var repository = new TaskItemRepository(purge);
            repository.Remove((await repository.FindByIdAsync(task.Id, Ct))!);
            await purge.SaveChangesAsync(Ct);
        }

        await using var after = db.CreateContext();
        (await after.CommandExecutions.CountAsync(Ct)).Should().Be(0);
    }

    /// <summary>
    /// Quem atualiza continua com os comandos de antes, rodando como sempre:
    /// escondidos, na raiz, sem perguntar — e com o terminal aberto por padrão.
    /// </summary>
    [Fact]
    public async Task GlobalCommandsFromBefore_BecomeHiddenCommandsAtTheRoot()
    {
        await using var db = await new TempSqliteDatabase().MigrateToAsync("TaskDeadlines", Ct);
        var commandId = Guid.CreateVersion7();
        var tagId = Guid.CreateVersion7();
        var directoryId = Guid.CreateVersion7();

        await using (var write = db.CreateContext())
        {
            await write.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO DevelopmentCommands (Id, Alias, Command, Description, CreatedAt, UpdatedAt)
                 VALUES ({commandId}, {"@restore"}, {"dotnet restore"}, NULL, {Now.UtcTicks}, {Now.UtcTicks});
                 """,
                Ct);

            await write.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO Tags (Id, Name, ColorHex, CreatedAt)
                 VALUES ({tagId}, {"ECO CORE"}, {"#22C55E"}, {Now.UtcTicks});
                 """,
                Ct);

            await write.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO TagDirectories (Id, TagId, Alias, Path, Name, Description, DefaultBranch, CreatedAt)
                 VALUES ({directoryId}, {tagId}, {"@eco"}, {Repository}, NULL, NULL, NULL, {Now.UtcTicks});
                 """,
                Ct);
        }

        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        var command = (await new DevelopmentCommandRepository(read).ListAsync(Ct)).Single();
        command.Alias.Should().Be("@restore");
        command.Command.Should().Be("dotnet restore");
        command.Name.Should().BeNull();
        command.Mode.Should().Be(CommandMode.Execute);
        command.WorkingDirectory.Should().BeNull();
        command.KeepTerminalOpen.Should().BeTrue();
        command.RequiresConfirmation.Should().BeFalse();
        command.Parameters.Should().BeEmpty();

        var tag = await new TagRepository(read).FindByIdAsync(tagId, Ct);
        tag!.Directories.Single().Commands.Should().BeEmpty();
        (await read.CommandExecutions.CountAsync(Ct)).Should().Be(0);
    }
}
