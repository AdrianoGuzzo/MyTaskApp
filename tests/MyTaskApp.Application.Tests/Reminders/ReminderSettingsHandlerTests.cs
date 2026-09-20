using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Reminders;

public class ReminderSettingsHandlerTests
{
    private static readonly DateTimeOffset TwoPm = new(2026, 9, 17, 17, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();
    private readonly FakeReminderSettingsStore _settings = new();
    private readonly FakeTimeProvider _time = new(TwoPm);

    [Fact]
    public async Task OnAFreshInstall_TheDefaultsAreTheFactoryOnes()
    {
        var handler = new GetReminderDefaultsHandler(_settings);

        var settings = await handler.HandleAsync(new GetReminderDefaults(), Ct);

        settings.DefaultPolicy.Should().Be(ReminderPolicy.Default);
        settings.PausedUntilUtc.Should().BeNull();
    }

    [Fact]
    public async Task UpdatingTheDefaults_StoresTheNewPolicy()
    {
        await UpdateHandler().HandleAsync(
            new UpdateReminderDefaults(
                IsEnabled: true,
                ReminderAnchor.AfterCreation,
                TimeSpan.FromMinutes(30),
                RepeatUntilAcknowledged: true,
                TimeSpan.FromMinutes(5),
                AlertChannels.Notification),
            Ct);

        var stored = _settings.Settings.DefaultPolicy;

        stored.Offset.Should().Be(TimeSpan.FromMinutes(30));
        stored.RepeatEvery.Should().Be(TimeSpan.FromMinutes(5));
        stored.Channels.Should().Be(AlertChannels.Notification);
    }

    [Fact]
    public async Task UpdatingTheDefaults_WithAnImpossibleInterval_IsRefusedWithAReadableMessage()
    {
        var update = async () => await UpdateHandler().HandleAsync(
            new UpdateReminderDefaults(
                IsEnabled: true,
                ReminderAnchor.AfterCreation,
                TimeSpan.FromHours(1),
                RepeatUntilAcknowledged: true,
                TimeSpan.Zero,
                AlertChannels.All),
            Ct);

        await update.Should().ThrowAsync<DomainException>().WithMessage("*pelo menos 1 minuto*");
    }

    [Fact]
    public async Task UpdatingTheDefaults_WithNoChannel_IsRefused()
    {
        var update = async () => await UpdateHandler().HandleAsync(
            new UpdateReminderDefaults(
                IsEnabled: true,
                ReminderAnchor.AfterCreation,
                TimeSpan.FromHours(1),
                RepeatUntilAcknowledged: true,
                TimeSpan.FromMinutes(15),
                AlertChannels.None),
            Ct);

        await update.Should().ThrowAsync<DomainException>().WithMessage("*forma de aviso*");
    }

    [Fact]
    public async Task UpdatingTheDefaults_DoesNotTouchTasksThatAlreadyExist()
    {
        // Rearmar o banco inteiro porque o usuário mexeu num combo seria
        // genuinamente assustador: o padrão vale na criação.
        var task = SeedArmedTask();
        var armedAt = task.Occurrences.Single().Reminder.NextFireAtUtc;

        await UpdateHandler().HandleAsync(
            new UpdateReminderDefaults(
                IsEnabled: true,
                ReminderAnchor.AfterCreation,
                TimeSpan.FromMinutes(5),
                RepeatUntilAcknowledged: false,
                TimeSpan.Zero,
                AlertChannels.Sound),
            Ct);

        task.Reminder.Should().Be(ReminderPolicy.Default);
        task.Occurrences.Single().Reminder.NextFireAtUtc.Should().Be(armedAt);
    }

    [Fact]
    public async Task UpdatingTheDefaults_KeepsAnActivePause()
    {
        // Mexer no padrão não é o mesmo que pedir para voltar a ser incomodado.
        _settings.Settings = _settings.Settings with { PausedUntilUtc = TwoPm.AddHours(1) };

        await UpdateHandler().HandleAsync(
            new UpdateReminderDefaults(
                IsEnabled: true,
                ReminderAnchor.AfterCreation,
                TimeSpan.FromMinutes(30),
                RepeatUntilAcknowledged: true,
                TimeSpan.FromMinutes(5),
                AlertChannels.All),
            Ct);

        _settings.Settings.PausedUntilUtc.Should().Be(TwoPm.AddHours(1));
    }

    [Fact]
    public async Task Pausing_SilencesEverythingForTheAskedPeriod()
    {
        await PauseHandler().HandleAsync(new PauseReminders(TimeSpan.FromHours(1)), Ct);

        _settings.Settings.PausedUntilUtc.Should().Be(TwoPm.AddHours(1));
        _settings.Settings.IsPausedAt(TwoPm.AddMinutes(30)).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PausingForNoTime_IsRefused(int hours)
    {
        var pause = async () => await PauseHandler()
            .HandleAsync(new PauseReminders(TimeSpan.FromHours(hours)), Ct);

        await pause.Should().ThrowAsync<DomainException>().WithMessage("*quanto tempo*");
    }

    [Fact]
    public async Task PausingForeverIsNotAPause_SoItIsRefused()
    {
        var pause = async () => await PauseHandler()
            .HandleAsync(new PauseReminders(TimeSpan.FromDays(8)), Ct);

        await pause.Should().ThrowAsync<DomainException>().WithMessage("*7 dias*");
    }

    [Fact]
    public async Task Resuming_ClearsThePauseWithoutLosingWhatCameDue()
    {
        _settings.Settings = _settings.Settings with { PausedUntilUtc = TwoPm.AddHours(1) };
        var task = SeedArmedTask();

        await new ResumeRemindersHandler(
            _settings, _repository, NullLogger<ResumeRemindersHandler>.Instance)
            .HandleAsync(new ResumeReminders(), Ct);

        _settings.Settings.PausedUntilUtc.Should().BeNull();
        task.Occurrences.Single().Reminder.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task SettingATaskReminder_RearmsThePendingOccurrence()
    {
        var task = SeedArmedTask();

        await SetHandler().HandleAsync(new SetTaskReminder(task.Id, ReminderPolicy.Urgent), Ct);

        task.Reminder.Should().Be(ReminderPolicy.Urgent);
        task.Occurrences.Single().Reminder.NextFireAtUtc.Should().Be(TwoPm.AddMinutes(10));
    }

    [Fact]
    public async Task SettingATaskReminderToNone_DisarmsIt()
    {
        var task = SeedArmedTask();

        await SetHandler().HandleAsync(new SetTaskReminder(task.Id, ReminderPolicy.None), Ct);

        task.Occurrences.Single().Reminder.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task SettingATaskReminder_LeavesCompletedOccurrencesAlone()
    {
        var task = SeedArmedTask();
        task.Occurrences.Single().Complete(TwoPm);

        await SetHandler().HandleAsync(new SetTaskReminder(task.Id, ReminderPolicy.Urgent), Ct);

        task.Occurrences.Single().Status.Should().Be(TaskItemStatus.Completed);
        task.Occurrences.Single().Reminder.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task SettingTheReminderOfAnUnknownTask_SaysSoInWordsTheUserUnderstands()
    {
        var set = async () => await SetHandler()
            .HandleAsync(new SetTaskReminder(Guid.NewGuid(), ReminderPolicy.Urgent), Ct);

        await set.Should().ThrowAsync<DomainException>().WithMessage("*não encontrada*");
    }

    private TaskItem SeedArmedTask()
    {
        var task = TaskItem.Create(
            "Verificar estoque",
            TwoPm,
            schedule: TaskSchedule.On(new DateOnly(2026, 9, 17)),
            reminder: ReminderPolicy.Default);

        task.Occurrences.Single().ArmReminder(TwoPm.AddHours(1));
        _repository.Seed(task);

        return task;
    }

    private UpdateReminderDefaultsHandler UpdateHandler() =>
        new(_settings, _repository, NullLogger<UpdateReminderDefaultsHandler>.Instance);

    private PauseRemindersHandler PauseHandler() =>
        new(_settings, _repository, _time, NullLogger<PauseRemindersHandler>.Instance);

    private SetTaskReminderHandler SetHandler() =>
        new(
            _repository,
            _repository,
            TestClock.Over(_time),
            _time,
            NullLogger<SetTaskReminderHandler>.Instance);
}
