using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// O ambiente de desenvolvimento da tarefa contra SQLite de verdade (ADR-027):
/// a tabela nova, o id que nasce no domínio e um ambiente por repositório (ADR-031).
/// </summary>
public class TaskDevelopmentPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private const string Repository = @"C:\Projects\ecossistema-core";
    private const string Worktree = @"C:\Projects\ecossistema-core-feature-x";

    private const string OtherRepository = @"C:\Projects\ecossistema-api";
    private const string OtherWorktree = @"C:\Projects\ecossistema-api-feature-x";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TaskItem> SeedAsync(TempSqliteDatabase db)
    {
        var task = TaskItem.Create("Corrigir cálculo de animais", Now);

        await using var context = db.CreateContext();
        context.Tasks.Add(task);
        await context.SaveChangesAsync(Ct);

        return task;
    }

    /// <summary>
    /// A armadilha do id gerado no domínio: sem <c>ValueGeneratedNever</c>, o
    /// ambiente criado numa tarefa já rastreada virava UPDATE e falhava.
    /// </summary>
    [Fact]
    public async Task ADevelopmentAddedToATrackedTask_IsInserted_AndComesBack()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var seeded = await SeedAsync(db);

        await using (var write = db.CreateContext())
        {
            var task = await new TaskItemRepository(write).FindByIdAsync(seeded.Id, Ct);
            task!.Developments.Should().BeEmpty();

            task.BeginDevelopment(null, Repository, "origin/develop", "feature/x", Worktree, Now);
            await write.SaveChangesAsync(Ct);

            task.MarkDevelopmentReady(task.Developments[0].Id, Now.AddMinutes(1));
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var stored = await new TaskItemRepository(read).FindByIdAsync(seeded.Id, Ct);
        var development = stored!.Developments.Should().ContainSingle().Subject;

        development.TaskItemId.Should().Be(seeded.Id);
        development.RepositoryPath.Should().Be(Repository);
        development.SourceBranch.Should().Be("origin/develop");
        development.Branch.Should().Be("feature/x");
        development.WorktreePath.Should().Be(Worktree);
        development.Status.Should().Be(TaskDevelopmentStatus.Ready);
        development.CreatedAt.Should().Be(Now);
        development.StatusChangedAt.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public async Task StartingAgainAfterRemoval_ReusesTheRow()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var seeded = await SeedAsync(db);

        await using (var write = db.CreateContext())
        {
            var task = await new TaskItemRepository(write).FindByIdAsync(seeded.Id, Ct);
            task!.BeginDevelopment(null, Repository, "main", "feature/x", Worktree, Now);
            task.MarkDevelopmentReady(task.Developments[0].Id, Now);
            task.MarkDevelopmentRemoved(task.Developments[0].Id, Now);
            await write.SaveChangesAsync(Ct);
        }

        await using (var write = db.CreateContext())
        {
            var task = await new TaskItemRepository(write).FindByIdAsync(seeded.Id, Ct);
            task!.BeginDevelopment(null, Repository, "develop", "feature/y", Worktree + "-y", Now.AddDays(1));
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var rows = await read.TaskDevelopments.ToListAsync(Ct);
        rows.Should().ContainSingle().Which.Branch.Should().Be("feature/y");
    }

    [Fact]
    public async Task PurgingTheTask_TakesTheDevelopmentAlong()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var seeded = await SeedAsync(db);

        await using (var write = db.CreateContext())
        {
            var task = await new TaskItemRepository(write).FindByIdAsync(seeded.Id, Ct);
            task!.BeginDevelopment(null, Repository, "main", "feature/x", Worktree, Now);
            await write.SaveChangesAsync(Ct);
        }

        await using (var purge = db.CreateContext())
        {
            var task = await new TaskItemRepository(purge).FindByIdAsync(seeded.Id, Ct);
            new TaskItemRepository(purge).Remove(task!);
            await purge.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await read.TaskDevelopments.CountAsync(Ct)).Should().Be(0);
    }

    /// <summary>Um ambiente por repositório, na mesma tarefa (ADR-031).</summary>
    [Fact]
    public async Task TwoRepositories_AreTwoEnvironments_InTheOrderTheyWereCreated()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var seeded = await SeedAsync(db);

        await using (var write = db.CreateContext())
        {
            var task = await new TaskItemRepository(write).FindByIdAsync(seeded.Id, Ct);
            task!.BeginDevelopment(null, Repository, "main", "feature/x", Worktree, Now);
            await write.SaveChangesAsync(Ct);
        }

        await using (var write = db.CreateContext())
        {
            var task = await new TaskItemRepository(write).FindByIdAsync(seeded.Id, Ct);
            task!.BeginDevelopment(null, OtherRepository, "main", "feature/x", OtherWorktree, Now.AddMinutes(1));
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var stored = await new TaskItemRepository(read).FindByIdAsync(seeded.Id, Ct);
        stored!.Developments.Select(development => development.RepositoryPath).Should().Equal(Repository, OtherRepository);
    }

    /// <summary>
    /// Tirar da lista apaga o ambiente e os comandos dele, mas não o histórico
    /// de sessões de agente: a sessão só perde o vínculo.
    /// </summary>
    [Fact]
    public async Task Forgetting_DeletesTheRowAndItsCommands_ButKeepsTheAgentHistory()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var seeded = await SeedAsync(db);
        Guid sessionId;

        await using (var write = db.CreateContext())
        {
            var task = await new TaskItemRepository(write).FindByIdAsync(seeded.Id, Ct);
            var development = task!.BeginDevelopment(null, Repository, "main", "feature/x", Worktree, Now);
            task.SetDevelopmentCommands(development.Id, ["dotnet restore"], Now);
            task.MarkDevelopmentReady(development.Id, Now);

            var session = AgentSession.Create(task.Id, development.Id, "claude-code", @"C:\claude.exe", Worktree, Now);
            session.MarkRunning(4242, Now);
            session.MarkExited(Now.AddMinutes(5));
            sessionId = session.Id;
            write.AgentSessions.Add(session);

            task.MarkDevelopmentRemoved(development.Id, Now.AddMinutes(6));
            await write.SaveChangesAsync(Ct);
        }

        await using (var write = db.CreateContext())
        {
            var task = await new TaskItemRepository(write).FindByIdAsync(seeded.Id, Ct);
            task!.ForgetDevelopment(task.Developments[0].Id);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await read.TaskDevelopments.CountAsync(Ct)).Should().Be(0);
        (await read.TaskDevelopmentCommands.CountAsync(Ct)).Should().Be(0);
        var kept = await read.AgentSessions.SingleAsync(session => session.Id == sessionId, Ct);
        kept.TaskDevelopmentId.Should().BeNull();
    }

    /// <summary>
    /// Tarefas de antes do ADR-027 sobem sem ambiente nenhum e sem configurar
    /// nada: a migration só cria a tabela.
    /// </summary>
    [Fact]
    public async Task TasksFromBeforeTheMigration_ComeBackWithoutDevelopment()
    {
        await using var db = await new TempSqliteDatabase().MigrateToAsync("TagDirectories", Ct);
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
        }

        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        var task = await new TaskItemRepository(read).FindByIdAsync(taskId, Ct);
        task!.Title.Should().Be("Tarefa antiga");
        task.Developments.Should().BeEmpty();
    }
}
