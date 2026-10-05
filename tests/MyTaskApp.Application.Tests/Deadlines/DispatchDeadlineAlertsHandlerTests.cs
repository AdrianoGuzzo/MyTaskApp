using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Deadlines;

/// <summary>
/// O despacho dos avisos de prazo (ADR-050): um aviso por degrau, a política
/// global viva, a sobrescrita da tarefa, a pausa e a ordem marcar → gravar →
/// apresentar.
/// </summary>
public class DispatchDeadlineAlertsHandlerTests
{
    // Sexta, 09/10/2026, 18:00 em São Paulo = 21:00 UTC.
    private static readonly TaskDeadline Friday = new(new DateOnly(2026, 10, 9), new TimeOnly(18, 0));
    private static readonly DateTimeOffset FridayUtc = new(2026, 10, 9, 21, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(FridayUtc.AddDays(-4));
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeDeadlineSettingsStore _settings = new();
    private readonly FakeReminderSettingsStore _reminders = new();
    private readonly RecordingDeadlineAlertPresenter _presenter = new();
    private readonly RecordingSoundPlayer _sound = new();

    private DispatchDeadlineAlertsHandler Handler() =>
        new(
            new FakeDeadlineAlertQuery(_tasks),
            _tasks,
            _tasks,
            _settings,
            _reminders,
            _presenter,
            _sound,
            TestClock.Over(_time),
            _time,
            NullLogger<DispatchDeadlineAlertsHandler>.Instance);

    private Task<DispatchDeadlineAlertsResult> DispatchAsync() =>
        Handler().HandleAsync(new DispatchDeadlineAlerts(), Ct);

    private TaskItem Seed(string title = "Implementar integração Jira", TaskDeadline? deadline = null, DeadlineAlertStage? alerts = null)
    {
        var task = TaskItem.Create(title, _time.GetUtcNow(), schedule: TaskSchedule.On(new DateOnly(2026, 10, 5)));
        task.ChangeDeadlineAlerts(alerts);
        task.SetOccurrenceDeadline(task.Occurrences[0].Id, deadline ?? Friday, DeadlineAlertStage.None);
        _tasks.Seed(task);

        return task;
    }

    [Fact]
    public async Task FarFromTheDeadline_NothingIsSaid()
    {
        Seed();

        var result = await DispatchAsync();

        result.Should().Be(DispatchDeadlineAlertsResult.Nothing);
        _presenter.Presented.Should().BeEmpty();
        _tasks.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task TheDayBefore_SaysItOnceWithAContextualMessage()
    {
        var task = Seed();
        _time.SetUtcNow(FridayUtc.AddHours(-24));

        var first = await DispatchAsync();
        _time.Advance(TimeSpan.FromSeconds(30));
        var second = await DispatchAsync();

        first.Dispatched.Should().Be(1);
        second.Dispatched.Should().Be(0);

        var alert = _presenter.Presented.Should().ContainSingle().Subject;
        alert.TaskId.Should().Be(task.Id);
        alert.Title.Should().Be("Implementar integração Jira");
        alert.Heading.Should().Be("Prazo amanhã");
        alert.Message.Should().Be("Vence amanhã às 18:00.");
        alert.Stage.Should().Be(DeadlineAlertStage.OneDay);
        alert.IsUrgent.Should().BeFalse();
        _sound.Played.Should().Be(0, "a véspera avisa sem bipe");
    }

    [Fact]
    public async Task TwoHoursBefore_IsUrgentAndPlaysASound()
    {
        Seed();
        _time.SetUtcNow(FridayUtc.AddMinutes(-102));

        await DispatchAsync();

        var alert = _presenter.Presented.Should().ContainSingle().Subject;
        alert.Heading.Should().Be("Prazo urgente");
        alert.Message.Should().Be("Vence em 1h 42min.");
        alert.IsUrgent.Should().BeTrue();
        alert.Severity.Should().Be(DeadlineSeverity.Urgent);
        _sound.Played.Should().Be(1);
    }

    [Fact]
    public async Task AfterTheAppWasClosed_TheMissedStagesBecomeOneAlert()
    {
        // Fechado desde terça; abre faltando uma hora: um aviso, o de 2 horas.
        var task = Seed();
        _time.SetUtcNow(FridayUtc.AddHours(-1));

        await DispatchAsync();

        _presenter.Presented.Should().ContainSingle().Which.Stage.Should().Be(DeadlineAlertStage.TwoHours);
        task.Occurrences[0].DeadlineAlert.LastStage.Should().Be(DeadlineAlertStage.TwoHours);
        task.Occurrences[0].DeadlineAlert.LastAlertAtUtc.Should().Be(FridayUtc.AddHours(-1));
    }

    [Fact]
    public async Task Overdue_SaysSinceWhen_AndRepeatsDaily()
    {
        Seed();
        _time.SetUtcNow(FridayUtc.AddHours(2));

        await DispatchAsync();
        _time.Advance(TimeSpan.FromHours(23));
        await DispatchAsync();
        _time.Advance(TimeSpan.FromHours(1));
        await DispatchAsync();

        _presenter.Presented.Should().HaveCount(2);
        _presenter.Presented[0].Heading.Should().Be("Tarefa atrasada");
        _presenter.Presented[0].Message.Should().Be("Está atrasada há 2 horas.");
        _presenter.Presented[1].Message.Should().Be("Está atrasada há 1 dia.");
    }

    [Fact]
    public async Task ACompletedTask_IsNeverAlerted()
    {
        var task = Seed();
        task.CompleteOccurrence(task.Occurrences[0].Id, _time.GetUtcNow());
        _time.SetUtcNow(FridayUtc.AddHours(5));

        (await DispatchAsync()).Should().Be(DispatchDeadlineAlertsResult.Nothing);
        _presenter.Presented.Should().BeEmpty();
    }

    [Fact]
    public async Task AnArchivedTask_IsNeverAlerted()
    {
        var task = Seed();
        task.Archive(_time.GetUtcNow());
        _time.SetUtcNow(FridayUtc.AddHours(-1));

        await DispatchAsync();

        _presenter.Presented.Should().BeEmpty();
    }

    [Fact]
    public async Task ASilencedTask_IsNeverAlerted()
    {
        Seed(alerts: DeadlineAlertStage.None);
        _time.SetUtcNow(FridayUtc.AddHours(3));

        await DispatchAsync();

        _presenter.Presented.Should().BeEmpty();
    }

    [Fact]
    public async Task TheGlobalPolicy_IsReadLive()
    {
        // Ninguém rearma nada ao mudar a configuração: a tarefa em "Padrão"
        // segue o que a configuração diz agora.
        Seed();
        _settings.Alerts(true, DeadlineAlertStage.ThreeDays | DeadlineAlertStage.Overdue);
        _time.SetUtcNow(FridayUtc.AddDays(-3));

        await DispatchAsync();

        _presenter.Presented.Should().ContainSingle().Which.Stage.Should().Be(DeadlineAlertStage.ThreeDays);
    }

    [Fact]
    public async Task GlobalDisabled_SilencesTasksOnDefault()
    {
        Seed();
        _settings.Alerts(false, DeadlineAlertPolicy.Default.Stages);
        _time.SetUtcNow(FridayUtc.AddHours(-1));

        await DispatchAsync();

        _presenter.Presented.Should().BeEmpty();
    }

    [Fact]
    public async Task ACustomTask_IsAlertedEvenWithTheGlobalDisabled()
    {
        // §20: a tarefa crítica marcada à mão continua avisando.
        Seed(alerts: DeadlineAlertStages.OnlyTheDayBefore);
        _settings.Alerts(false, DeadlineAlertPolicy.Default.Stages);
        _time.SetUtcNow(FridayUtc.AddHours(-20));

        await DispatchAsync();

        _presenter.Presented.Should().ContainSingle().Which.Stage.Should().Be(DeadlineAlertStage.OneDay);
    }

    [Fact]
    public async Task ACustomTask_SkipsTheStagesItTurnedOff()
    {
        Seed(alerts: DeadlineAlertStages.OnlyTheDayBefore);
        _time.SetUtcNow(FridayUtc.AddHours(-24));
        await DispatchAsync();

        _time.SetUtcNow(FridayUtc.AddHours(-1));
        await DispatchAsync();

        _presenter.Presented.Should().ContainSingle("8 h e 2 h estão desligados nesta tarefa");
    }

    [Fact]
    public async Task WhilePaused_NothingIsSaid_AndItComesOnceAfterwards()
    {
        Seed();
        _time.SetUtcNow(FridayUtc.AddHours(-20));
        _reminders.Settings = _reminders.Settings with { PausedUntilUtc = FridayUtc.AddHours(-19) };

        var paused = await DispatchAsync();
        _time.SetUtcNow(FridayUtc.AddHours(-19));
        var resumed = await DispatchAsync();

        paused.Should().Be(DispatchDeadlineAlertsResult.WhilePaused);
        resumed.Dispatched.Should().Be(1);
    }

    [Fact]
    public async Task TheAlertIsSavedBeforeItIsPresented()
    {
        Seed();
        _time.SetUtcNow(FridayUtc.AddHours(-1));
        var savesWhenPresented = -1;
        _presenter.OnPresent = _ => savesWhenPresented = _tasks.SaveCount;

        await DispatchAsync();

        savesWhenPresented.Should().Be(1);
    }

    [Fact]
    public async Task APresenterThatFails_DoesNotUndoTheMark()
    {
        var task = Seed();
        _time.SetUtcNow(FridayUtc.AddHours(-1));
        _presenter.Fail = true;

        var result = await DispatchAsync();

        result.Dispatched.Should().Be(1);
        task.Occurrences[0].DeadlineAlert.LastStage.Should().Be(DeadlineAlertStage.TwoHours);
    }

    [Fact]
    public async Task ManyAtOnce_BecomeOneDigest()
    {
        foreach (var title in (string[])["A", "B", "C", "D"])
        {
            Seed(title);
        }

        _time.SetUtcNow(FridayUtc.AddHours(1));

        await DispatchAsync();

        _presenter.Presented.Should().BeEmpty();
        _presenter.Digests.Should().ContainSingle().Which.Should().Be(new DeadlineDigest(4, IsUrgent: true));
    }

    [Fact]
    public async Task ASnoozedAlert_ComesBackAfterTheSnooze_WithTheDeadlineUntouched()
    {
        var task = Seed();
        _time.SetUtcNow(FridayUtc.AddHours(-8));
        await DispatchAsync();

        await new SnoozeDeadlineAlertHandler(_tasks, _tasks, _presenter, _time, NullLogger<SnoozeDeadlineAlertHandler>.Instance)
            .HandleAsync(new SnoozeDeadlineAlert(task.Occurrences[0].Id, TimeSpan.FromHours(1)), Ct);

        _time.Advance(TimeSpan.FromMinutes(59));
        await DispatchAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        await DispatchAsync();

        _presenter.Presented.Should().HaveCount(2);
        _presenter.Presented[1].Stage.Should().Be(DeadlineAlertStage.EightHours);
        task.Occurrences[0].Deadline.Should().Be(Friday, "adiar o aviso não adia o prazo (§21)");
    }
}
