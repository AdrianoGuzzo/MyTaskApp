using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Planning;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.Tests.Devices;

/// <summary>
/// Mandar para outro computador e receber (ADR-062): o que conta como
/// "chegou", onde a tarefa que chegou aparece, e o que impede de mandar.
/// </summary>
public sealed class ArrivalAndMoveTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 17, 0, 0, TimeSpan.Zero);

    private static readonly Guid Work = Guid.CreateVersion7();

    private static readonly Guid Home = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SentHereByAnotherComputer_IsAnArrival()
    {
        var (tasks, task) = Seed(DevicePlacement.On(Home));

        await Reconcile(tasks, new PlacementMove(task.Id, DevicePlacement.On(Work), DevicePlacement.On(Home), Work, IsNew: false));

        task.ReceivedAt.Should().Be(Now);
        task.ReceivedFromDeviceId.Should().Be(Work);
    }

    [Fact]
    public async Task CreatedOnAnotherComputer_AlreadySentHere_IsAnArrival()
    {
        var (tasks, task) = Seed(DevicePlacement.On(Home));

        await Reconcile(tasks, new PlacementMove(task.Id, null, DevicePlacement.On(Home), Work, IsNew: true));

        task.ReceivedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SharedWhileHiddenHere_IsAnArrival_ButANewSharedTaskIsNot()
    {
        var (tasks, shared) = Seed(DevicePlacement.Everywhere);
        var (_, born) = Seed(DevicePlacement.Everywhere, tasks);

        await Reconcile(
            tasks,
            new PlacementMove(shared.Id, DevicePlacement.On(Work), DevicePlacement.Everywhere, Work, IsNew: false),
            new PlacementMove(born.Id, null, DevicePlacement.Everywhere, Work, IsNew: true));

        shared.ReceivedAt.Should().NotBeNull();
        born.ReceivedAt.Should().BeNull("ninguém a mandou para cá em particular");
    }

    [Fact]
    public async Task MovingAway_OrMovesMadeHere_AreNotArrivals()
    {
        var (tasks, away) = Seed(DevicePlacement.On(Work));
        var (_, mine) = Seed(DevicePlacement.On(Home), tasks);

        await Reconcile(
            tasks,
            new PlacementMove(away.Id, DevicePlacement.On(Home), DevicePlacement.On(Work), Work, IsNew: false),
            new PlacementMove(mine.Id, DevicePlacement.On(Work), DevicePlacement.On(Home), Home, IsNew: false));

        away.ReceivedAt.Should().BeNull();
        mine.ReceivedAt.Should().BeNull("quem mandou foi este computador");
    }

    [Fact]
    public async Task TheBoard_PutsArrivalsOnTop_OnlyThere_EvenWithoutADate()
    {
        var arrived = new TodayOccurrenceRow(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "Comprar ração", TaskPriority.Normal, null, null, TaskItemStatus.Pending, null)
        {
            ReceivedAt = Now,
            ReceivedFromDeviceId = Work,
        };
        var scheduled = new TodayOccurrenceRow(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "Pagar boleto", TaskPriority.Normal, new DateOnly(2026, 10, 13), null, TaskItemStatus.Pending, null)
        {
            ReceivedAt = Now,
        };

        var board = await Board(arrived, scheduled).HandleAsync(Ct);

        board.Arrived.Select(task => task.Title).Should().BeEquivalentTo("Comprar ração", "Pagar boleto");
        board.Unscheduled.Should().BeEmpty("a que chegou não aparece duas vezes");
        board.Today.Should().BeEmpty();
        board.RemainingCount.Should().Be(2);
    }

    [Fact]
    public async Task SendingAway_IsRefusedWhileItsTimerRuns()
    {
        var tasks = new FakeTaskItemRepository();
        var task = TaskItem.Create("Relatório", Now, placement: DevicePlacement.On(Home));
        tasks.Seed(task);
        var entries = new FakeTimeEntryRepository();
        await entries.AddAsync(TimeEntry.StartTimer(task.Occurrences.Single().Id, Now), Ct);
        var handler = new PlaceTaskHandler(
            tasks,
            tasks,
            Placements.Connected(Home, (Work, "Trabalho")),
            NullLogger<PlaceTaskHandler>.Instance,
            new FakeActiveTimerQuery(entries, tasks));

        var send = () => handler.HandleAsync(new PlaceTask(task.Id, DevicePlacement.On(Work)), Ct);

        await send.Should().ThrowAsync<DomainException>().WithMessage("*cronômetro*");
        task.Placement.Should().Be(DevicePlacement.On(Home));

        var share = await handler.HandleAsync(new PlaceTask(task.Id, DevicePlacement.Everywhere), Ct);
        share.LeftThisComputer.Should().BeFalse("compartilhar não tira daqui");
    }

    [Fact]
    public async Task Sending_SaysWhere_AndRemembersWhereItWas()
    {
        var tasks = new FakeTaskItemRepository();
        var task = TaskItem.Create("Relatório", Now, placement: DevicePlacement.On(Home));
        tasks.Seed(task);
        var handler = new PlaceTaskHandler(tasks, tasks, Placements.Connected(Home, (Work, "Trabalho")), NullLogger<PlaceTaskHandler>.Instance);

        var result = await handler.HandleAsync(new PlaceTask(task.Id, DevicePlacement.On(Work)), Ct);

        result.Should().Be(new PlaceResult(true, "Trabalho", DevicePlacement.On(Home)));
        task.Placement.Should().Be(DevicePlacement.On(Work));
        tasks.SaveCount.Should().Be(1);
    }

    private static (FakeTaskItemRepository Tasks, TaskItem Task) Seed(DevicePlacement placement, FakeTaskItemRepository? tasks = null)
    {
        tasks ??= new FakeTaskItemRepository();
        var task = TaskItem.Create("Comprar ração", Now.AddDays(-1), placement: placement);
        tasks.Seed(task);
        return (tasks, task);
    }

    private static Task Reconcile(FakeTaskItemRepository tasks, params PlacementMove[] moves)
    {
        var clock = new FakeTimeProvider(Now);
        var handler = new ReconcileAfterRemoteChangeHandler(
            tasks,
            tasks,
            TestClock.Over(clock),
            clock,
            Placements.Connected(Home, (Work, "Trabalho")));

        return handler.HandleAsync(
            new ReconcileAfterRemoteChange([.. moves.Select(move => move.TaskId)], [], moves),
            Ct);
    }

    private static GetTodayBoardHandler Board(params TodayOccurrenceRow[] rows)
    {
        var options = Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" });
        var timeProvider = new FakeTimeProvider(Now);
        var clock = new UserClock(timeProvider, options, NullLogger<UserClock>.Instance);

        return new GetTodayBoardHandler(new RowsQuery(rows), clock, timeProvider, options);
    }

    private sealed class RowsQuery(IReadOnlyList<TodayOccurrenceRow> rows) : ITodayQuery
    {
        public Task<IReadOnlyList<TodayOccurrenceRow>> GetCandidatesAsync(DateOnly today, PlacementFilter placement, CancellationToken cancellationToken = default) =>
            Task.FromResult(rows);

        public Task<TodayOccurrenceRow?> FindOccurrenceAsync(Guid occurrenceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<TodayOccurrenceRow?>(null);
    }
}
