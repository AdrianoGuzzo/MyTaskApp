using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Domain.Tests.Deadlines;

public class DeadlineFormatterTests
{
    // Segunda-feira.
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static readonly TaskDeadline Friday = new(new DateOnly(2026, 10, 9), new TimeOnly(18, 0));

    [Theory]
    [InlineData(7 * 24 * 60, "7 dias")]
    [InlineData((8 * 24 + 5) * 60, "8 dias")]
    [InlineData((5 * 24 + 17) * 60, "5 dias e 17 horas")]
    [InlineData((3 * 24 + 4) * 60, "3 dias e 4 horas")]
    [InlineData(2 * 24 * 60, "2 dias")]
    [InlineData((24 + 1) * 60, "1 dia e 1 hora")]
    [InlineData(8 * 60, "8 horas")]
    [InlineData(8 * 60 + 59, "8 horas")]
    [InlineData(2 * 60, "2 horas")]
    [InlineData(102, "1h 42min")]
    [InlineData(60, "1 hora")]
    [InlineData(45, "45 minutos")]
    [InlineData(1, "1 minuto")]
    [InlineData(0, "menos de 1 minuto")]
    public void Remaining_UsesTheReadableUnit(int minutes, string expected)
    {
        DeadlineFormatter.Remaining(TimeSpan.FromMinutes(minutes)).Should().Be(expected);
    }

    [Fact]
    public void Remaining_NeverSaysHundredsOfHours()
    {
        DeadlineFormatter.Remaining(TimeSpan.FromHours(137)).Should().Be("5 dias e 17 horas");
    }

