using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// O prazo contra SQLite de verdade (ADR-050): data e hora de parede, o que já
/// foi avisado e a configuração precisam voltar exatamente como foram.
/// </summary>
public class DeadlinePersistenceTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly TaskDeadline Friday = new(new DateOnly(2026, 10, 9), new TimeOnly(18, 30));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TaskItem TaskWithDeadline(string title, TaskDeadline? deadline = null)
    {
        var task = TaskItem.Create(title, Now, schedule: TaskSchedule.On(Today));
        task.SetOccurrenceDeadline(task.Occurrences[0].Id, deadline ?? Friday, DeadlineAlertStage.None);

        return task;
    }

    private static async Task SeedAsync(TempSqliteDatabase db, params object[] entities)
    {
        await using var context = db.CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }

    private static DeadlineSettingsStore Store(MyTaskAppDbContext context) =>
        new(context, NullLogger<DeadlineSettingsStore>.Instance);

    [Fact]
    public async Task TheDeadlineAndItsAlertState_SurviveTheRoundTrip()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskWithDeadline("Implementar módulo de nutrição");
        var occurrenceId = task.Occurrences[0].Id;
        task.MarkDeadlineAlerted(occurrenceId, DeadlineAlertStage.OneDay, Now);
        task.SnoozeDeadlineAlert(occurrenceId, Now.AddHours(1));
        task.ChangeDeadlineAlerts(DeadlineAlertStages.OnlyTheDayBefore);
        task.ChangePlan("Criar endpoint POST /diets", TimeSpan.FromHours(6.5));
        await SeedAsync(db, task);

        await using var read = db.CreateContext();
        var loaded = await new TaskItemRepository(read).FindByIdAsync(task.Id, Ct);
        var occurrence = loaded!.Occurrences[0];

        occurrence.Deadline.Should().Be(Friday);
        occurrence.DeadlineAlert.LastStage.Should().Be(DeadlineAlertStage.OneDay);
        occurrence.DeadlineAlert.LastAlertAtUtc.Should().Be(Now);
        occurrence.DeadlineAlert.SnoozedUntilUtc.Should().Be(Now.AddHours(1));
        loaded.DeadlineAlerts.Should().Be(DeadlineAlertStages.OnlyTheDayBefore);
        loaded.NextAction.Should().Be("Criar endpoint POST /diets");
        loaded.Estimate.Should().Be(TimeSpan.FromHours(6.5));
    }

    [Fact]
    public async Task ATaskWithoutDeadline_StaysWithout()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Comprar pão", Now, schedule: TaskSchedule.On(Today));
        await SeedAsync(db, task);

        await using var read = db.CreateContext();
        var loaded = await new TaskItemRepository(read).FindByIdAsync(task.Id, Ct);

        loaded!.Occurrences[0].Deadline.Should().BeNull();
        loaded.Occurrences[0].DeadlineAlert.LastStage.Should().Be(DeadlineAlertStage.None);
        loaded.DeadlineAlerts.Should().BeNull("nula é \"segue o padrão\", e não silenciosa");
        loaded.Estimate.Should().BeNull();
    }

    [Fact]
    public async Task ASilencedTask_IsNotConfusedWithTheDefault()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskWithDeadline("Publicar release");
        task.ChangeDeadlineAlerts(DeadlineAlertStage.None);
        await SeedAsync(db, task);

        await using var read = db.CreateContext();
        var loaded = await new TaskItemRepository(read).FindByIdAsync(task.Id, Ct);

        loaded!.DeadlineAlerts.Should().Be(DeadlineAlertStage.None);
    }

    [Fact]
    public async Task Settings_OnAFreshInstall_AreTheFactoryOnes()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await using var context = db.CreateContext();

        (await Store(context).GetAsync(Ct)).Should().Be(DeadlineSettings.Factory);
    }

    [Fact]
    public async Task Settings_WhatIsSaved_IsWhatComesBack_InASingleRow()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var custom = new DeadlineSettings(
            new DeadlineAlertPolicy(true, DeadlineAlertStage.ThreeDays | DeadlineAlertStage.Overdue, TimeSpan.FromHours(4)),
            new TimeOnly(17, 0));

        foreach (var settings in new[] { DeadlineSettings.Factory, custom })
        {
            await using var write = db.CreateContext();
            await Store(write).SaveAsync(settings, Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        (await read.DeadlineSettings.CountAsync(Ct)).Should().Be(1);
        (await Store(read).GetAsync(Ct)).Should().Be(custom);
    }

    [Fact]
    public async Task Settings_ACorruptedRow_DegradesToTheFactoryOnes()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            // Ligado sem degrau nenhum: a política recusa.
            await write.Database.ExecuteSqlAsync(
                $"INSERT INTO DeadlineSettings (Id, IsEnabled, Stages, OverdueRepeatEveryTicks, DefaultTime) VALUES (1, 1, 0, NULL, '18:00:00');",
                Ct);
        }

        await using var read = db.CreateContext();

        (await Store(read).GetAsync(Ct)).Should().Be(DeadlineSettings.Factory);
    }

    [Fact]
    public async Task TheAlertQuery_BringsOnlyOpenDeadlinesOfActiveChecklists()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var open = TaskWithDeadline("Implementar API", new TaskDeadline(Today, new TimeOnly(18, 0)));
        var later = TaskWithDeadline("Integração Jira");
        later.ChangeDeadlineAlerts(DeadlineAlertStage.None);
        var done = TaskWithDeadline("Revisar contrato");
        done.CompleteOccurrence(done.Occurrences[0].Id, Now);
        var archived = TaskWithDeadline("Planejar viagem");
        archived.Archive(Now);
        var noDeadline = TaskItem.Create("Comprar pão", Now, schedule: TaskSchedule.On(Today));
        await SeedAsync(db, open, later, done, archived, noDeadline);

        await using var read = db.CreateContext();
        var rows = await new DeadlineAlertQuery(read).GetCandidatesAsync(Ct);

        rows.Select(row => row.Title).Should().Equal("Implementar API", "Integração Jira");
        rows[0].Deadline.Should().Be(new TaskDeadline(Today, new TimeOnly(18, 0)));
        rows[0].TaskOverride.Should().BeNull();
        rows[1].TaskOverride.Should().Be(DeadlineAlertStage.None);
        rows[1].Alert.LastStage.Should().Be(DeadlineAlertStage.None);
    }

    [Fact]
    public async Task TheTodayQuery_BringsDeadlinesScheduledForAnyDay()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var future = TaskItem.Create("Preparar apresentação", Now, schedule: TaskSchedule.On(Today.AddDays(3)));
        future.SetOccurrenceDeadline(future.Occurrences[0].Id, Friday, DeadlineAlertStage.None);
        future.ChangePlan("Montar o roteiro", TimeSpan.FromHours(3));
        var futureWithout = TaskItem.Create("Ligar para o banco", Now, schedule: TaskSchedule.On(Today.AddDays(3)));
        await SeedAsync(db, future, futureWithout);

        await using var read = db.CreateContext();
        var rows = await new TodayQuery(read).GetCandidatesAsync(Today, Ct);

        var row = rows.Should().ContainSingle().Subject;
        row.Title.Should().Be("Preparar apresentação");
        row.Deadline.Should().Be(Friday);
        row.NextAction.Should().Be("Montar o roteiro");
        row.Estimate.Should().Be(TimeSpan.FromHours(3));
    }

    /// <summary>
    /// Quem atualiza encontra todas as tarefas sem prazo e sem nada avisado
    /// (ADR-014, "upgrade não arma nada"), e a lista sai exatamente como antes.
    /// </summary>
    [Fact]
    public async Task TheDeadlineUpgrade_KeepsEveryTaskWithoutADeadline()
    {
        await using var db = await new TempSqliteDatabase().MigrateToAsync("BranchSettings", Ct);
        var taskId = Guid.CreateVersion7();

        await using (var write = db.CreateContext())
        {
            var occurrenceId = Guid.CreateVersion7();
            var createdAt = Now.UtcTicks;

            await write.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO Tasks (Id, Title, Description, Priority, CreatedAt)
                 VALUES ({taskId}, {"Renovar o contrato"}, NULL, 0, {createdAt});
                 """,
                Ct);

            await write.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO TaskOccurrences
                     (Id, TaskItemId, ScheduledDate, ScheduledTime, Status, CompletedAt)
                 VALUES ({occurrenceId}, {taskId}, '2026-10-01', NULL, 0, NULL);
                 """,
                Ct);
        }

        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        var loaded = await new TaskItemRepository(read).FindByIdAsync(taskId, Ct);

        loaded!.Title.Should().Be("Renovar o contrato");
        loaded.Occurrences[0].Deadline.Should().BeNull();
        loaded.Occurrences[0].DeadlineAlert.LastStage.Should().Be(DeadlineAlertStage.None);
        loaded.DeadlineAlerts.Should().BeNull();
        loaded.NextAction.Should().BeNull();
        (await new DeadlineAlertQuery(read).GetCandidatesAsync(Ct)).Should().BeEmpty();
        (await Store(read).GetAsync(Ct)).Should().Be(DeadlineSettings.Factory);
    }
}
