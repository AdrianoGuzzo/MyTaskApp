using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// Comandos globais e a lista pós-Worktree contra SQLite de verdade (ADR-028):
/// as tabelas novas, o apelido único sem diferenciar maiúsculas e a ordem.
/// </summary>
public class DevelopmentCommandPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private const string Repository = @"C:\Projects\ecossistema-core";
    private const string Worktree = @"C:\Projects\ecossistema-core-feature-x";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AGlobalCommand_IsCreatedEditedAndDeleted()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var created = DevelopmentCommand.Create("@restore", "dotnet restore", "Restaura", Now);

        await using (var write = db.CreateContext())
        {
            await new DevelopmentCommandRepository(write).AddAsync(created, Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using (var edit = db.CreateContext())
        {
            var repository = new DevelopmentCommandRepository(edit);
            var stored = await repository.FindByIdAsync(created.Id, Ct);
            stored!.Update("@restore", "dotnet restore --locked-mode", null, Now.AddHours(1));
            await edit.SaveChangesAsync(Ct);
        }

        await using (var read = db.CreateContext())
        {
            var all = await new DevelopmentCommandRepository(read).ListAsync(Ct);
            all.Should().ContainSingle();
            all[0].Command.Should().Be("dotnet restore --locked-mode");
            all[0].Description.Should().BeNull();
            all[0].CreatedAt.Should().Be(Now);
            all[0].UpdatedAt.Should().Be(Now.AddHours(1));
        }

        await using (var delete = db.CreateContext())
        {
            var repository = new DevelopmentCommandRepository(delete);
            repository.Remove((await repository.FindByIdAsync(created.Id, Ct))!);
            await delete.SaveChangesAsync(Ct);
        }

        await using var final = db.CreateContext();
        (await final.DevelopmentCommands.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task TheAlias_IsUnique_IgnoringCase()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            write.DevelopmentCommands.Add(DevelopmentCommand.Create("@restore", "dotnet restore", null, Now));
            await write.SaveChangesAsync(Ct);
        }

        await using (var check = db.CreateContext())
        {
            var repository = new DevelopmentCommandRepository(check);
            (await repository.AliasExistsAsync("@RESTORE", null, Ct)).Should().BeTrue();
            (await repository.AliasExistsAsync("@build", null, Ct)).Should().BeFalse();
        }

        await using var duplicate = db.CreateContext();
        duplicate.DevelopmentCommands.Add(DevelopmentCommand.Create("@Restore", "npm ci", null, Now));

        var save = () => duplicate.SaveChangesAsync(Ct);
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task TheTaskList_IsSavedInOrder_AndReorderingSticks()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Feature X", Now);

        await using (var seed = db.CreateContext())
        {
            seed.Tasks.Add(task);
            await seed.SaveChangesAsync(Ct);
        }

        await using (var write = db.CreateContext())
        {
            var tracked = await new TaskItemRepository(write).FindByIdAsync(task.Id, Ct);
            tracked!.BeginDevelopment(Repository, "origin/develop", "feature/x", Worktree, Now);
            tracked.SetDevelopmentCommands(["@restore", "@npm-install", "dotnet ef database update"], Now);
            tracked.MarkDevelopmentReady(Now);
            await write.SaveChangesAsync(Ct);
        }

        await using (var reorder = db.CreateContext())
        {
            var tracked = await new TaskItemRepository(reorder).FindByIdAsync(task.Id, Ct);
            tracked!.Development!.Commands.Select(command => command.Command)
                .Should().Equal("@restore", "@npm-install", "dotnet ef database update");

            tracked.SetDevelopmentCommands(["dotnet ef database update", "@restore", "@build", "@test"], Now);
            await reorder.SaveChangesAsync(Ct);
        }

        await using (var shrink = db.CreateContext())
        {
            var tracked = await new TaskItemRepository(shrink).FindByIdAsync(task.Id, Ct);
            tracked!.Development!.Commands.Select(command => command.Command)
                .Should().Equal("dotnet ef database update", "@restore", "@build", "@test");

            tracked.SetDevelopmentCommands(["@build"], Now);
            await shrink.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var stored = await new TaskItemRepository(read).FindByIdAsync(task.Id, Ct);
        stored!.Development!.Commands.Select(command => (command.Command, command.Order))
            .Should().Equal(("@build", 0));
        (await read.TaskDevelopmentCommands.CountAsync(Ct)).Should().Be(1);
    }

    /// <summary>Ambientes de antes do ADR-028 sobem com a lista vazia: o fluxo de sempre.</summary>
    [Fact]
    public async Task DevelopmentsFromBeforeTheMigration_ComeBackWithNoCommands()
    {
        await using var db = await new TempSqliteDatabase().MigrateToAsync("TaskDevelopment", Ct);
        var taskId = Guid.CreateVersion7();

        await using (var write = db.CreateContext())
        {
            await write.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO Tasks (Id, Title, Description, Priority, CreatedAt)
                 VALUES ({taskId}, {"Tarefa antiga"}, NULL, 0, {Now.UtcTicks});
                 """,
                Ct);

            await write.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO TaskOccurrences (Id, TaskItemId, ScheduledDate, ScheduledTime, Status, CompletedAt)
                 VALUES ({Guid.CreateVersion7()}, {taskId}, NULL, NULL, 0, NULL);
                 """,
                Ct);

            await write.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO TaskDevelopments
                     (Id, TaskItemId, RepositoryPath, SourceBranch, Branch, WorktreePath, Status, CreatedAt, StatusChangedAt, FailureReason)
                 VALUES ({Guid.CreateVersion7()}, {taskId}, {Repository}, {"origin/develop"}, {"feature/x"}, {Worktree}, 2, {Now.UtcTicks}, {Now.UtcTicks}, NULL);
                 """,
                Ct);
        }

        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        var task = await new TaskItemRepository(read).FindByIdAsync(taskId, Ct);
        task!.Development!.Status.Should().Be(TaskDevelopmentStatus.Ready);
        task.Development.Commands.Should().BeEmpty();
        (await read.DevelopmentCommands.CountAsync(Ct)).Should().Be(0);
    }
}
