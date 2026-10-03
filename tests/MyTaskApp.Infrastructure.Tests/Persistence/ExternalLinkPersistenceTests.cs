using MyTaskApp.Domain.External;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// O retrato da issue contra SQLite de verdade (ADR-045): é ele que desenha a
/// tarefa sem rede, então ida e volta e "ausente continua ausente" importam.
/// </summary>
public class ExternalLinkPersistenceTests
{
    private static readonly DateOnly Today = new(2026, 10, 3);
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ExternalLink Link(string status = "Em andamento", string title = "Corrigir erro de sincronização") =>
        ExternalLink.Create(
            "Jira",
            "GAECO-1234",
            title,
            "https://empresa.atlassian.net/browse/GAECO-1234",
            "Bug",
            status,
            Now);

    private static TaskItem TodayTask(string title) =>
        TaskItem.Create(title, Now, schedule: TaskSchedule.On(Today));

    private static async Task SeedAsync(TempSqliteDatabase db, params object[] entities)
    {
        await using var context = db.CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task TheSnapshotSurvivesTheRoundTrip()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TodayTask("Corrigir erro de sincronização");
        task.LinkExternal(Link());
        await SeedAsync(db, task);

        await using var read = db.CreateContext();
        var loaded = await new TaskItemRepository(read).FindByIdAsync(task.Id, Ct);

        loaded!.External.Should().NotBeNull();
        loaded.External!.Provider.Should().Be("Jira");
        loaded.External.Id.Should().Be("GAECO-1234");
        loaded.External.Title.Should().Be("Corrigir erro de sincronização");
        loaded.External.Url.Should().Be("https://empresa.atlassian.net/browse/GAECO-1234");
        loaded.External.IssueType.Should().Be("Bug");
        loaded.External.Status.Should().Be("Em andamento");
        loaded.External.SyncedAt.Should().Be(Now);
    }

    [Fact]
    public async Task ALocalTask_StaysUnlinked()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TodayTask("Comprar pão");
        await SeedAsync(db, task);

        await using var read = db.CreateContext();
        var loaded = await new TaskItemRepository(read).FindByIdAsync(task.Id, Ct);

        loaded!.External.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAndUnlink_ArePersisted()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = TodayTask("Corrigir erro de sincronização");
        task.LinkExternal(Link(status: "A fazer"));
        await SeedAsync(db, task);

        await using (var write = db.CreateContext())
        {
            var loaded = await new TaskItemRepository(write).FindByIdAsync(task.Id, Ct);
            loaded!.RefreshExternal(Link(status: "Em revisão", title: "Corrigir sync no login"));
            await write.SaveChangesAsync(Ct);
        }

        await using (var read = db.CreateContext())
        {
            var loaded = await new TaskItemRepository(read).FindByIdAsync(task.Id, Ct);
            loaded!.External!.Status.Should().Be("Em revisão");
            loaded.Title.Should().Be("Corrigir sync no login");
        }

        await using (var write = db.CreateContext())
        {
            var loaded = await new TaskItemRepository(write).FindByIdAsync(task.Id, Ct);
            loaded!.UnlinkExternal();
            await write.SaveChangesAsync(Ct);
        }

        await using var final = db.CreateContext();
        (await new TaskItemRepository(final).FindByIdAsync(task.Id, Ct))!.External.Should().BeNull();
    }

    [Fact]
    public async Task TheTodayQuery_CarriesTheSnapshot_SoTheRowDrawsOffline()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var linked = TodayTask("Corrigir erro de sincronização");
        linked.LinkExternal(Link());
        var local = TodayTask("Comprar pão");
        await SeedAsync(db, linked, local);

        await using var read = db.CreateContext();
        var rows = await new TodayQuery(read).GetCandidatesAsync(Today, Ct);

        rows.Single(row => row.TaskId == linked.Id).External!.Id.Should().Be("GAECO-1234");
        rows.Single(row => row.TaskId == local.Id).External.Should().BeNull();
    }
}
