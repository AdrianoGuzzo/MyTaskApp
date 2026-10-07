using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.History;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Planning;

namespace MyTaskApp.Application.Tests.History;

/// <summary>O histórico no fuso do usuário, já escrito para a tela (ADR-053).</summary>
public class GetActivityHistoryHandlerTests
{
    private static readonly TimeSpan SaoPaulo = TimeSpan.FromHours(-3);

    /// <summary>Terça, 06/10/2026, 18:00 em São Paulo.</summary>
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 6, 21, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeActivityHistoryQuery _query = new();

    private static readonly Guid Task = Guid.CreateVersion7();

    private static readonly Guid Occurrence = Guid.CreateVersion7();

    private GetActivityHistoryHandler Handler(DateTimeOffset? nowUtc = null, string timeZone = "America/Sao_Paulo")
    {
        var options = Options.Create(new ApplicationOptions { TimeZoneId = timeZone });
        var timeProvider = new FakeTimeProvider(nowUtc ?? NowUtc);
        var clock = new UserClock(timeProvider, options, NullLogger<UserClock>.Instance);

        return new GetActivityHistoryHandler(_query, clock, timeProvider);
    }

    private static DateTimeOffset Local(int day, int hour, int minute, int month = 10) =>
        new(2026, month, day, hour, minute, 0, SaoPaulo);

    [Fact]
    public async Task TheWindow_GoesFromMidnightSixDaysAgo_ToTomorrowsMidnight_InTheUsersZone()
    {
        await Handler().HandleAsync(new GetActivityHistory(), Ct);

        _query.Requested.Should().Be((Local(30, 0, 0, month: 9), Local(7, 0, 0)));
    }

    [Fact]
    public async Task SevenDays_WithTheirLabels()
    {
        var history = await Handler().HandleAsync(new GetActivityHistory(), Ct);

        history.Days.Select(day => day.Label).Should().Equal(
            "HOJE · TER 06/10",
            "ONTEM · SEG 05/10",
            "DOM · 04/10",
            "SÁB · 03/10",
            "SEX · 02/10",
            "QUI · 01/10",
            "QUA · 30/09");
    }

    [Fact]
    public async Task NothingDone_SaysSo_AndKeepsTheDays()
    {
        var history = await Handler().HandleAsync(new GetActivityHistory(), Ct);

        history.IsEmpty.Should().BeTrue();
        history.Days.Should().HaveCount(7).And.OnlyContain(day => day.IsEmpty);
        history.Summary.Should().Be("Nenhuma atividade registrada");
    }

    [Fact]
    public async Task ACompletedTask_ShowsTheLocalTime_AndTheTimeWorkedThatDay()
    {
        _query.Completions.Add(new ActivityCompletion(Task, Occurrence, "Implementar histórico", Local(6, 17, 42)));
        _query.Periods.Add(new ActivityPeriod(Task, Occurrence, "Implementar histórico", Local(6, 15, 27), Local(6, 17, 42)));

        var history = await Handler().HandleAsync(new GetActivityHistory(), Ct);

        var item = history.Days[0].Items.Should().ContainSingle().Subject;
        item.Should().Be(new ActivityItemView(Task, Occurrence, "Implementar histórico", true, "Concluída 17:42 · 2h 15min"));
        history.Summary.Should().Be("1 concluída · 2h 15min");
    }

    [Fact]
    public async Task ACompletedTask_WithoutTimeLogged_DoesNotClaimZeroMinutes()
    {
        _query.Completions.Add(new ActivityCompletion(Task, Occurrence, "Corrigir API", Local(6, 14, 31)));

        var history = await Handler().HandleAsync(new GetActivityHistory(), Ct);

        history.Days[0].Items.Should().ContainSingle().Which.Detail.Should().Be("Concluída 14:31");
    }

    [Fact]
    public async Task AWorkedTask_SaysHowLong()
    {
        _query.Periods.Add(new ActivityPeriod(Task, Occurrence, "Integração TGC", Local(5, 9, 0), Local(5, 11, 10)));

        var history = await Handler().HandleAsync(new GetActivityHistory(), Ct);

        var item = history.Days[1].Items.Should().ContainSingle().Subject;
        item.IsCompleted.Should().BeFalse();
        item.Detail.Should().Be("Trabalhado · 2h 10min");
        history.Summary.Should().Be("0 concluídas · 2h 10min");
    }

    [Fact]
    public async Task ACompletionAt2330_IsThatDay_NotTheNextOneInUtc()
    {
        _query.Completions.Add(new ActivityCompletion(Task, Occurrence, "Deploy", Local(5, 23, 30)));

        var history = await Handler().HandleAsync(new GetActivityHistory(), Ct);

        history.Days[1].Items.Should().ContainSingle().Which.Detail.Should().Be("Concluída 23:30");
        history.Days[0].IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task TheRunningTimer_CountsUpToNow()
    {
        _query.Periods.Add(new ActivityPeriod(Task, Occurrence, "Revisar PR", Local(6, 17, 15), EndedAt: null));

        var history = await Handler().HandleAsync(new GetActivityHistory(), Ct);

        history.Days[0].Items.Should().ContainSingle().Which.Detail.Should().Be("Trabalhado · 45min");
    }

    [Fact]
    public async Task TheDaylightSavingDay_UsesItsRealMidnights()
    {
        // 01/11/2026 em Nova York: o relógio volta às 2:00, e o dia tem 25 horas.
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(-5));
        var lateSunday = new DateTimeOffset(2026, 11, 1, 23, 30, 0, TimeSpan.FromHours(-5));
        _query.Completions.Add(new ActivityCompletion(Task, Occurrence, "Fechar o mês", lateSunday));

        var history = await Handler(now, "America/New_York").HandleAsync(new GetActivityHistory(), Ct);

        _query.Requested.Should().Be((
            new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(-4)),
            new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.FromHours(-5))));
        history.Days[0].Label.Should().Be("HOJE · DOM 01/11");
        history.Days[0].Items.Should().ContainSingle().Which.Detail.Should().Be("Concluída 23:30");
    }

    [Fact]
    public async Task ZeroDays_IsRefused()
    {
        var act = () => Handler().HandleAsync(new GetActivityHistory(Days: 0), Ct);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
