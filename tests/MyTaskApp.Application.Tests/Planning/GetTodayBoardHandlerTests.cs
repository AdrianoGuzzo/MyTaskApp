using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Planning;

public class GetTodayBoardHandlerTests
{
    private static readonly DateOnly Today = new(2026, 9, 17);

    /// <summary>17/09/2026 14:00 em São Paulo.</summary>
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 17, 17, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTodayQuery _query = new();

    private GetTodayBoardHandler Handler()
    {
        var options = Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" });
        var timeProvider = new FakeTimeProvider(NowUtc);
        var clock = new UserClock(timeProvider, options, NullLogger<UserClock>.Instance);

        return new GetTodayBoardHandler(_query, clock, timeProvider, options);
    }

    private static TodayOccurrenceRow Row(
        string title,
        DateOnly? date,
        TimeOnly? time = null,
        TaskItemStatus status = TaskItemStatus.Pending,
        DateTimeOffset? completedAt = null,
        TaskPriority priority = TaskPriority.Normal) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, priority, date, time, status, completedAt);

    [Fact]
    public async Task Board_IsDatedWithTheUsersToday()
    {
        var board = await Handler().HandleAsync(Ct);

        board.Date.Should().Be(Today);
    }

    [Fact]
    public async Task EachOccurrence_LandsInItsOwnSection()
    {
        _query.Rows =
        [
            Row("Revisar documentação", Today.AddDays(-1)),
            Row("Verificar chamados", Today, new TimeOnly(14, 0)),
            Row("Deploy", Today, new TimeOnly(15, 30)),
            Row("Organizar documentação", Today),
            Row("Revisar PR", Today, new TimeOnly(10, 0), TaskItemStatus.Completed, NowUtc),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Overdue.Select(item => item.Title).Should().Equal("Revisar documentação");
        board.Now.Select(item => item.Title).Should().Equal("Verificar chamados");
        board.Today.Select(item => item.Title).Should().Equal("Deploy");
        board.Unscheduled.Select(item => item.Title).Should().Equal("Organizar documentação");
        board.Completed.Select(item => item.Title).Should().Equal("Revisar PR");
    }

    [Fact]
    public async Task CancelledOccurrences_AreLeftOutOfEverySection()
    {
        _query.Rows = [Row("Tarefa cancelada", Today, new TimeOnly(14, 0), TaskItemStatus.Cancelled)];

        var board = await Handler().HandleAsync(Ct);

        board.TotalVisible.Should().Be(0);
    }

    [Fact]
    public async Task FutureOccurrences_AreLeftOutOfTheBoard()
    {
        _query.Rows = [Row("Reunião de amanhã", Today.AddDays(1), new TimeOnly(9, 0))];

        var board = await Handler().HandleAsync(Ct);

        board.TotalVisible.Should().Be(0);
    }

    [Fact]
    public async Task CompletionIsJudgedInTheUsersTimeZone()
    {
        // Concluída 17/09 23:30 UTC = 20:30 em São Paulo, ainda hoje.
        _query.Rows =
        [
            Row("Daily", Today, new TimeOnly(9, 0), TaskItemStatus.Completed,
                new DateTimeOffset(2026, 9, 17, 23, 30, 0, TimeSpan.Zero)),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Completed.Should().ContainSingle();
    }

    [Fact]
    public async Task TodaysSection_IsOrderedByTime()
    {
        _query.Rows =
        [
            Row("Retrospectiva", Today, new TimeOnly(18, 0)),
            Row("Daily", Today, new TimeOnly(9, 0)),
            Row("Almoço", Today, new TimeOnly(12, 0)),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Today.Select(item => item.Title).Should().Equal("Daily", "Almoço", "Retrospectiva");
    }

    [Fact]
    public async Task OverdueSection_ShowsTheOldestFirst()
    {
        _query.Rows =
        [
            Row("Anteontem", Today.AddDays(-2)),
            Row("Semana passada", Today.AddDays(-7)),
            Row("Ontem", Today.AddDays(-1)),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Overdue.Select(item => item.Title)
            .Should().Equal("Semana passada", "Anteontem", "Ontem");
    }

    [Fact]
    public async Task UnscheduledSection_PutsUrgentWorkFirst()
    {
        _query.Rows =
        [
            Row("Tarefa comum", Today, priority: TaskPriority.Normal),
            Row("Incêndio", Today, priority: TaskPriority.Urgent),
            Row("Quando der", Today, priority: TaskPriority.Low),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Unscheduled.Select(item => item.Title)
            .Should().Equal("Incêndio", "Tarefa comum", "Quando der");
    }

    [Fact]
    public async Task CompletedSection_ShowsTheMostRecentFirst()
    {
        _query.Rows =
        [
            Row("Primeira", Today, status: TaskItemStatus.Completed, completedAt: NowUtc.AddHours(-3)),
            Row("Última", Today, status: TaskItemStatus.Completed, completedAt: NowUtc.AddMinutes(-5)),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Completed.Select(item => item.Title).Should().Equal("Última", "Primeira");
    }

    [Fact]
    public async Task TodaysPastTasks_AreFlaggedLateWithoutLeavingTheDay()
    {
        _query.Rows = [Row("Daily", Today, new TimeOnly(9, 0))];

        var board = await Handler().HandleAsync(Ct);

        board.Today.Single().IsLate.Should().BeTrue();
    }

    [Fact]
    public async Task EmptyDay_ProducesAnEmptyButUsableBoard()
    {
        var board = await Handler().HandleAsync(Ct);

        board.TotalVisible.Should().Be(0);
        board.Overdue.Should().BeEmpty();
        board.Completed.Should().BeEmpty();
    }

    [Fact]
    public async Task Query_IsAskedForTheUsersTodayNotTheUtcDay()
    {
        await Handler().HandleAsync(Ct);

        _query.RequestedDate.Should().Be(Today);
    }

    private sealed class FakeTodayQuery : ITodayQuery
    {
        public IReadOnlyList<TodayOccurrenceRow> Rows { get; set; } = [];

        public DateOnly? RequestedDate { get; private set; }

        public Task<IReadOnlyList<TodayOccurrenceRow>> GetCandidatesAsync(
            DateOnly today,
            CancellationToken cancellationToken = default)
        {
            RequestedDate = today;
            return Task.FromResult(Rows);
        }
    }
}
