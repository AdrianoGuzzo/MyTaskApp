using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Queries;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

public class TodayQueryTests
{
    private static readonly DateOnly Today = new(2026, 9, 17);
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 17, 17, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SeedAsync(TempSqliteDatabase db, params TaskItem[] tasks)
    {
        await using var context = db.CreateContext();
        context.Tasks.AddRange(tasks);
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<IReadOnlyList<string>> TitlesAsync(TempSqliteDatabase db)
    {
        await using var context = db.CreateContext();
        var rows = await new TodayQuery(context).GetCandidatesAsync(Today, Ct);
        return rows.Select(row => row.Title).ToList();
    }

    [Fact]
    public async Task ReturnsPendingWorkScheduledForToday()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await SeedAsync(db, TaskItem.Create("Daily", NowUtc, schedule: TaskSchedule.At(Today, new TimeOnly(9, 0))));

        (await TitlesAsync(db)).Should().Equal("Daily");
    }

    [Fact]
    public async Task ReturnsPendingWorkLeftFromPreviousDays()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await SeedAsync(db, TaskItem.Create("Revisar documentação", NowUtc,
            schedule: TaskSchedule.On(Today.AddDays(-3))));

        (await TitlesAsync(db)).Should().Equal("Revisar documentação");
    }

    [Fact]
    public async Task LeavesOutWorkScheduledForTheFuture()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await SeedAsync(db, TaskItem.Create("Reunião de amanhã", NowUtc,
            schedule: TaskSchedule.On(Today.AddDays(1))));

        (await TitlesAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task LeavesOutUndatedWorkBecauseThatIsTheInbox()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await SeedAsync(db, TaskItem.Create("Estudar OpenTelemetry", NowUtc));

        (await TitlesAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReturnsWorkCompletedToday()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Revisar PR", NowUtc, schedule: TaskSchedule.On(Today));
        task.Occurrences.Single().Complete(NowUtc);
        await SeedAsync(db, task);

        (await TitlesAsync(db)).Should().Equal("Revisar PR");
    }

    [Fact]
    public async Task LeavesOutWorkCompletedLongAgo()
    {
        // O quadro de hoje não é o histórico; trazer tudo não escala.
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Deploy antigo", NowUtc, schedule: TaskSchedule.On(Today.AddDays(-30)));
        task.Occurrences.Single().Complete(NowUtc.AddDays(-30));
        await SeedAsync(db, task);

        (await TitlesAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task LeavesOutCancelledWork()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Tarefa cancelada", NowUtc, schedule: TaskSchedule.On(Today));
        task.Occurrences.Single().Cancel();
        await SeedAsync(db, task);

        (await TitlesAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task CarriesTitleAndPriorityFromTheTaskDefinition()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await SeedAsync(db, TaskItem.Create("Corrigir bug", NowUtc, "detalhes",
            TaskPriority.Urgent, TaskSchedule.At(Today, new TimeOnly(15, 30))));

        await using var context = db.CreateContext();
        var row = (await new TodayQuery(context).GetCandidatesAsync(Today, Ct)).Single();

        row.Title.Should().Be("Corrigir bug");
        row.Priority.Should().Be(TaskPriority.Urgent);
        row.ScheduledTime.Should().Be(new TimeOnly(15, 30));
        row.Status.Should().Be(TaskItemStatus.Pending);
    }

    [Fact]
    public async Task CompletionStoredWithANonUtcOffset_IsStillComparedCorrectly()
    {
        // DateTimeOffset vira TEXT: sem normalizar para UTC, a comparação de
        // intervalo passa a depender do offset e o filtro erra silenciosamente.
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Daily", NowUtc, schedule: TaskSchedule.On(Today));
        task.Occurrences.Single().Complete(new DateTimeOffset(2026, 9, 18, 2, 0, 0, TimeSpan.FromHours(9)));
        await SeedAsync(db, task);

        (await TitlesAsync(db)).Should().Equal("Daily");
    }

    /// <summary>
    /// A bolinha de worktree (ADR-034) só acende com worktree pronto: o que
    /// falhou ou foi removido não tem pasta onde haja trabalho a perder.
    /// </summary>
    [Fact]
    public async Task BringsOnlyTheReadyWorktrees()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TaskItem.Create("Corrigir animais", NowUtc, schedule: TaskSchedule.On(Today));

        var ready = task.BeginDevelopment(null, @"C:\Projects\eco-core", "origin/main", "feature/x", @"C:\Projects\eco-core-feature-x", NowUtc);
        task.MarkDevelopmentReady(ready.Id, NowUtc);

        var removed = task.BeginDevelopment(null, @"C:\Projects\eco-api", "origin/main", "feature/x", @"C:\Projects\eco-api-feature-x", NowUtc);
        task.MarkDevelopmentReady(removed.Id, NowUtc);
        task.MarkDevelopmentRemoved(removed.Id, NowUtc);

        var failed = task.BeginDevelopment(null, @"C:\Projects\eco-web", "origin/main", "feature/x", @"C:\Projects\eco-web-feature-x", NowUtc);
        task.MarkDevelopmentFailed(failed.Id, "falhou", NowUtc);

        await SeedAsync(db, task, TaskItem.Create("Sem worktree", NowUtc, schedule: TaskSchedule.On(Today)));

        await using var context = db.CreateContext();
        var rows = await new TodayQuery(context).GetCandidatesAsync(Today, Ct);

        rows.Single(row => row.Title == "Sem worktree").Worktrees.Should().BeNull();
        rows.Single(row => row.Title == "Corrigir animais").Worktrees.Should().ContainSingle()
            .Which.Should().Be(new WorktreeRow(
                ready.Id, @"C:\Projects\eco-core", "feature/x", "origin/main", @"C:\Projects\eco-core-feature-x"));
    }
}
