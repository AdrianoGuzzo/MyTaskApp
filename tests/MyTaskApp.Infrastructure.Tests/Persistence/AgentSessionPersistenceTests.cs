using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// As sessões de agente contra SQLite de verdade (ADR-030): o que sobrevive ao
/// app fechar, a sessão ativa única por tarefa e o selo da lista.
/// </summary>
public class AgentSessionPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 18, 42, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 23);

    private const string Claude = @"C:\Users\dev\.local\bin\claude.exe";
    private const string Worktree = @"C:\Projects\eco-core-feature-123";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TaskItem> SeedTaskAsync(TempSqliteDatabase db, string title = "Implementar autenticação")
    {
        var task = TaskItem.Create(title, Now, schedule: TaskSchedule.At(Today, new TimeOnly(9, 0)));

        await using var context = db.CreateContext();
        context.Tasks.Add(task);
        await context.SaveChangesAsync(Ct);

        return task;
    }

    private static AgentSession Running(Guid taskId, int processId, DateTimeOffset? at = null)
    {
        var session = AgentSession.Create(taskId, "claude-code", Claude, Worktree, at ?? Now);
        session.MarkRunning(processId, (at ?? Now).AddMilliseconds(137));
        return session;
    }

    private static async Task AddAsync(TempSqliteDatabase db, params AgentSession[] sessions)
    {
        await using var context = db.CreateContext();
        var repository = new AgentSessionRepository(context);

        foreach (var session in sessions)
        {
            await repository.AddAsync(session, Ct);
        }

        await context.SaveChangesAsync(Ct);
    }

    /// <summary>O cenário de reabrir o app: tudo o que identifica o processo volta igual.</summary>
    [Fact]
    public async Task ARunningSession_SurvivesAReopen_WithEverythingThatIdentifiesTheProcess()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        var session = Running(task.Id, 15432);

        await AddAsync(db, session);

        await using var read = db.CreateContext();
        var stored = await new AgentSessionRepository(read).FindLatestForTaskAsync(task.Id, Ct);

        stored.Should().NotBeNull();
        stored!.Id.Should().Be(session.Id);
        stored.ProviderId.Should().Be("claude-code");
        stored.Command.Should().Be(Claude);
        stored.WorkingDirectory.Should().Be(Worktree);
        stored.ProcessId.Should().Be(15432);
        stored.ProcessStartedAt.Should().Be(Now.AddMilliseconds(137));
        stored.StartedAt.Should().Be(Now);
        stored.EndedAt.Should().BeNull();
        stored.Status.Should().Be(AgentSessionStatus.Running);
    }

    [Fact]
    public async Task TheLatestSession_IsTheOneShown_AndOnlyActivesAreListed()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        var old = Running(task.Id, 100, Now.AddHours(-2));
        old.MarkExited(Now.AddHours(-1));
        var current = Running(task.Id, 200);

        await AddAsync(db, old, current);

        await using var read = db.CreateContext();
        var repository = new AgentSessionRepository(read);

        (await repository.FindLatestForTaskAsync(task.Id, Ct))!.Id.Should().Be(current.Id);
        (await repository.ListActiveAsync(Ct)).Select(session => session.Id).Should().Equal(current.Id);
    }

    [Fact]
    public async Task EndingASession_IsSaved()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        var session = Running(task.Id, 15432);
        await AddAsync(db, session);

        await using (var write = db.CreateContext())
        {
            var stored = await new AgentSessionRepository(write).FindByIdAsync(session.Id, Ct);
            stored!.MarkExited(Now.AddMinutes(35));
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var ended = await new AgentSessionRepository(read).FindByIdAsync(session.Id, Ct);
        ended!.Status.Should().Be(AgentSessionStatus.Exited);
        ended.EndedAt.Should().Be(Now.AddMinutes(35));
    }

    /// <summary>O banco também recusa duas sessões ativas na mesma tarefa.</summary>
    [Fact]
    public async Task TwoActiveSessionsForOneTask_AreRefusedByTheDatabase()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);

        var add = () => AddAsync(db, Running(task.Id, 100), Running(task.Id, 200));

        await add.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task EndedSessions_DoNotCountAgainstTheActiveOne()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        var first = Running(task.Id, 100, Now.AddHours(-1));
        first.MarkExited(Now.AddMinutes(-30));
        var failed = AgentSession.Create(task.Id, "claude-code", Claude, Worktree, Now.AddMinutes(-10));
        failed.MarkFailed("não abriu", Now.AddMinutes(-10));

        var add = () => AddAsync(db, first, failed, Running(task.Id, 200));

        await add.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PurgingTheTask_TakesItsSessionsAlong()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        await AddAsync(db, Running(task.Id, 15432));

        await using (var purge = db.CreateContext())
        {
            var stored = await new TaskItemRepository(purge).FindByIdAsync(task.Id, Ct);
            new TaskItemRepository(purge).Remove(stored!);
            await purge.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await read.AgentSessions.CountAsync(Ct)).Should().Be(0);
    }

    /// <summary>O selo "● Claude" da lista vem só de sessão em execução.</summary>
    [Fact]
    public async Task TheTodayList_KnowsWhichTasksHaveARunningAgent()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var withAgent = await SeedTaskAsync(db, "Implementar autenticação");
        var finished = await SeedTaskAsync(db, "Corrigir consulta SQL");
        var without = await SeedTaskAsync(db, "Ajustar tela de login");

        var ended = Running(finished.Id, 100);
        ended.MarkExited(Now.AddMinutes(5));
        await AddAsync(db, Running(withAgent.Id, 15432), ended);

        await using var read = db.CreateContext();
        var rows = await new TodayQuery(read).GetCandidatesAsync(Today, Ct);

        rows.Single(row => row.TaskId == withAgent.Id).ActiveAgentProviderId.Should().Be("claude-code");
        rows.Single(row => row.TaskId == finished.Id).ActiveAgentProviderId.Should().BeNull();
        rows.Single(row => row.TaskId == without.Id).ActiveAgentProviderId.Should().BeNull();
    }
}
