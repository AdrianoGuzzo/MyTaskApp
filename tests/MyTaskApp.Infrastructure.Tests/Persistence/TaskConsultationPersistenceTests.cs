using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Consultations;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// A Consulta Inteligente contra SQLite de verdade (ADR-081): a configuração
/// da tarefa, o que a apaga e o que não, e as sessões de consulta.
/// </summary>
public class TaskConsultationPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 14, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TaskItem> SeedTaskAsync(TempSqliteDatabase db)
    {
        var task = TaskItem.Create("Pedido pendente", Now);

        await using var context = db.CreateContext();
        context.Tasks.Add(task);
        await context.SaveChangesAsync(Ct);

        return task;
    }

    private static AgentSession Consultation(TaskItem task, DateTimeOffset? at = null) =>
        AgentSession.CreateConsultation(task.Id, "claude-code", @"C:\claude.exe", @"C:\Projetos\loja", Guid.NewGuid().ToString(), at ?? Now);

    [Fact]
    public async Task TheConsultation_SurvivesAReopen_WithTheFoldersInOrder()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        var tag = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());

        await using (var context = db.CreateContext())
        {
            var consultation = TaskConsultation.Create(task.Id, Now);
            consultation.SetSubject(tag, Now);
            consultation.SetSources(
                [new(ConsultationSourceKind.TagDirectory, second), new(ConsultationSourceKind.Worktree, first)], Now);
            consultation.SetDatabase(connection, "loja_copia", Now);
            consultation.SetInstructions("Responda curto.", Now);
            consultation.SetLaunchPreferences("opus", "high", Now);
            consultation.RememberQuestion("Por que está pendente?", Now);

            await new TaskConsultationRepository(context).AddAsync(consultation, Ct);
            await context.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var stored = await new TaskConsultationRepository(read).FindAsync(task.Id, Ct);

        stored.Should().NotBeNull();
        stored!.TagId.Should().Be(tag);
        stored.Sources.Select(source => source.ReferenceId).Should().Equal(second, first);
        stored.DatabaseConnectionId.Should().Be(connection);
        stored.DatabaseName.Should().Be("loja_copia");
        stored.Instructions.Should().Be("Responda curto.");
        stored.Model.Should().Be("opus");
        stored.Effort.Should().Be("high");
        stored.LastQuestion.Should().Be("Por que está pendente?");
    }

    [Fact]
    public async Task ChangingTheFolders_ReplacesThem()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        var kept = Guid.NewGuid();

        await using (var context = db.CreateContext())
        {
            var consultation = TaskConsultation.Create(task.Id, Now);
            consultation.SetSources([new(ConsultationSourceKind.TagDirectory, Guid.NewGuid())], Now);
            await new TaskConsultationRepository(context).AddAsync(consultation, Ct);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = db.CreateContext())
        {
            var consultation = await new TaskConsultationRepository(context).FindAsync(task.Id, Ct);
            consultation!.SetSources([new(ConsultationSourceKind.TagDirectory, kept)], Now);
            await context.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await read.Set<TaskConsultationSource>().Select(source => source.ReferenceId).ToListAsync(Ct)).Should().Equal(kept);
    }

    [Fact]
    public async Task Clearing_RemovesOnlyTheConsultation()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        var connection = DatabaseConnection.Create("Local", "localhost", 5432, null, "postgres", DatabaseEnvironment.Development,
            DatabaseSslMode.Disable, null, ConnectionPermissions.FromFlags(ConnectionPermission.All), Now);

        await using (var context = db.CreateContext())
        {
            context.Add(connection);
            var consultation = TaskConsultation.Create(task.Id, Now);
            consultation.SetDatabase(connection.Id, "loja_copia", Now);
            consultation.SetSources([new(ConsultationSourceKind.TagDirectory, Guid.NewGuid())], Now);
            await new TaskConsultationRepository(context).AddAsync(consultation, Ct);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = db.CreateContext())
        {
            var repository = new TaskConsultationRepository(context);
            repository.Remove((await repository.FindAsync(task.Id, Ct))!);
            await context.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await read.Set<TaskConsultation>().CountAsync(Ct)).Should().Be(0);
        (await read.Set<TaskConsultationSource>().CountAsync(Ct)).Should().Be(0);
        (await read.Set<DatabaseConnection>().CountAsync(Ct)).Should().Be(1);
        (await read.Tasks.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task DeletingTheConnection_IsNotBlockedByAConsultation()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        var connection = DatabaseConnection.Create("Local", "localhost", 5432, null, "postgres", DatabaseEnvironment.Development,
            DatabaseSslMode.Disable, null, ConnectionPermissions.FromFlags(ConnectionPermission.All), Now);

        await using (var context = db.CreateContext())
        {
            context.Add(connection);
            var consultation = TaskConsultation.Create(task.Id, Now);
            consultation.SetDatabase(connection.Id, "loja_copia", Now);
            await new TaskConsultationRepository(context).AddAsync(consultation, Ct);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = db.CreateContext())
        {
            context.Remove(await context.Set<DatabaseConnection>().SingleAsync(Ct));
            await context.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await new TaskConsultationRepository(read).FindAsync(task.Id, Ct))!.DatabaseConnectionId.Should().Be(connection.Id);
    }

    [Fact]
    public async Task OneOpenConsultation_PerTask()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);

        await using (var context = db.CreateContext())
        {
            await new AgentSessionRepository(context).AddAsync(Consultation(task), Ct);
            await context.SaveChangesAsync(Ct);
        }

        await using var second = db.CreateContext();
        await new AgentSessionRepository(second).AddAsync(Consultation(task, Now.AddMinutes(1)), Ct);

        var act = () => second.SaveChangesAsync(Ct);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task TheSecret_FindsOnlyTheOpenConsultation()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = await SeedTaskAsync(db);
        var (token, hash) = AgentHookToken.Create();
        var session = Consultation(task);
        session.GrantConsultationAccess(hash);
        session.MarkRunning(4242, Now);

        await using (var context = db.CreateContext())
        {
            await new AgentSessionRepository(context).AddAsync(session, Ct);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = db.CreateContext())
        {
            (await new AgentSessionRepository(context).FindActiveConsultationByTokenHashAsync(AgentHookToken.Hash(token), Ct))!
                .Id.Should().Be(session.Id);

            var stored = await new AgentSessionRepository(context).FindByIdAsync(session.Id, Ct);
            stored!.MarkExited(Now.AddMinutes(3));
            await context.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await new AgentSessionRepository(read).FindActiveConsultationByTokenHashAsync(AgentHookToken.Hash(token), Ct)).Should().BeNull();
        (await new AgentSessionRepository(read).ListConsultationsForTaskAsync(task.Id, 10, Ct)).Should().ContainSingle()
            .Which.Kind.Should().Be(AgentSessionKind.Consultation);
    }
}
