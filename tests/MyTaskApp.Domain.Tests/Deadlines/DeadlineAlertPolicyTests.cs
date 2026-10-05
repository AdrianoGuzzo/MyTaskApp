using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Domain.Tests.Deadlines;

public class DeadlineAlertPolicyTests
{
    [Fact]
    public void TheDefault_StartsTheDayBefore_AndRepeatsOverdueDaily()
    {
        var policy = DeadlineAlertPolicy.Default;

        policy.IsEnabled.Should().BeTrue();
        policy.Stages.Should().Be(
            DeadlineAlertStage.OneDay | DeadlineAlertStage.EightHours
            | DeadlineAlertStage.TwoHours | DeadlineAlertStage.Overdue);
        policy.OverdueRepeatEvery.Should().Be(TimeSpan.FromDays(1));
    }

    [Fact]
    public void Enabled_WithoutAnyStage_IsRefused()
    {
        var create = () => new DeadlineAlertPolicy(true, DeadlineAlertStage.None, null);

        create.Should().Throw<DomainException>().WithMessage("*pelo menos um*");
    }

    [Fact]
    public void Disabled_KeepsTheChosenStagesForWhenItComesBack()
    {
        var policy = new DeadlineAlertPolicy(false, DeadlineAlertStage.ThreeDays | DeadlineAlertStage.Overdue, TimeSpan.FromHours(4));

        policy.Stages.Should().Be(DeadlineAlertStage.ThreeDays | DeadlineAlertStage.Overdue);
        policy.OverdueRepeatEvery.Should().Be(TimeSpan.FromHours(4));
    }

    [Fact]
    public void AnUnknownStage_IsRefused()
    {
        var create = () => new DeadlineAlertPolicy(true, (DeadlineAlertStage)128, null);

        create.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60 * 24 * 8)]
    public void RepeatOutOfRange_IsRefused(int minutes)
    {
        var create = () => new DeadlineAlertPolicy(true, DeadlineAlertStage.Overdue, TimeSpan.FromMinutes(minutes));

        create.Should().Throw<DomainException>();
    }

    [Fact]
    public void RepeatWithoutTheOverdueStage_IsDropped()
    {
        var policy = new DeadlineAlertPolicy(true, DeadlineAlertStage.OneDay, TimeSpan.FromDays(1));

        policy.OverdueRepeatEvery.Should().BeNull();
        policy.Should().Be(new DeadlineAlertPolicy(true, DeadlineAlertStage.OneDay, null));
    }

    [Fact]
    public void TheTaskOverride_WinsOverTheGlobal()
    {
        var disabled = DeadlineAlertPolicy.Default with { };
        var off = new DeadlineAlertPolicy(false, disabled.Stages, disabled.OverdueRepeatEvery);

        off.StagesFor(null).Should().Be(DeadlineAlertStage.None, "padrão segue a global desligada");
        off.StagesFor(DeadlineAlertStage.TwoHours).Should().Be(DeadlineAlertStage.TwoHours);
        DeadlineAlertPolicy.Default.StagesFor(DeadlineAlertStage.None).Should().Be(DeadlineAlertStage.None);
        DeadlineAlertPolicy.Default.StagesFor(null).Should().Be(DeadlineAlertPolicy.Default.Stages);
    }
}
