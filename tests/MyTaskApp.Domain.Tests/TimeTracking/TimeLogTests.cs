using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Domain.Tests.TimeTracking;

/// <summary>Sobreposição e total dos períodos de uma tarefa (ADR-052).</summary>
public class TimeLogTests
{
    private static readonly Guid Occurrence = Guid.CreateVersion7();

    private static readonly DateTimeOffset Evening = At(20, 0);

    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 10, 6, hour, minute, 0, TimeSpan.FromHours(-3));

    private static TimeEntry Closed(int fromHour, int fromMinute, int toHour, int toMinute) =>
        TimeEntry.Manual(Occurrence, At(fromHour, fromMinute), At(toHour, toMinute), null, Evening);

    [Theory]
    // 14:00 → 15:30 registrado; o candidato:
    [InlineData(14, 45, 16, 0)] // começa dentro, termina depois
    [InlineData(13, 0, 14, 30)] // começa antes, termina dentro
    [InlineData(13, 0, 16, 0)] // contém o registrado
    [InlineData(14, 30, 15, 0)] // contido no registrado
    [InlineData(14, 0, 15, 30)] // idêntico
    [InlineData(14, 0, 14, 1)] // mesmo início
    [InlineData(15, 29, 16, 0)] // um minuto de folga dentro
    public void AnOverlappingPeriod_IsFound(int fromHour, int fromMinute, int toHour, int toMinute)
    {
        var existing = Closed(14, 0, 15, 30);

        TimeLog.FindOverlap(At(fromHour, fromMinute), At(toHour, toMinute), [existing], Evening)
            .Should().BeSameAs(existing);
    }

    [Theory]
    [InlineData(15, 30, 16, 0)] // encosta no fim
    [InlineData(13, 0, 14, 0)] // encosta no início
    [InlineData(16, 0, 17, 0)] // depois
    [InlineData(9, 0, 10, 0)] // antes
    public void APeriodThatOnlyTouches_IsAllowed(int fromHour, int fromMinute, int toHour, int toMinute)
    {
        TimeLog.FindOverlap(At(fromHour, fromMinute), At(toHour, toMinute), [Closed(14, 0, 15, 30)], Evening)
            .Should().BeNull();
    }

    [Fact]
    public void TheRunningPeriod_LastsUntilNow()
    {
        var running = TimeEntry.StartTimer(Occurrence, At(14, 0));

        TimeLog.FindOverlap(At(13, 0), At(14, 30), [running], now: At(15, 0)).Should().BeSameAs(running);
        TimeLog.FindOverlap(At(13, 0), At(14, 0), [running], now: At(15, 0)).Should().BeNull();
    }

    [Fact]
    public void ARunningCandidate_ClaimsEverythingAfterItsStart()
    {
        var later = Closed(16, 0, 17, 0);

        TimeLog.FindOverlap(At(15, 0), end: null, [later], Evening).Should().BeSameAs(later);
        TimeLog.FindOverlap(At(17, 0), end: null, [later], Evening).Should().BeNull();
    }

    [Fact]
    public void WhenEditing_ThePeriodDoesNotOverlapItself()
    {
        var existing = Closed(14, 0, 15, 30);

        TimeLog.FindOverlap(At(14, 10), At(15, 45), [existing], Evening, ignoring: existing.Id).Should().BeNull();
    }

    [Fact]
    public void EnsureNoOverlap_SaysWhichPeriodWasHit()
    {
        var existing = Closed(14, 0, 15, 30);

        var ensure = () => TimeLog.EnsureNoOverlap(
            At(14, 45), At(16, 0), [existing], Evening, _ => "14:00 → 15:30");

        ensure.Should().Throw<DomainException>()
            .WithMessage("Este período se sobrepõe a outro já registrado nesta tarefa (14:00 → 15:30).");
    }

    [Fact]
    public void Logged_SumsTheClosedPeriods_AndLeavesTheRunningOneOut()
    {
        var entries = new[]
        {
            Closed(14, 0, 15, 0),
            Closed(16, 0, 17, 0),
            TimeEntry.StartTimer(Occurrence, At(18, 0)),
        };

        TimeLog.Logged(entries).Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void Logged_OfNothing_IsZero() => TimeLog.Logged([]).Should().Be(TimeSpan.Zero);
}
