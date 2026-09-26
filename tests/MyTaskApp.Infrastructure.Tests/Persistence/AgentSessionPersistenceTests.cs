using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// As sessões de agente contra SQLite de verdade (ADR-030): o que sobrevive ao
/// app fechar, a sessão ativa única por ambiente (ADR-031) e o selo da lista.
/// </summary>
public class AgentSessionPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 18, 42, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 23);

    private const string Claude = @"C:\Users\dev\.local\bin\claude.exe";
    private const string Worktree = @"C:\Projects\eco-core-feature-123";
    private const string Repository = @"C:\Projects\eco-core";
    private const string ApiRepository = @"C:\Projects\eco-api";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TaskItem> SeedTaskAsync(TempSqliteDatabase db, string title = "Implementar autenticação")
    {
        var task = TaskItem.Create(title, Now, schedule: TaskSchedule.At(Today, new TimeOnly(9, 0)));
        var development = task.BeginDevelopment(null, Repository, "origin/develop", "feature/123", Worktree, Now);
        task.MarkDevelopmentReady(development.Id, Now);

        await using var context = db.CreateContext();
        context.Tasks.Add(task);
        await context.SaveChangesAsync(Ct);

        return task;
    }

    private static AgentSession Running(TaskItem task, int processId, DateTimeOffset? at = null, TaskDevelopment? development = null)
    {
        var session = AgentSession.Create(
            task.Id, (development ?? task.Developments[0]).Id, "claude-code", Claude, Worktree, at ?? Now);
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
        var session = Running(task, 15432);

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
        var old = Running(task, 100, Now.AddHours(-2));
        old.MarkExited(Now.AddHours(-1));
        var current = Running(task, 200);

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
        var session = Running(task, 15432);
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

    /// <summary>O banco também recusa duas sessões ativas no mesmo ambiente.</summary>
    [Fact]
    public async Task TwoActiveSessionsForOneEnvironment_AreRefusedByTheDatabase()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);

        var add = () => AddAsync(db, Running(task, 100), Running(task, 200));

        await add.Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>Um agente por ambiente (ADR-031): dois repositórios, dois agentes ao mesmo tempo.</summary>
    [Fact]
    public async Task TwoActiveSessions_InTwoEnvironmentsOfTheTask_AreAccepted()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        var api = await AddEnvironmentAsync(db, task, ApiRepository);

        await AddAsync(db, Running(task, 100), Running(task, 200, development: api));

        await using var read = db.CreateContext();
        var repository = new AgentSessionRepository(read);
        (await repository.ListActiveForTaskAsync(task.Id, Ct)).Should().HaveCount(2);
        (await repository.FindLatestForDevelopmentAsync(api.Id, Ct))!.ProcessId.Should().Be(200);
    }

    [Fact]
    public async Task TheTodayList_BringsOneAgentPerEnvironment()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        var api = await AddEnvironmentAsync(db, task, ApiRepository);

        await AddAsync(db, Running(task, 100, Now.AddMinutes(-5)), Running(task, 200, development: api));

        await using var read = db.CreateContext();
        var rows = await new TodayQuery(read).GetCandidatesAsync(Today, Ct);

        rows.Single(row => row.TaskId == task.Id).ActiveAgents!.Select(agent => agent.RepositoryPath)
            .Should().Equal(Repository, ApiRepository);
    }

    private static async Task<TaskDevelopment> AddEnvironmentAsync(TempSqliteDatabase db, TaskItem task, string repository)
    {
        await using var write = db.CreateContext();
        var tracked = await new TaskItemRepository(write).FindByIdAsync(task.Id, Ct);
        var development = tracked!.BeginDevelopment(null, repository, "origin/develop", "feature/123", repository + "-feature-123", Now);
        tracked.MarkDevelopmentReady(development.Id, Now);
        await write.SaveChangesAsync(Ct);
        return development;
    }

    [Fact]
    public async Task EndedSessions_DoNotCountAgainstTheActiveOne()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        var first = Running(task, 100, Now.AddHours(-1));
        first.MarkExited(Now.AddMinutes(-30));
        var failed = AgentSession.Create(task.Id, task.Developments[0].Id, "claude-code", Claude, Worktree, Now.AddMinutes(-10));
        failed.MarkFailed("não abriu", Now.AddMinutes(-10));

        var add = () => AddAsync(db, first, failed, Running(task, 200));

        await add.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PurgingTheTask_TakesItsSessionsAlong()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        await AddAsync(db, Running(task, 15432));

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

        var ended = Running(finished, 100);
        ended.MarkExited(Now.AddMinutes(5));
        await AddAsync(db, Running(withAgent, 15432), ended);

        await using var read = db.CreateContext();
        var rows = await new TodayQuery(read).GetCandidatesAsync(Today, Ct);

        rows.Single(row => row.TaskId == withAgent.Id).ActiveAgents.Should().ContainSingle()
            .Which.Should().Be(new ActiveAgentRow(withAgent.Developments[0].Id, "claude-code", Repository, "feature/123"));
        rows.Single(row => row.TaskId == finished.Id).ActiveAgents.Should().BeNull();
        rows.Single(row => row.TaskId == without.Id).ActiveAgents.Should().BeNull();
    }

    /// <summary>
    /// Antes do ADR-031, a tarefa tinha um ambiente só: a migration liga a
    /// sessão que já existia a ele, e o agente aberto continua achado.
    /// </summary>
    [Fact]
    public async Task SessionsFromBeforeMultipleEnvironments_AreLinkedToTheTasksEnvironment()
    {
        await using var db = await new TempSqliteDatabase().MigrateToAsync("AgentSessions", Ct);
        var taskId = Guid.CreateVersion7();
        var developmentId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();

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
                 VALUES ({developmentId}, {taskId}, {Repository}, {"origin/develop"}, {"feature/123"}, {Worktree}, 2, {Now.UtcTicks}, {Now.UtcTicks}, NULL);
                 """,
                Ct);

            await write.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO AgentSessions
                     (Id, TaskItemId, ProviderId, Command, WorkingDirectory, ProcessId, ProcessStartedAt, StartedAt, EndedAt, Status, FailureReason)
                 VALUES ({sessionId}, {taskId}, {"claude-code"}, {Claude}, {Worktree}, 15432, {Now.UtcTicks}, {Now.UtcTicks}, NULL, 2, NULL);
                 """,
                Ct);
        }

        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        var repository = new AgentSessionRepository(read);
        var session = await repository.FindLatestForDevelopmentAsync(developmentId, Ct);
        session!.Id.Should().Be(sessionId);
        session.TaskDevelopmentId.Should().Be(developmentId);

        var task = await new TaskItemRepository(read).FindByIdAsync(taskId, Ct);
        task!.Developments.Should().ContainSingle().Which.Id.Should().Be(developmentId);
    }

    /// <summary>
    /// O que os hooks disseram sobrevive ao app fechar (ADR-036): reabrir mostra
    /// "aguardando você", e não "em execução" sem mais nada.
    /// </summary>
    [Fact]
    public async Task TheAgentsActivity_SurvivesAReopen_AndReachesTheBadge()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        var hash = new string('B', AgentSession.HookTokenHashLength);

        var session = AgentSession.Create(task.Id, task.Developments[0].Id, "claude-code", Claude, Worktree, Now);
        session.EnableMonitoring(hash);
        session.MarkRunning(15432, Now);
        session.RecordExternalSession("7f3c-claude");
        session.RecordActivity(AgentActivity.WaitingForUser, "Redis ou MemoryCache?", Now.AddMinutes(9));
        await AddAsync(db, session);

        await using var read = db.CreateContext();
        var stored = await new AgentSessionRepository(read).FindByIdAsync(session.Id, Ct);

        stored!.HookTokenHash.Should().Be(hash);
        stored.ExternalSessionId.Should().Be("7f3c-claude");
        stored.Activity.Should().Be(AgentActivity.WaitingForUser);
        stored.ActivityMessage.Should().Be("Redis ou MemoryCache?");
        stored.ActivityChangedAt.Should().Be(Now.AddMinutes(9));

        var rows = await new TodayQuery(read).GetCandidatesAsync(Today, Ct);
        rows.Single().ActiveAgents!.Single().Activity.Should().Be(AgentActivity.WaitingForUser);
    }

    [Fact]
    public async Task ASessionWithoutMonitoring_KeepsNoSecret_AndNoActivity()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        var session = Running(task, 15432);
        await AddAsync(db, session);

        await using var read = db.CreateContext();
        var stored = await new AgentSessionRepository(read).FindByIdAsync(session.Id, Ct);

        stored!.IsMonitored.Should().BeFalse();
        stored.Activity.Should().Be(AgentActivity.Unknown);
        stored.ActivityChangedAt.Should().BeNull();
    }
}
