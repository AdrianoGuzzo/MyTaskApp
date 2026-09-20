using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Domain.Tests.Reminders;

public class ReminderEscalationTests
{
    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(2, false, false, false)]
    [InlineData(3, true, false, false)]
    [InlineData(4, true, true, false)]
    [InlineData(5, true, true, true)]
    public void EachAttempt_ClimbsOneRungOfTheLadder(
        int attempt,
        bool playsSound,
        bool isProminent,
        bool bringsToFront)
    {
        var level = ReminderEscalation.LevelFor(attempt, AlertChannels.All);

        level.Step.Should().Be(attempt);
        level.Notify.Should().BeTrue();
        level.PlaySound.Should().Be(playsSound);
        level.Prominent.Should().Be(isProminent);
        level.BringToFront.Should().Be(bringsToFront);
    }

    [Fact]
    public void TheTwentiethAttempt_StaysOnTheTopRung()
    {
        // A insistência para de crescer; ela não vira outra coisa.
        var level = ReminderEscalation.LevelFor(20, AlertChannels.All);

        level.Should().Be(ReminderEscalation.LevelFor(5, AlertChannels.All));
    }

    [Fact]
    public void SoundStartsOnlyAtTheThirdAttempt()
    {
        ReminderEscalation.LevelFor(2, AlertChannels.All).PlaySound.Should().BeFalse();
        ReminderEscalation.LevelFor(3, AlertChannels.All).PlaySound.Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(99)]
    public void WithTheSoundChannelOff_ItNeverPlaysNoMatterHowLateItIs(int attempt)
    {
        var channels = AlertChannels.Notification | AlertChannels.BringToFront;

        ReminderEscalation.LevelFor(attempt, channels).PlaySound.Should().BeFalse();
    }

    [Fact]
    public void WithTheBringToFrontChannelOff_ItNeverStealsTheScreen()
    {
        var channels = AlertChannels.Notification | AlertChannels.Sound;

        ReminderEscalation.LevelFor(99, channels).BringToFront.Should().BeFalse();
    }

    [Fact]
    public void WithTheNotificationChannelOff_TheProminentRungIsAlsoSilenced()
    {
        // "Notificação mais evidente" continua sendo uma notificação.
        var level = ReminderEscalation.LevelFor(4, AlertChannels.Sound);

        level.Notify.Should().BeFalse();
        level.Prominent.Should().BeFalse();
        level.PlaySound.Should().BeTrue();
    }

    [Fact]
    public void WithEveryChannelOff_TheLevelHasNothingToDo()
    {
        ReminderEscalation.LevelFor(5, AlertChannels.None).IsSilent.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void AnAttemptBelowTheFirst_IsTreatedAsTheFirst(int attempt)
    {
        ReminderEscalation.LevelFor(attempt, AlertChannels.All).Step.Should().Be(1);
    }
}
