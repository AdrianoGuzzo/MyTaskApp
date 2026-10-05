using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Deadlines;

public class DeadlineUseCaseTests
{
    // Segunda, 05/10/2026, 09:00 em São Paulo.
    private static readonly DateTimeOffset MondayMorning = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(MondayMorning);
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeDeadlineSettingsStore _settings = new();
    private readonly RecordingDeadlineAlertPresenter _presenter = new();
    private readonly TaskItem _task;

    public DeadlineUseCaseTests()
    {
        _task = TaskItem.Create("Implementar módulo de nutrição", MondayMorning, schedule: TaskSchedule.On(Monday));
        _tasks.Seed(_task);
    }

    private Guid OccurrenceId => _task.Occurrences[0].Id;

    private SetDeadlineHandler SetHandler() =>
        new(_tasks, _tasks, _settings, _presenter, TestClock.Over(_time), _time, NullLogger<SetDeadlineHandler>.Instance);

    [Fact]
    public async Task SettingADeadline_SavesItAndDescribesIt()
    {
        var view = await SetHandler().HandleAsync(new SetDeadline(OccurrenceId, new DateOnly(2026, 10, 9), new TimeOnly(18, 0)), Ct);

        _task.Occurrences[0].Deadline.Should().Be(new TaskDeadline(new DateOnly(2026, 10, 9), new TimeOnly(18, 0)));
        _tasks.SaveCount.Should().Be(1);
        view.Status.Should().Be(DeadlineStatus.OnTrack);
        view.Label.Should().Be("4 dias e 9 horas restantes");
        view.DateLabel.Should().Be("sex 09/10 18:00");
        view.Countdown.Should().Be("4 dias e 9 horas restantes");
        _presenter.Dismissed.Should().Equal(OccurrenceId);
    }

    [Fact]
    public async Task ADeadlineInThePast_IsRefused()
    {
        var set = () => SetHandler().HandleAsync(new SetDeadline(OccurrenceId, Monday, new TimeOnly(8, 0)), Ct);

        await set.Should().ThrowAsync<DomainException>().WithMessage("*futuro*");
        _tasks.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task ADeadlineSoon_StartsPastTheStagesItWasBornIn()
    {
        // Às 09:00, prazo às 12:00: o degrau de 8 h já nasce cruzado, e não vira aviso.
        await SetHandler().HandleAsync(new SetDeadline(OccurrenceId, Monday, new TimeOnly(12, 0)), Ct);

        _task.Occurrences[0].DeadlineAlert.LastStage.Should().Be(DeadlineAlertStage.EightHours);
    }

    [Fact]
    public async Task AShortcut_UsesTheDefaultTime()
    {
        _settings.Settings = _settings.Settings with { DefaultTime = new TimeOnly(17, 0) };

        await SetHandler().HandleAsync(new SetDeadlineShortcut(OccurrenceId, DeadlineShortcut.EndOfWeek), Ct);

        _task.Occurrences[0].Deadline.Should().Be(new TaskDeadline(new DateOnly(2026, 10, 9), new TimeOnly(17, 0)));
    }

    [Fact]
    public async Task AShortcut_KeepsTheTimeAlreadyChosen()
    {
        await SetHandler().HandleAsync(new SetDeadline(OccurrenceId, new DateOnly(2026, 10, 6), new TimeOnly(10, 30)), Ct);

        await SetHandler().HandleAsync(new SetDeadlineShortcut(OccurrenceId, DeadlineShortcut.InOneWeek), Ct);

        _task.Occurrences[0].Deadline.Should().Be(new TaskDeadline(new DateOnly(2026, 10, 12), new TimeOnly(10, 30)));
    }

    [Fact]
    public async Task ClearingTheDeadline_TakesItsAlertOffTheScreen()
    {
        await SetHandler().HandleAsync(new SetDeadlineShortcut(OccurrenceId, DeadlineShortcut.Tomorrow), Ct);

        await new ClearDeadlineHandler(_tasks, _tasks, _presenter, NullLogger<ClearDeadlineHandler>.Instance)
            .HandleAsync(new ClearDeadline(OccurrenceId), Ct);

        _task.Occurrences[0].Deadline.Should().BeNull();
        _presenter.Dismissed.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60 * 25)]
    public async Task SnoozeOutOfRange_IsRefused(int minutes)
    {
        await SetHandler().HandleAsync(new SetDeadlineShortcut(OccurrenceId, DeadlineShortcut.Tomorrow), Ct);
        var handler = new SnoozeDeadlineAlertHandler(_tasks, _tasks, _presenter, _time, NullLogger<SnoozeDeadlineAlertHandler>.Instance);

        var snooze = () => handler.HandleAsync(new SnoozeDeadlineAlert(OccurrenceId, TimeSpan.FromMinutes(minutes)), Ct);

        await snooze.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task TheTaskOverride_IsSaved()
    {
        await new SetTaskDeadlineAlertsHandler(_tasks, _tasks)
            .HandleAsync(new SetTaskDeadlineAlerts(_task.Id, DeadlineAlertStage.None), Ct);

        _task.DeadlineAlerts.Should().Be(DeadlineAlertStage.None);
        _tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task ThePlan_IsSaved()
    {
        await new UpdateTaskPlanHandler(_tasks, _tasks)
            .HandleAsync(new UpdateTaskPlan(_task.Id, "Criar endpoint POST /diets", TimeSpan.FromHours(6)), Ct);

        _task.NextAction.Should().Be("Criar endpoint POST /diets");
        _task.Estimate.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task TheSettings_RoundTripThroughTheHandlers()
    {
        var saved = await new UpdateDeadlineSettingsHandler(_settings, _tasks).HandleAsync(
            new UpdateDeadlineSettings(true, DeadlineAlertStage.ThreeDays | DeadlineAlertStage.Overdue, TimeSpan.FromHours(4), new TimeOnly(17, 0)),
            Ct);

        var read = await new GetDeadlineSettingsHandler(_settings).HandleAsync(new GetDeadlineSettings(), Ct);

        read.Should().Be(saved);
        read.DefaultTime.Should().Be(new TimeOnly(17, 0));
        _tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task InvalidSettings_AreRefusedBeforeSaving()
    {
        var update = () => new UpdateDeadlineSettingsHandler(_settings, _tasks).HandleAsync(
            new UpdateDeadlineSettings(true, DeadlineAlertStage.None, null, new TimeOnly(18, 0)),
            Ct);

        await update.Should().ThrowAsync<DomainException>();
        _settings.Settings.Should().Be(DeadlineSettings.Factory);
        _tasks.SaveCount.Should().Be(0);
    }
}
