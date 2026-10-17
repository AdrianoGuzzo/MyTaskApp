using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;
using MyTaskApp.Infrastructure.Persistence.Queries;
using MyTaskApp.Infrastructure.Tests.Sync;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// O "onde aparece" nas consultas (ADR-062): sem lugar e compartilhada
/// aparecem em todos; a de um computador, só nele; "Todos" não filtra.
/// </summary>
public sealed class PlacementQueryTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 10, 13);

    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Work = Guid.CreateVersion7();

    private static readonly Guid Home = Guid.CreateVersion7();

    private TempSqliteDatabase _db = null!;

    private TaskItem _legacy = null!;

    private TaskItem _shared = null!;

    private TaskItem _work = null!;

    private TaskItem _home = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlacementFilter AtWork => new([Work]);

    private static PlacementFilter AtHome => new([Home]);

    public async ValueTask InitializeAsync()
    {
        _db = await new TempSqliteDatabase().MigrateAsync(Ct);
        _legacy = Create("Legado", null);
        _shared = Create("Compartilhada", DevicePlacement.Everywhere);
        _work = Create("Do trabalho", DevicePlacement.On(Work));
        _home = Create("De casa", DevicePlacement.On(Home));

        await using var write = _db.CreateContext();
        write.AddRange(_legacy, _shared, _work, _home);
        await write.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task TheBoard_ShowsThisComputers_TheSharedAndTheLegacy()
    {
        await using var read = _db.CreateContext();
        var query = new TodayQuery(read);

        Titles(await query.GetCandidatesAsync(Today, AtWork, Ct)).Should().BeEquivalentTo("Legado", "Compartilhada", "Do trabalho");
        Titles(await query.GetCandidatesAsync(Today, AtHome, Ct)).Should().BeEquivalentTo("Legado", "Compartilhada", "De casa");
        Titles(await query.GetCandidatesAsync(Today, PlacementFilter.All, Ct)).Should().HaveCount(4);
    }

    [Fact]
    public async Task TheBoardRow_CarriesThePlacement()
    {
        await using var read = _db.CreateContext();

        var rows = await new TodayQuery(read).GetCandidatesAsync(Today, PlacementFilter.All, Ct);

        rows.Single(row => row.TaskId == _home.Id).Placement.Should().Be(DevicePlacement.On(Home));
        rows.Single(row => row.TaskId == _shared.Id).Placement.Should().Be(DevicePlacement.Everywhere);
        rows.Single(row => row.TaskId == _legacy.Id).Placement.Should().BeNull();
    }

    [Fact]
    public async Task AnOldIdOfThisComputer_StillCounts()
    {
        await using var read = _db.CreateContext();

        var rows = await new TodayQuery(read).GetCandidatesAsync(Today, new PlacementFilter([Work, Home]), Ct);

        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task Reminders_RingOnlyHere_AndHiddenOnesDoNotTakeTheBatch()
    {
        await using var read = _db.CreateContext();
        var query = new DueReminderQuery(read, Connected(Work));

        // A de casa espera há mais tempo: sem o filtro antes do lote, ocuparia o único lugar.
        var due = await query.GetDueAsync(Now, 1, Ct);

        due.Should().ContainSingle().Which.Title.Should().NotBe("De casa");
        (await query.GetDueAsync(Now, 10, Ct)).Select(row => row.Title).Should().NotContain("De casa");
    }

    [Fact]
    public async Task Reminders_BeforeTheFirstConnection_RingForEverything()
    {
        await using var read = _db.CreateContext();

        var due = await new DueReminderQuery(read).GetDueAsync(Now, 10, Ct);

        due.Should().HaveCount(4);
    }

    [Fact]
    public async Task Search_Statistics_Archive_AndHours_FollowTheFilter()
    {
        await using (var write = _db.CreateContext())
        {
            write.Add(TimeEntry.Manual(_home.Occurrences.Single().Id, Now.AddHours(-2), Now.AddHours(-1), null, Now));
            write.Add(TimeEntry.Manual(_work.Occurrences.Single().Id, Now.AddHours(-3), Now.AddHours(-2), null, Now));
            await write.SaveChangesAsync(Ct);
        }

        await using var read = _db.CreateContext();

        var search = await new TaskSearchQuery(read).SearchAsync(
            new Application.Tasks.TaskSearchCriteria([TaskLifecycle.Active], Placement: AtWork),
            Ct);
        search.Select(row => row.Row.Title).Should().BeEquivalentTo("Legado", "Compartilhada", "Do trabalho");

        (await new TaskSearchQuery(read).ListForCountingAsync(AtWork, Ct)).Should().HaveCount(3);

        var hours = await new TimeEntryReportQuery(read).ListAsync(new TimeEntryReportCriteria(Placement: AtWork), Ct);
        hours.Should().ContainSingle().Which.TaskTitle.Should().Be("Do trabalho");

        (await new TimeEntryReportQuery(read).ListAsync(new TimeEntryReportCriteria(), Ct)).Should().HaveCount(2);
    }

    /// <summary>
    /// A origem do celular (ADR-067) é exata: "Do trabalho" não traz as
    /// compartilhadas, e "Compartilhadas" traz o legado, que aparece em todos.
    /// </summary>
    [Fact]
    public async Task TheExactOrigin_KeepsOnlyThatDeviceOrOnlyTheShared()
    {
        await using var read = _db.CreateContext();
        var query = new TaskSearchQuery(read);

        async Task<IEnumerable<string>> SearchAsync(PlacementMatch origin) =>
            (await query.SearchAsync(new Application.Tasks.TaskSearchCriteria([TaskLifecycle.Active], Origin: origin), Ct))
                .Select(row => row.Row.Title);

        (await SearchAsync(PlacementMatch.On([Work]))).Should().BeEquivalentTo("Do trabalho");
        (await SearchAsync(PlacementMatch.On([Work, Home]))).Should().BeEquivalentTo("Do trabalho", "De casa");
        (await SearchAsync(PlacementMatch.Shared)).Should().BeEquivalentTo("Legado", "Compartilhada");
    }

    [Fact]
    public async Task TheExactOrigin_StacksOnTheDeviceView()
    {
        await using var read = _db.CreateContext();

        var rows = await new TaskSearchQuery(read).SearchAsync(
            new Application.Tasks.TaskSearchCriteria([TaskLifecycle.Active], Placement: AtWork, Origin: PlacementMatch.On([Home])),
            Ct);

        rows.Should().BeEmpty("a de casa não aparece no trabalho");
    }

    [Fact]
    public void AnOriginWithoutDevices_IsAMistake()
    {
        var empty = () => PlacementMatch.On([]);

        empty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Tags_ShowThisComputers_PlusTheOnesTheOpenTaskAlreadyHas()
    {
        var mine = Tag.Create("Daqui", "#000000", Now, DevicePlacement.On(Work));
        var theirs = Tag.Create("De lá", "#000000", Now, DevicePlacement.On(Home));
        var everywhere = Tag.Create("Todos", "#000000", Now, DevicePlacement.Everywhere);

        await using (var write = _db.CreateContext())
        {
            write.AddRange(mine, theirs, everywhere);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = _db.CreateContext();
        var query = new TagQuery(read);

        (await query.ListAsync(AtWork, [], Ct)).Select(tag => tag.Name).Should().BeEquivalentTo("Daqui", "Todos");
        (await query.ListAsync(AtWork, [theirs.Id], Ct)).Select(tag => tag.Name).Should().BeEquivalentTo("Daqui", "De lá", "Todos");
        (await query.ListAsync(PlacementFilter.All, [], Ct)).Single(tag => tag.Name == "De lá").Placement
            .Should().Be(DevicePlacement.On(Home));
    }

    private static TaskItem Create(string title, DevicePlacement? placement)
    {
        var task = TaskItem.Create(
            title,
            Now.AddHours(-1),
            schedule: TaskSchedule.On(Today),
            reminder: ReminderPolicy.Default,
            placement: placement);

        // A de casa é a que espera há mais tempo.
        task.Occurrences.Single().ArmReminder(Now.AddMinutes(title == "De casa" ? -60 : -5));

        return task;
    }

    private static DevicePlacementState Connected(Guid here)
    {
        var state = new DevicePlacementState(new FakeDeviceIdentity(here), new SyncDeviceDirectory());
        state.Update(here, []);
        return state;
    }

    private static IEnumerable<string> Titles(IEnumerable<Application.Planning.TodayOccurrenceRow> rows) =>
        rows.Select(row => row.Title);
}
