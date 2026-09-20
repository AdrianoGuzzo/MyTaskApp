using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Tests.ViewModels;

public class TaskRowAttentionTests
{
    private static readonly DateOnly Date = new(2026, 9, 17);

    [Fact]
    public void ARowThatIsNotWaiting_ShowsNoWarning()
    {
        Row().IsAwaitingAttention.Should().BeFalse();
    }

    [Fact]
    public void ARowThatIsWaiting_ShowsHowLongItHasBeenWaiting()
    {
        // "⚠ Checklist atrasado / Aguardando sua atenção há 35 minutos."
        var row = Row(waiting: TimeSpan.FromMinutes(35));

        row.IsAwaitingAttention.Should().BeTrue();
        row.AttentionLabel.Should().Be("Aguardando sua atenção há 35 minutos.");
    }

    [Fact]
    public void TheWarningGetsLouderOnceTheLadderReachesTheSoundRung()
    {
        Row(waiting: TimeSpan.FromMinutes(5), step: 2).IsUrgentlyAwaiting.Should().BeFalse();
        Row(waiting: TimeSpan.FromMinutes(45), step: 3).IsUrgentlyAwaiting.Should().BeTrue();
    }

    [Fact]
    public void ARowCarriesItsOwnReminderEditor()
    {
        // Um editor por linha: o flyout abre junto com o clique, e um editor
        // compartilhado daria corrida entre carregar e desenhar.
        var first = Row();
        var second = Row();

        first.Editor.Should().NotBeSameAs(second.Editor);
        first.Editor.ToPolicy().Should().Be(ReminderPolicy.Default);
    }

    [Fact]
    public void ARowWithoutATime_CannotAskToBeRemindedBeforeIt()
    {
        Row(time: null).Editor.CanRemindAtScheduledTime.Should().BeFalse();
        Row(time: new TimeOnly(9, 0)).Editor.CanRemindAtScheduledTime.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, "Aguardando sua atenção.")]
    [InlineData(1, "Aguardando sua atenção há 1 minuto.")]
    [InlineData(35, "Aguardando sua atenção há 35 minutos.")]
    [InlineData(120, "Aguardando sua atenção há 2 horas.")]
    [InlineData(60 * 72, "Aguardando sua atenção há 3 dias.")]
    public void TheWaitIsWrittenInWordsAPersonReads(int minutes, string expected)
    {
        TaskRowViewModel.DescribeWait(TimeSpan.FromMinutes(minutes)).Should().Be(expected);
    }

    private static TaskRowViewModel Row(
        TimeSpan? waiting = null,
        int step = 0,
        TimeOnly? time = null,
        ReminderPolicy? policy = null) =>
        new(
            new TodayTask(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "Verificar estoque",
                TaskPriority.Normal,
                Date,
                time,
                IsLate: false,
                waiting,
                step,
                policy ?? ReminderPolicy.Default),
            isCompleted: false);
}
