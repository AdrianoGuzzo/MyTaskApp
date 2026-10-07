using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Domain.Tests.TimeTracking;

/// <summary>Um período de trabalho: início e fim, nunca a duração guardada (ADR-052).</summary>
public class TimeEntryTests
{
    private static readonly DateTimeOffset At14 = new(2026, 10, 6, 14, 0, 0, TimeSpan.FromHours(-3));

    private static readonly Guid Occurrence = Guid.CreateVersion7();

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 10, 6, hour, minute, 0, TimeSpan.FromHours(-3));

    [Fact]
    public void StartTimer_IsOpen_FromTheTimer()
    {
        var entry = TimeEntry.StartTimer(Occurrence, At14);

        entry.TaskOccurrenceId.Should().Be(Occurrence);
        entry.StartedAt.Should().Be(At14);
        entry.EndedAt.Should().BeNull();
        entry.IsActive.Should().BeTrue();
        entry.Source.Should().Be(TimeEntrySource.Timer);
        entry.CreatedAt.Should().Be(At14);
        entry.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public void Stop_ClosesThePeriod_AndTheDurationIsEndMinusStart()
    {
        var entry = TimeEntry.StartTimer(Occurrence, At(14, 32));

        entry.Stop(At(15, 48)).Should().BeTrue();

        entry.EndedAt.Should().Be(At(15, 48));
        entry.IsActive.Should().BeFalse();
        entry.Duration(At(23)).Should().Be(TimeSpan.FromMinutes(76));
        entry.UpdatedAt.Should().Be(At(15, 48));
    }

    [Fact]
    public void StoppingTwice_ChangesNothing()
    {
        var entry = TimeEntry.StartTimer(Occurrence, At14);
        entry.Stop(At(15));

        entry.Stop(At(16)).Should().BeFalse();

        entry.EndedAt.Should().Be(At(15));
    }

    [Fact]
    public void Stop_AtOrBeforeTheStart_IsRefused()
    {
        var entry = TimeEntry.StartTimer(Occurrence, At14);

        var stop = () => entry.Stop(At14);

        stop.Should().Throw<DomainException>().WithMessage("*posterior*");
        entry.IsActive.Should().BeTrue();
    }

    [Fact]
    public void WhileRunning_TheDurationIsNowMinusStart()
    {
        var entry = TimeEntry.StartTimer(Occurrence, At14);

        entry.Duration(At(15, 37)).Should().Be(TimeSpan.FromMinutes(97));
        entry.Duration(At(13)).Should().Be(TimeSpan.Zero, "um relógio que voltou não produz tempo negativo");
    }

    [Fact]
    public void Manual_IsClosed_FromTheUser()
    {
        var entry = TimeEntry.Manual(Occurrence, At(14), At(15, 30), "  Corrigi problema na API  ", At(18));

        entry.Source.Should().Be(TimeEntrySource.Manual);
        entry.Duration(At(18)).Should().Be(TimeSpan.FromMinutes(90));
        entry.Note.Should().Be("Corrigi problema na API");
        entry.IsActive.Should().BeFalse();
    }

    [Theory]
    [InlineData(15, 30, 14, 0)]
    [InlineData(14, 0, 14, 0)]
    public void Manual_WhoseEndIsNotAfterTheStart_IsRefused(int startHour, int startMinute, int endHour, int endMinute)
    {
        var create = () => TimeEntry.Manual(Occurrence, At(startHour, startMinute), At(endHour, endMinute), null, At(18));

        create.Should().Throw<DomainException>()
            .WithMessage("O horário final deve ser posterior ao horário inicial.");
    }

    [Fact]
    public void Manual_EndingInTheFuture_IsRefused()
    {
        var create = () => TimeEntry.Manual(Occurrence, At(14), At(16), null, At(15));

        create.Should().Throw<DomainException>().WithMessage("*futuro*");
    }

    [Fact]
    public void ABlankNote_IsNoNote_AndALongOne_IsRefused()
    {
        TimeEntry.Manual(Occurrence, At(14), At(15), "   ", At(18)).Note.Should().BeNull();

        var tooLong = () => TimeEntry.Manual(
            Occurrence, At(14), At(15), new string('x', TimeEntry.MaxNoteLength + 1), At(18));

        tooLong.Should().Throw<DomainException>().WithMessage("*500*");
    }

    [Fact]
    public void Change_KeepsTheSource_AndStampsTheUpdate()
    {
        var entry = TimeEntry.StartTimer(Occurrence, At(14));
        entry.Stop(At(15, 30));

        entry.Change(At(14, 10), At(15, 45), "revisado", At(18));

        entry.StartedAt.Should().Be(At(14, 10));
        entry.EndedAt.Should().Be(At(15, 45));
        entry.Duration(At(18)).Should().Be(TimeSpan.FromMinutes(95));
        entry.Source.Should().Be(TimeEntrySource.Timer);
        entry.Note.Should().Be("revisado");
        entry.UpdatedAt.Should().Be(At(18));
    }

    [Fact]
    public void ARefusedChange_LeavesThePeriodUntouched()
    {
        var entry = TimeEntry.Manual(Occurrence, At(14), At(15, 30), "antes", At(18));

        var change = () => entry.Change(At(16), At(15), "depois", At(18));

        change.Should().Throw<DomainException>();
        entry.StartedAt.Should().Be(At(14));
        entry.EndedAt.Should().Be(At(15, 30));
        entry.Note.Should().Be("antes");
        entry.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public void ChangingTheRunningPeriod_IsRefused()
    {
        var entry = TimeEntry.StartTimer(Occurrence, At14);

        var change = () => entry.Change(At(13), At(14, 30), null, At(15));

        change.Should().Throw<DomainException>().WithMessage("*Pare o cronômetro*");
        entry.IsActive.Should().BeTrue("um período encerrado não pode ser tratado como ativo, e vice-versa");
    }

    [Fact]
    public void APeriodWithoutATask_IsRefused()
    {
        var start = () => TimeEntry.StartTimer(Guid.Empty, At14);

        start.Should().Throw<DomainException>();
    }
}
