using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Domain.Tests.Reminders;

public class ReminderPolicyTests
{
    [Fact]
    public void Default_IsAnHourAfterCreationRepeatingEveryFifteenMinutes()
    {
        var policy = ReminderPolicy.Default;

        policy.IsEnabled.Should().BeTrue();
        policy.Anchor.Should().Be(ReminderAnchor.AfterCreation);
        policy.Offset.Should().Be(TimeSpan.FromHours(1));
        policy.RepeatUntilAcknowledged.Should().BeTrue();
        policy.RepeatEvery.Should().Be(TimeSpan.FromMinutes(15));
        policy.Channels.Should().Be(AlertChannels.All);
    }

    [Fact]
    public void Urgent_IsTenMinutesRepeatingEveryFive()
    {
        ReminderPolicy.Urgent.Offset.Should().Be(TimeSpan.FromMinutes(10));
        ReminderPolicy.Urgent.RepeatEvery.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void None_IsDisabledAndAsksForNoChannel()
    {
        ReminderPolicy.None.IsEnabled.Should().BeFalse();
        ReminderPolicy.None.Channels.Should().Be(AlertChannels.None);
    }

    [Fact]
    public void EnabledWithNoChannels_IsRefused()
    {
        // Lembrete ligado que não avisa por nada nenhum não lembra de nada.
        var build = () => Enabled(channels: AlertChannels.None);

        build.Should().Throw<DomainException>().WithMessage("*forma de aviso*");
    }

    [Fact]
    public void UnknownChannel_IsRefused()
    {
        // Vale para o banco editado à mão: o store degrada para o padrão.
        var build = () => Enabled(channels: (AlertChannels)64);

        build.Should().Throw<DomainException>().WithMessage("*não existe*");
    }

    [Fact]
    public void UnknownAnchor_IsRefused()
    {
        var build = () => Enabled(anchor: (ReminderAnchor)7);

        build.Should().Throw<DomainException>().WithMessage("*não existe*");
    }

    [Fact]
    public void NegativeOffset_IsRefused()
    {
        var build = () => Enabled(offset: TimeSpan.FromMinutes(-1));

        build.Should().Throw<DomainException>().WithMessage("*antes da criação*");
    }

    [Fact]
    public void OffsetBeyondThirtyDays_IsRefused()
    {
        var build = () => Enabled(offset: TimeSpan.FromDays(31));

        build.Should().Throw<DomainException>().WithMessage("*30 dias*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    public void RepeatIntervalBelowOneMinute_IsRefused(int seconds)
    {
        var build = () => Enabled(repeatEvery: TimeSpan.FromSeconds(seconds));

        build.Should().Throw<DomainException>().WithMessage("*pelo menos 1 minuto*");
    }

    [Fact]
    public void RepeatIntervalBeyondADay_IsRefused()
    {
        var build = () => Enabled(repeatEvery: TimeSpan.FromHours(25));

        build.Should().Throw<DomainException>().WithMessage("*24 horas*");
    }

    [Fact]
    public void TurningRepeatOff_ClearsTheInterval()
    {
        // Campo morto guardado torna duas políticas iguais "diferentes", e o
        // teste de round-trip da persistência passa a mentir.
        var policy = Enabled(repeat: false, repeatEvery: TimeSpan.FromMinutes(15));

        policy.RepeatEvery.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ADisabledPolicy_IsCanonicalWhateverElseWasAskedFor()
    {
        var policy = new ReminderPolicy(
            IsEnabled: false,
            ReminderAnchor.BeforeScheduledTime,
            TimeSpan.FromHours(3),
            RepeatUntilAcknowledged: true,
            TimeSpan.FromMinutes(5),
            AlertChannels.Sound);

        policy.Should().Be(ReminderPolicy.None);
    }

    [Fact]
    public void PoliciesWithTheSameValues_AreEqual()
    {
        Enabled().Should().Be(Enabled());
    }

    private static ReminderPolicy Enabled(
        ReminderAnchor anchor = ReminderAnchor.AfterCreation,
        TimeSpan? offset = null,
        bool repeat = true,
        TimeSpan? repeatEvery = null,
        AlertChannels channels = AlertChannels.All) =>
        new(
            IsEnabled: true,
            anchor,
            offset ?? TimeSpan.FromHours(1),
            repeat,
            repeatEvery ?? TimeSpan.FromMinutes(15),
            channels);
}
