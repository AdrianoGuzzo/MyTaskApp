using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Application.Tests.Reminders;

public class ReminderDefaultsAtCreationTests
{
    /// <summary>14:00 em São Paulo — o exemplo do próprio pedido.</summary>
    private static readonly DateTimeOffset TwoPm = new(2026, 9, 17, 17, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();
    private readonly FakeReminderSettingsStore _settings = new();
    private readonly FakeTimeProvider _time = new(TwoPm);

    [Fact]
    public async Task Creating_AppliesTheStoredDefaultAndArmsTheFirstFire()
    {
        // 14:00 + 1 hora = 15:00, sem o usuário configurar nada.
        await CreateHandler().HandleAsync(new CreateTask("Verificar estoque"), Ct);

        var occurrence = _repository.Tasks.Single().Occurrences.Single();

        occurrence.Reminder.NextFireAtUtc.Should().Be(TwoPm.AddHours(1));
        occurrence.Reminder.Attempt.Should().Be(0);
    }

    [Fact]
    public async Task Creating_CopiesTheDefaultPolicyOntoTheTask()
    {
        await CreateHandler().HandleAsync(new CreateTask("Verificar estoque"), Ct);

        _repository.Tasks.Single().Reminder.Should().Be(ReminderPolicy.Default);
    }

    [Fact]
    public async Task Creating_WithAnExplicitPolicy_TheTaskOverrideWinsOverTheGlobalDefault()
    {
        var command = new CreateTask("Ligar para o cliente", Reminder: ReminderPolicy.Urgent);

        await CreateHandler().HandleAsync(command, Ct);

        var task = _repository.Tasks.Single();

        task.Reminder.Should().Be(ReminderPolicy.Urgent);
        task.Occurrences.Single().Reminder.NextFireAtUtc.Should().Be(TwoPm.AddMinutes(10));
    }

    [Fact]
    public async Task Creating_WithRemindersOff_ArmsNothing()
    {
        var command = new CreateTask("Comprar pão", Reminder: ReminderPolicy.None);

        await CreateHandler().HandleAsync(command, Ct);

        _repository.Tasks.Single().Occurrences.Single().Reminder.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task Creating_WhenTheUserTurnedDefaultsOff_ArmsNothing()
    {
        _settings.Settings = new ReminderSettings(ReminderPolicy.None, null);

        await CreateHandler().HandleAsync(new CreateTask("Comprar pão"), Ct);

        _repository.Tasks.Single().Occurrences.Single().Reminder.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task Creating_WithAnAnchorOnTheScheduledTime_FiresAtThatTime()
    {
        // "Todos os dias às 09:00, lembrete imediatamente às 09:00."
        _settings.Settings = new ReminderSettings(AtScheduledTime, null);

        var command = new CreateTask(
            "Verificar tarefas do dia",
            ScheduledDate: new DateOnly(2026, 9, 18),
            ScheduledTime: new TimeOnly(9, 0));

        await CreateHandler().HandleAsync(command, Ct);

        var occurrence = _repository.Tasks.Single().Occurrences.Single();

        // 09:00 em São Paulo é 12:00 UTC.
        occurrence.Reminder.NextFireAtUtc
            .Should().Be(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Creating_WithAnAnchorOnTheScheduledTime_ButNoTime_ArmsNothing()
    {
        // Ocorrência sem horário não tem "antes de quê".
        _settings.Settings = new ReminderSettings(AtScheduledTime, null);

        var command = new CreateTask("Comprar pão", ScheduledDate: new DateOnly(2026, 9, 18));

        await CreateHandler().HandleAsync(command, Ct);

        _repository.Tasks.Single().Occurrences.Single().Reminder.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task QuickCapture_AppliesTheDefaultToEveryLine()
    {
        await CaptureHandler().HandleAsync(new QuickCapture("comprar pão\nligar dentista"), Ct);

        _repository.Tasks.Should().HaveCount(2);
        _repository.Tasks.Should().AllSatisfy(task =>
            task.Occurrences.Single().Reminder.NextFireAtUtc.Should().Be(TwoPm.AddHours(1)));
    }

    [Fact]
    public async Task QuickCapture_ReadsTheDefaultOnceForTheWholeBatch()
    {
        var lines = string.Join('\n', Enumerable.Range(1, 20).Select(index => $"tarefa {index}"));

        await CaptureHandler().HandleAsync(new QuickCapture(lines), Ct);

        _settings.Reads.Should().Be(1);
    }

    [Fact]
    public async Task QuickCapture_StillSavesTheWholeBatchInOneGo()
    {
        // ADR-013: armar lembretes não pode furar o tudo-ou-nada da captura.
        await CaptureHandler().HandleAsync(new QuickCapture("a\nb\nc"), Ct);

        _repository.SaveCount.Should().Be(1);
    }

    private static ReminderPolicy AtScheduledTime { get; } = new(
        IsEnabled: true,
        ReminderAnchor.BeforeScheduledTime,
        TimeSpan.Zero,
        RepeatUntilAcknowledged: true,
        TimeSpan.FromMinutes(15),
        AlertChannels.All);

    private CreateTaskHandler CreateHandler() =>
        new(
            _repository,
            _repository,
            _settings,
            new FakeTaskAuditLog(),
            new FakeCurrentUser(),
            TestClock.Over(_time),
            _time,
            NullLogger<CreateTaskHandler>.Instance);

    private QuickCaptureHandler CaptureHandler() =>
        new(
            _repository,
            new FakeTagRepository(),
            _repository,
            _settings,
            new FakeTaskAuditLog(),
            new FakeCurrentUser(),
            TestClock.Over(_time),
            _time,
            NullLogger<QuickCaptureHandler>.Instance);
}
