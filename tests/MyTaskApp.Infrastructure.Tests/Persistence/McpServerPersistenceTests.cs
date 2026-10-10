using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Mcp;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// O que o servidor MCP grava e lê no SQLite (ADR-059): a configuração em linha
/// única, a transação que junta casos de uso, e as consultas novas de busca e
/// de horas.
/// </summary>
public class McpServerPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ANewInstall_ReadsTheFactorySettings_ServerOff()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await using var context = db.CreateContext();

        var settings = await Store(context).GetAsync(Ct);

        settings.Should().Be(McpServerSettings.Factory);
        settings.Enabled.Should().BeFalse();
        settings.Port.Should().Be(McpServerSettings.DefaultPort);
    }

    [Fact]
    public async Task Settings_RoundTrip()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            await Store(write).SaveAsync(new McpServerSettings(true, 6200, startWithApp: true, readOnly: true), Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using (var overwrite = db.CreateContext())
        {
            await Store(overwrite).SaveAsync(new McpServerSettings(true, 6201, startWithApp: false, readOnly: true), Ct);
            await overwrite.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        (await Store(read).GetAsync(Ct)).Should().Be(new McpServerSettings(true, 6201, false, true));
        (await read.McpServerSettings.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task ACorruptRow_FallsBackToOff()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            await write.Database.ExecuteSqlRawAsync(
                "INSERT INTO McpServerSettings (Id, Enabled, Port, StartWithApp, ReadOnly) VALUES (1, 1, 80, 1, 0)", Ct);
        }

        await using var read = db.CreateContext();
        (await Store(read).GetAsync(Ct)).Should().Be(McpServerSettings.Factory);
    }

    [Fact]
    public async Task UpgradingFromSkippedTables_KeepsTheData_AndStartsWithTheServerOff()
    {
        await using var db = await new TempSqliteDatabase().MigrateToAsync("SkippedTables", Ct);
        var task = TaskItem.Create("Antes do MCP", Now);

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(task);
            await write.SaveChangesAsync(Ct);
        }

        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        (await read.Tasks.SingleAsync(Ct)).Title.Should().Be("Antes do MCP");
        (await Store(read).GetAsync(Ct)).Enabled.Should().BeFalse();
        (await read.Database.GetPendingMigrationsAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task ATransaction_UndoesEverySaveInside_WhenOneStepFails()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var context = db.CreateContext())
        {
            var unitOfWork = new EfUnitOfWork(context);

            var attempt = () => unitOfWork.ExecuteInTransactionAsync(
                async token =>
                {
                    context.Tasks.Add(TaskItem.Create("Primeiro passo", Now));
                    await unitOfWork.SaveChangesAsync(token);

                    throw new InvalidOperationException("O segundo passo falhou.");
                },
                Ct);

            await attempt.Should().ThrowAsync<InvalidOperationException>();
        }

        await using var read = db.CreateContext();
        (await read.Tasks.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task ATransaction_KeepsEverySave_WhenAllStepsSucceed()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var context = db.CreateContext())
        {
            var unitOfWork = new EfUnitOfWork(context);

            await unitOfWork.ExecuteInTransactionAsync(
                async token =>
                {
                    context.Tasks.Add(TaskItem.Create("Um", Now));
                    await unitOfWork.SaveChangesAsync(token);
                    context.Tasks.Add(TaskItem.Create("Dois", Now));
                    await unitOfWork.SaveChangesAsync(token);
                },
                Ct);
        }

        await using var read = db.CreateContext();
        (await read.Tasks.CountAsync(Ct)).Should().Be(2);
    }

    [Fact]
    public async Task Searching_SeparatesTheMainList_FromArchiveAndTrash()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var active = TaskItem.Create("Ativa", Now);
        var archived = TaskItem.Create("Arquivada", Now.AddMinutes(1));
        var trashed = TaskItem.Create("Na lixeira", Now.AddMinutes(2));
        archived.CompleteOccurrence(archived.Occurrences.Single().Id, Now.AddMinutes(3));
        archived.Archive(Now.AddMinutes(4));
        trashed.MoveToTrash(Now.AddMinutes(5), "adria");

        await using (var write = db.CreateContext())
        {
            write.Tasks.AddRange(active, archived, trashed);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var query = new TaskSearchQuery(read);

        (await query.SearchAsync(new TaskSearchCriteria([TaskLifecycle.Active, TaskLifecycle.Completed]), Ct))
            .Select(row => row.Row.Title).Should().Equal("Ativa");

        (await query.SearchAsync(new TaskSearchCriteria([TaskLifecycle.Archived]), Ct))
            .Select(row => row.Row.Title).Should().Equal("Arquivada");

        var all = await query.SearchAsync(
            new TaskSearchCriteria([TaskLifecycle.Active, TaskLifecycle.Completed, TaskLifecycle.Archived, TaskLifecycle.Trashed]), Ct);
        all.Select(row => row.Row.Title).Should().Equal("Na lixeira", "Arquivada", "Ativa");
        all.Single(row => row.Row.Title == "Na lixeira").DeletedBy.Should().Be("adria");
    }

    [Fact]
    public async Task Searching_EscapesTheWildcardsTheUserTyped()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            write.Tasks.AddRange(TaskItem.Create("Desconto de 50%", Now), TaskItem.Create("Desconto de 500 reais", Now));
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        (await new TaskSearchQuery(read).SearchAsync(new TaskSearchCriteria([TaskLifecycle.Active], Text: "50%"), Ct))
            .Select(row => row.Row.Title).Should().Equal("Desconto de 50%");
    }

    [Fact]
    public async Task TimeReports_TakeThePeriodsThatTouchTheWindow()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Com horas", Now.AddDays(-5));
        var occurrence = task.Occurrences.Single().Id;
        var windowStart = Now.AddDays(-1);

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(task);
            write.TimeEntries.AddRange(
                TimeEntry.Manual(occurrence, windowStart.AddHours(-3), windowStart.AddHours(-2), "antes", Now),
                TimeEntry.Manual(occurrence, windowStart.AddMinutes(-30), windowStart.AddMinutes(30), "atravessa", Now),
                TimeEntry.Manual(occurrence, windowStart.AddHours(2), windowStart.AddHours(3), "dentro", Now));
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var rows = await new TimeEntryReportQuery(read).ListAsync(
            new MyTaskApp.Application.TimeTracking.TimeEntryReportCriteria(windowStart, Now), Ct);

        rows.Select(row => row.Note).Should().Equal("dentro", "atravessa");
        rows.Should().OnlyContain(row => row.TaskTitle == "Com horas" && row.Source == TimeEntrySource.Manual);
    }

    private static McpServerSettingsStore Store(MyTaskAppDbContext context) =>
        new(context, NullLogger<McpServerSettingsStore>.Instance);
}
