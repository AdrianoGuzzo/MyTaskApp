using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Planning;

/// <summary>A seção PRAZOS e o prazo descrito em cada linha (ADR-050).</summary>
public class TodayBoardDeadlineTests
{
    // Terça, 06/10/2026, 14:00 em São Paulo.
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 6, 17, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly StubTodayQuery _query = new();

    private GetTodayBoardHandler Handler()
    {
        var time = new FakeTimeProvider(NowUtc);

        return new GetTodayBoardHandler(
            _query,
            TestClock.Over(time),
            time,
            Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" }));
    }

    private static TodayOccurrenceRow Row(
        string title,
        DateOnly? date,
        TaskDeadline? deadline,
        TimeOnly? time = null,
        TaskItemStatus status = TaskItemStatus.Pending,
        DateTimeOffset? completedAt = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, date, time, status, completedAt,
            Deadline: deadline, NextAction: "Próximo passo", Estimate: TimeSpan.FromHours(6));

    [Fact]
    public async Task TasksWithADeadline_GoToTheirOwnSection_SortedByDeadline()
    {
        _query.Rows =
        [
            Row("Módulo de nutrição", Today.AddDays(-1), new TaskDeadline(Today.AddDays(3), new TimeOnly(18, 0))),
            Row("Integração Jira", Today, new TaskDeadline(Today, new TimeOnly(18, 0))),
            Row("Comprar pão", Today, deadline: null),
        ];

        var board = await Handler().HandleAsync(Ct);

        board.Deadlines.Select(task => task.Title).Should().Equal("Integração Jira", "Módulo de nutrição");
        board.Unscheduled.Select(task => task.Title).Should().Equal("Comprar pão");
        board.Overdue.Should().BeEmpty("capturada ontem, mas o prazo é só na sexta");
        board.RemainingCount.Should().Be(3);
    }

    [Fact]
    public async Task EachRow_CarriesItsDescribedDeadline()
    {
        _query.Rows = [Row("Integração Jira", Today, new TaskDeadline(Today, new TimeOnly(18, 0)))];

        var task = (await Handler().HandleAsync(Ct)).Deadlines.Single();

        task.Deadline!.Severity.Should().Be(DeadlineSeverity.Urgent);
        task.Deadline.Label.Should().Be("URGENTE · vence hoje às 18:00");
        task.Deadline.DateLabel.Should().Be("ter 06/10 18:00");
        task.Deadline.Countdown.Should().Be("4 horas restantes");
        task.NextAction.Should().Be("Próximo passo");
        task.Estimate.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task APassedDeadline_IsOverdue()
    {
        _query.Rows = [Row("Publicar release", Today, new TaskDeadline(Today, new TimeOnly(11, 0)))];

        var board = await Handler().HandleAsync(Ct);

        var task = board.Overdue.Should().ContainSingle().Subject;
        task.Deadline!.Label.Should().Be("ATRASADA · há 3 horas");
    }

    [Fact]
    public async Task ACompletedTask_SaysWhetherItMadeTheDeadline()
    {
        _query.Rows =
        [
            Row("Revisar contrato", Today, new TaskDeadline(Today, new TimeOnly(18, 0)),
                status: TaskItemStatus.Completed, completedAt: NowUtc.AddHours(-1)),
        ];

        var task = (await Handler().HandleAsync(Ct)).Completed.Single();

        task.Deadline!.Status.Should().Be(DeadlineStatus.Met);
        task.Deadline.Label.Should().Be("concluída no prazo");
    }

    [Fact]
    public async Task TheSummary_CountsOverdueTodayAndThisWeek()
    {
        _query.Rows =
        [
            Row("Atrasada", Today, new TaskDeadline(Today, new TimeOnly(9, 0))),
            Row("Hoje", Today, new TaskDeadline(Today, new TimeOnly(18, 0))),
            Row("Sexta", Today, new TaskDeadline(Today.AddDays(3), new TimeOnly(18, 0))),
            Row("Mês que vem", Today, new TaskDeadline(Today.AddDays(30), new TimeOnly(18, 0))),
        ];

        var summary = (await Handler().HandleAsync(Ct)).DeadlineSummary;

        summary.Should().Be(new DeadlineSummary(1, 1, 1));
        summary.Label.Should().Be("1 atrasada · 1 hoje · 1 na semana");
    }

    [Fact]
    public async Task WithoutDeadlines_TheSummaryIsEmpty()
    {
        _query.Rows = [Row("Comprar pão", Today, deadline: null)];

        var summary = (await Handler().HandleAsync(Ct)).DeadlineSummary;

        summary.IsEmpty.Should().BeTrue();
        summary.Label.Should().BeEmpty();
    }

    private sealed class StubTodayQuery : ITodayQuery
    {
        public IReadOnlyList<TodayOccurrenceRow> Rows { get; set; } = [];

        public Task<IReadOnlyList<TodayOccurrenceRow>> GetCandidatesAsync(
            DateOnly today,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows);
    }
}
