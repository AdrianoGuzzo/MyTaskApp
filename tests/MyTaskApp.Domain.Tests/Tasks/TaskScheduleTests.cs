using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Tests.Tasks;

public class TaskScheduleTests
{
    private static readonly DateOnly Date = new(2026, 9, 17);
    private static readonly TimeOnly Time = new(9, 0);

    [Fact]
    public void Unscheduled_HasNeitherDateNorTime()
    {
        TaskSchedule.Unscheduled.Date.Should().BeNull();
        TaskSchedule.Unscheduled.Time.Should().BeNull();
    }

    [Fact]
    public void On_KeepsDateAndLeavesTimeOpen()
    {
        var schedule = TaskSchedule.On(Date);

        schedule.Date.Should().Be(Date);
        schedule.Time.Should().BeNull();
    }

    [Fact]
    public void At_KeepsBothDateAndTime()
    {
        var schedule = TaskSchedule.At(Date, Time);

        schedule.Date.Should().Be(Date);
        schedule.Time.Should().Be(Time);
    }

    [Fact]
    public void TimeWithoutDate_IsRejected()
    {
        // "14:00" sozinho não identifica um instante: não existe tarefa com hora e sem dia.
        var build = () => new TaskSchedule(Date: null, Time: Time);

        build.Should().Throw<DomainException>().WithMessage("*sem data*");
    }

    [Fact]
    public void HasTime_IsFalseForDateOnlySchedule()
    {
        TaskSchedule.On(Date).HasTime.Should().BeFalse();
        TaskSchedule.At(Date, Time).HasTime.Should().BeTrue();
    }

    [Fact]
    public void IsScheduled_IsFalseOnlyWhenThereIsNoDate()
    {
        TaskSchedule.Unscheduled.IsScheduled.Should().BeFalse();
        TaskSchedule.On(Date).IsScheduled.Should().BeTrue();
    }

    [Fact]
    public void SchedulesWithSameValues_AreEqual()
    {
        TaskSchedule.At(Date, Time).Should().Be(TaskSchedule.At(Date, Time));
    }
}
