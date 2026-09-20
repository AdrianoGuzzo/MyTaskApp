using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Planning;

public class TodayBoardAttentionTests
{
    private static readonly DateOnly Today = new(2026, 9, 17);

    /// <summary>17/09/2026 15:35 em São Paulo.</summary>
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 17, 18, 35, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly StubQuery _query = new();

    [Fact]
    public async Task AChecklistWaitingSinceThreeOClock_IsReportedAsWaitingThirtyFiveMinutes()
    {
        _query.Rows = [Row(waitingSince: NowUtc.AddMinutes(-35), attempt: 3)];

        var board = await Handler().HandleAsync(Ct);

        var task = board.Unscheduled.Single();

        task.WaitingForAttention.Should().Be(TimeSpan.FromMinutes(35));
        task.ReminderStep.Should().Be(3);
    }

    [Fact]
    public async Task AChecklistThatWasNeverRemindedOf_IsNotWaiting()
    {
        _query.Rows = [Row()];

        var board = await Handler().HandleAsync(Ct);

        board.Unscheduled.Single().WaitingForAttention.Should().BeNull();
    }

    [Fact]
    public async Task ACompletedChecklist_StopsWaitingEvenIfItWaitedBefore()
    {
        _query.Rows =
        [
            Row(
                waitingSince: NowUtc.AddMinutes(-90),
                status: TaskItemStatus.Completed,
                completedAt: NowUtc.AddMinutes(-5)),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Completed.Single().WaitingForAttention.Should().BeNull();
    }

    [Fact]
    public async Task TheTaskPolicy_ReachesTheBoardSoTheRowCanOfferToEditIt()
    {
        _query.Rows = [Row(policy: ReminderPolicy.Urgent)];

        var board = await Handler().HandleAsync(Ct);

        board.Unscheduled.Single().Reminder.Should().Be(ReminderPolicy.Urgent);
    }

    [Fact]
    public async Task AMutedChannel_LowersTheStepReportedToTheScreen()
    {
        // Som desligado não muda o degrau, mas trazer-para-frente desligado no
        // topo da escada também não pode inventar insistência que não existe.
        var quiet = new ReminderPolicy(
            IsEnabled: true,
            ReminderAnchor.AfterCreation,
            TimeSpan.FromHours(1),
            RepeatUntilAcknowledged: true,
            TimeSpan.FromMinutes(15),
            AlertChannels.Notification);

        _query.Rows = [Row(waitingSince: NowUtc.AddMinutes(-90), attempt: 9, policy: quiet)];

        var board = await Handler().HandleAsync(Ct);

        var task = board.Unscheduled.Single();

        task.ReminderStep.Should().Be(ReminderEscalation.TopStep);
        task.Reminder!.Channels.Should().Be(AlertChannels.Notification);
    }

    private static TodayOccurrenceRow Row(
        DateTimeOffset? waitingSince = null,
        int attempt = 0,
        ReminderPolicy? policy = null,
        TaskItemStatus status = TaskItemStatus.Pending,
        DateTimeOffset? completedAt = null) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "Verificar estoque",
            TaskPriority.Normal,
            Today,
            null,
            status,
            completedAt,
            waitingSince,
            attempt,
            policy ?? ReminderPolicy.Default);

    private GetTodayBoardHandler Handler()
    {
        var options = Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" });
        var timeProvider = new FakeTimeProvider(NowUtc);
        var clock = new UserClock(timeProvider, options, NullLogger<UserClock>.Instance);

        return new GetTodayBoardHandler(_query, clock, timeProvider, options);
    }

    private sealed class StubQuery : ITodayQuery
    {
        public IReadOnlyList<TodayOccurrenceRow> Rows { get; set; } = [];

        public Task<IReadOnlyList<TodayOccurrenceRow>> GetCandidatesAsync(
            DateOnly today,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows);
    }
}
