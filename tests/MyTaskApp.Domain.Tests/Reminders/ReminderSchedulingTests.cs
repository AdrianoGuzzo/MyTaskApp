using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Domain.Tests.Reminders;

public class ReminderSchedulingTests
{
    // O exemplo do próprio pedido: criado às 14:00, avisa às 15:00.
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 9, 17, 14, 0, 0, TimeSpan.FromHours(-3));

    private static readonly DateTimeOffset ScheduledAt =
        new(2026, 9, 18, 9, 0, 0, TimeSpan.FromHours(-3));

    [Fact]
    public void FirstFireAt_AfterCreation_AddsTheOffset()
    {
        var fire = ReminderScheduling.FirstFireAt(ReminderPolicy.Default, CreatedAt, null);

        fire.Should().Be(CreatedAt.AddHours(1));
    }

    [Fact]
    public void FirstFireAt_AfterCreation_IgnoresTheScheduledTime()
    {
        // Decisão registrada: o padrão conta a partir da criação, e não do
        // horário agendado — quem quiser o outro muda por tarefa.
        var fire = ReminderScheduling.FirstFireAt(
            ReminderPolicy.Default, CreatedAt, ScheduledAt);

        fire.Should().Be(CreatedAt.AddHours(1));
    }

    [Fact]
    public void FirstFireAt_BeforeScheduledTime_SubtractsTheLeadTime()
    {
        var fire = ReminderScheduling.FirstFireAt(
            Before(TimeSpan.FromMinutes(10)), CreatedAt, ScheduledAt);

        fire.Should().Be(ScheduledAt.AddMinutes(-10));
    }

    [Fact]
    public void FirstFireAt_BeforeScheduledTime_WithNoLead_IsTheScheduledInstant()
    {
        // "Todos os dias às 09:00, lembrete imediatamente às 09:00."
        var fire = ReminderScheduling.FirstFireAt(
            Before(TimeSpan.Zero), CreatedAt, ScheduledAt);

        fire.Should().Be(ScheduledAt);
    }

    [Fact]
    public void FirstFireAt_BeforeScheduledTime_WithoutATime_ReturnsNothing()
    {
        // Ocorrência sem horário não tem "antes de quê".
        var fire = ReminderScheduling.FirstFireAt(
            Before(TimeSpan.FromMinutes(10)), CreatedAt, scheduledInstantUtc: null);

        fire.Should().BeNull();
    }

    [Fact]
    public void FirstFireAt_WithRemindersOff_ReturnsNothing()
    {
        ReminderScheduling.FirstFireAt(ReminderPolicy.None, CreatedAt, ScheduledAt)
            .Should().BeNull();
    }

    [Fact]
    public void NextFireAfter_AddsOneIntervalWhenTheAppWasAwake()
    {
        var fired = CreatedAt.AddHours(1);

        var next = ReminderScheduling.NextFireAfter(ReminderPolicy.Default, fired, fired);

        next.Should().Be(fired.AddMinutes(15));
    }

    [Fact]
    public void NextFireAfter_CoalescesThreeDaysOfMissedIntervalsIntoASingleNextOne()
    {
        // A regra do ADR-004: três dias fechado não são 288 avisos acumulados.
        var fired = CreatedAt.AddHours(1);
        var now = fired.AddDays(3);

        var next = ReminderScheduling.NextFireAfter(ReminderPolicy.Default, fired, now);

        next.Should().Be(now.AddMinutes(15));
    }

    [Fact]
    public void NextFireAfter_StaysOnTheOriginalGrid()
    {
        // Disparo previsto para 15:00, tique atrasado chegou 15:07: o próximo
        // continua sendo 15:15, não 15:22.
        var fired = CreatedAt.AddHours(1);
        var now = fired.AddMinutes(7);

        var next = ReminderScheduling.NextFireAfter(ReminderPolicy.Default, fired, now);

        next.Should().Be(fired.AddMinutes(15));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(4321)]
    public void NextFireAfter_AlwaysLandsStrictlyAfterNow(int minutesLate)
    {
        var fired = CreatedAt.AddHours(1);
        var now = fired.AddMinutes(minutesLate);

        var next = ReminderScheduling.NextFireAfter(ReminderPolicy.Default, fired, now);

        next.Should().NotBeNull();
        next!.Value.Should().BeAfter(now);
        (next.Value - now).Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void NextFireAfter_WithoutRepetition_ReturnsNothing()
    {
        var once = new ReminderPolicy(
            IsEnabled: true,
            ReminderAnchor.AfterCreation,
            TimeSpan.FromHours(1),
            RepeatUntilAcknowledged: false,
            TimeSpan.Zero,
            AlertChannels.Notification);

        ReminderScheduling.NextFireAfter(once, CreatedAt, CreatedAt).Should().BeNull();
    }

    [Fact]
    public void NextFireAfter_WithRemindersOff_ReturnsNothing()
    {
        ReminderScheduling.NextFireAfter(ReminderPolicy.None, CreatedAt, CreatedAt)
            .Should().BeNull();
    }

    [Fact]
    public void NextFireAfter_DoesNotOverflowOnAnAbsurdlyOldFire()
    {
        // Instante vindo de um banco corrompido não pode estourar a data.
        var next = ReminderScheduling.NextFireAfter(
            ReminderPolicy.Default, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

        next.Should().BeNull();
    }

    private static ReminderPolicy Before(TimeSpan lead) =>
        new(
            IsEnabled: true,
            ReminderAnchor.BeforeScheduledTime,
            lead,
            RepeatUntilAcknowledged: true,
            TimeSpan.FromMinutes(15),
            AlertChannels.All);
}