    [Theory]
    [InlineData(-25, "há 25 minutos")]
    [InlineData(-120, "há 2 horas")]
    [InlineData(-60 * 24 * 3 - 200, "há 3 dias")]
    [InlineData(0, "agora mesmo")]
    public void Ago_IsCoarse(int minutes, string expected)
    {
        DeadlineFormatter.Ago(TimeSpan.FromMinutes(minutes)).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, "hoje às 18:00")]
    [InlineData(1, "amanhã às 18:00")]
    [InlineData(4, "sexta às 18:00")]
    [InlineData(6, "domingo às 18:00")]
    [InlineData(7, "12/10 às 18:00")]
    [InlineData(-1, "04/10 às 18:00")]
    public void Moment_IsRelativeToToday(int days, string expected)
    {
        var deadline = new TaskDeadline(Today.AddDays(days), new TimeOnly(18, 0));

        DeadlineFormatter.Moment(deadline, Today).Should().Be(expected);
    }

    [Fact]
    public void Date_IsShortWithWeekday()
    {
        DeadlineFormatter.Date(Friday).Should().Be("sex 09/10 18:00");
    }

    [Fact]
    public void RowLabel_Normal_IsJustTheCountdown()
    {
        var snapshot = new DeadlineSnapshot(DeadlineStatus.OnTrack, DeadlineSeverity.Normal, TimeSpan.FromHours(5 * 24 + 3));

        DeadlineFormatter.RowLabel(snapshot, Friday, Today).Should().Be("5 dias e 3 horas restantes");
    }

    [Fact]
    public void RowLabel_Attention_Tomorrow_SaysWhen()
    {
        var tomorrow = new TaskDeadline(Today.AddDays(1), new TimeOnly(18, 0));
        var snapshot = new DeadlineSnapshot(DeadlineStatus.DueSoon, DeadlineSeverity.Attention, TimeSpan.FromHours(33));

        DeadlineFormatter.RowLabel(snapshot, tomorrow, Today).Should().Be("ATENÇÃO · vence amanhã às 18:00");
    }

    [Fact]
    public void RowLabel_Attention_TwoDaysOut_SaysHowLong()
    {
        var wednesday = new TaskDeadline(Today.AddDays(2), new TimeOnly(8, 0));
        var snapshot = new DeadlineSnapshot(DeadlineStatus.DueSoon, DeadlineSeverity.Attention, TimeSpan.FromHours(47));

        DeadlineFormatter.RowLabel(snapshot, wednesday, Today).Should().Be("ATENÇÃO · 1 dia e 23 horas restantes");
    }

    [Fact]
    public void RowLabel_Urgent_Today_SaysTheTime()
    {
        var today = new TaskDeadline(Today, new TimeOnly(18, 0));
        var snapshot = new DeadlineSnapshot(DeadlineStatus.DueToday, DeadlineSeverity.Urgent, TimeSpan.FromHours(6));

        DeadlineFormatter.RowLabel(snapshot, today, Today).Should().Be("URGENTE · vence hoje às 18:00");
    }

    [Fact]
    public void RowLabel_Urgent_UnderTwoHours_Counts()
    {
        var today = new TaskDeadline(Today, new TimeOnly(18, 0));
        var snapshot = new DeadlineSnapshot(DeadlineStatus.DueToday, DeadlineSeverity.Urgent, TimeSpan.FromMinutes(102));

        DeadlineFormatter.RowLabel(snapshot, today, Today).Should().Be("URGENTE · vence em 1h 42min");
    }

    [Fact]
    public void RowLabel_Overdue_SaysSinceWhen()
    {
        var snapshot = new DeadlineSnapshot(DeadlineStatus.Overdue, DeadlineSeverity.Overdue, TimeSpan.FromHours(-3));

        DeadlineFormatter.RowLabel(snapshot, Friday, Today).Should().Be("ATRASADA · há 3 horas");
    }

    [Fact]
    public void RowLabel_Completed_SaysHowItWent()
    {
        var met = new DeadlineSnapshot(DeadlineStatus.Met, DeadlineSeverity.Normal, TimeSpan.FromHours(2));
        var missed = new DeadlineSnapshot(DeadlineStatus.Missed, DeadlineSeverity.Normal, TimeSpan.FromHours(-3));

        DeadlineFormatter.RowLabel(met, Friday, Today).Should().Be("concluída no prazo");
        DeadlineFormatter.RowLabel(missed, Friday, Today).Should().Be("concluída 3 horas após o prazo");
    }

    [Fact]
    public void Countdown_HasNoSeverityPrefix()
    {
        var soon = new DeadlineSnapshot(DeadlineStatus.DueSoon, DeadlineSeverity.Attention, TimeSpan.FromHours(54));
        var late = new DeadlineSnapshot(DeadlineStatus.Overdue, DeadlineSeverity.Overdue, TimeSpan.FromHours(-3));
        var met = new DeadlineSnapshot(DeadlineStatus.Met, DeadlineSeverity.Normal, TimeSpan.FromHours(3));
        var missed = new DeadlineSnapshot(DeadlineStatus.Missed, DeadlineSeverity.Normal, TimeSpan.FromHours(-3));

        DeadlineFormatter.Countdown(soon).Should().Be("2 dias e 6 horas restantes");
        DeadlineFormatter.Countdown(late).Should().Be("atrasada há 3 horas");
        DeadlineFormatter.Countdown(met).Should().Be("concluída no prazo");
        DeadlineFormatter.Countdown(missed).Should().Be("concluída 3 horas após o prazo");
    }

    [Theory]
    [InlineData(4, 18 * 60, "Prazo se aproximando", "Vence sexta às 18:00.")]
    [InlineData(1, 33 * 60, "Prazo amanhã", "Vence amanhã às 18:00.")]
    [InlineData(0, 8 * 60, "Prazo hoje", "Vence hoje às 18:00.")]
    [InlineData(0, 102, "Prazo urgente", "Vence em 1h 42min.")]
    public void Alert_IsContextual(int days, int minutesLeft, string heading, string message)
    {
        var deadline = new TaskDeadline(Today.AddDays(days), new TimeOnly(18, 0));
        var snapshot = new DeadlineSnapshot(DeadlineStatus.DueSoon, DeadlineSeverity.Attention, TimeSpan.FromMinutes(minutesLeft));

        DeadlineFormatter.AlertHeading(snapshot, deadline, Today).Should().Be(heading);
        DeadlineFormatter.AlertMessage(snapshot, deadline, Today).Should().Be(message);
    }

    [Fact]
    public void Alert_Overdue_SaysSinceWhen()
    {
        var snapshot = new DeadlineSnapshot(DeadlineStatus.Overdue, DeadlineSeverity.Overdue, TimeSpan.FromHours(-2));

        DeadlineFormatter.AlertHeading(snapshot, Friday, Today).Should().Be("Tarefa atrasada");
        DeadlineFormatter.AlertMessage(snapshot, Friday, Today).Should().Be("Está atrasada há 2 horas.");
    }
}
