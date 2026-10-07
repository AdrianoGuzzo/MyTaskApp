using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.TimeTracking;

/// <summary>O quadro leva o tempo de cada linha e o cronômetro do app (ADR-052).</summary>
public class TodayBoardTimeTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 6, 17, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GetTodayBoardHandler Handler(ITodayQuery query, IActiveTimerQuery? timers)
    {
        var time = new FakeTimeProvider(NowUtc);

        return new GetTodayBoardHandler(
            query,
            TestClock.Over(time),
            time,
            Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" }),
            timers: timers);
    }

    [Fact]
    public async Task EachRow_CarriesItsLoggedTime_AndTheTimerStart()
    {
        var startedAt = NowUtc.AddMinutes(-37);
        var query = new StubTodayQuery(
            new TodayOccurrenceRow(
                Guid.CreateVersion7(), Guid.CreateVersion7(), "Implementar autenticação", TaskPriority.Normal,
                Today, null, TaskItemStatus.Pending, null,
                Logged: TimeSpan.FromMinutes(272), TimerStartedAt: startedAt));

        var board = await Handler(query, timers: null).HandleAsync(Ct);

        var row = board.Unscheduled.Single();
        row.Logged.Should().Be(TimeSpan.FromMinutes(272));
        row.TimerStartedAt.Should().Be(startedAt);
        board.ActiveTimer.Should().BeNull("sem a consulta, o quadro não sabe do cronômetro");
    }

    [Fact]
    public async Task TheBoard_KnowsTheRunningTimer_EvenOfATaskThatIsNotOnIt()
    {
        var tasks = new FakeTaskItemRepository();
        var entries = new FakeTimeEntryRepository();
        var tomorrow = TaskItem.Create("Planejar sprint", NowUtc, schedule: new TaskSchedule(Today.AddDays(1), null));
        tasks.Seed(tomorrow);
        entries.Seed(Domain.TimeTracking.TimeEntry.StartTimer(tomorrow.Occurrences.Single().Id, NowUtc.AddMinutes(-5)));

        var board = await Handler(new StubTodayQuery(), new FakeActiveTimerQuery(entries, tasks)).HandleAsync(Ct);

        board.TotalVisible.Should().Be(0);
        board.ActiveTimer!.TaskTitle.Should().Be("Planejar sprint");
        board.ActiveTimer.StartedAt.Should().Be(NowUtc.AddMinutes(-5));
    }

    private sealed class StubTodayQuery(params TodayOccurrenceRow[] rows) : ITodayQuery
    {
        public Task<TodayOccurrenceRow?> FindOccurrenceAsync(
            Guid occurrenceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(rows.FirstOrDefault(row => row.OccurrenceId == occurrenceId));

        public Task<IReadOnlyList<TodayOccurrenceRow>> GetCandidatesAsync(
            DateOnly today,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TodayOccurrenceRow>>(rows);
    }
}
