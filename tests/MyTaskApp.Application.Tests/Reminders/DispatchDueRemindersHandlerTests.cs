using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Reminders;

public class DispatchDueRemindersHandlerTests
{
    /// <summary>15:00 em São Paulo — a hora do primeiro aviso no exemplo do pedido.</summary>
    private static readonly DateTimeOffset ThreePm = new(2026, 9, 17, 18, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();
    private readonly FakeReminderSettingsStore _settings = new();
    private readonly RecordingAlertPresenter _presenter = new();
    private readonly RecordingSoundPlayer _sound = new();
    private readonly FakeTimeProvider _time = new(ThreePm);

    [Fact]
    public async Task AReminderThatCameDue_IsPresentedOnce()
    {
        var occurrence = SeedArmed(ThreePm);

        var result = await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        result.Dispatched.Should().Be(1);
        _presenter.Presented.Should().ContainSingle()
            .Which.OccurrenceId.Should().Be(occurrence.Id);
    }

    [Fact]
    public async Task PresentingDoesNotAcknowledge()
    {
        // O coração da funcionalidade: o aviso saiu, o lembrete continua vivo.
        var occurrence = SeedArmed(ThreePm);

        await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        occurrence.Reminder.IsAcknowledged.Should().BeFalse();
        occurrence.Reminder.NeedsAttention.Should().BeTrue();
        occurrence.Reminder.Attempt.Should().Be(1);
    }

    [Fact]
    public async Task TheFireIsSavedBeforeThePresenterIsCalled()
    {
        // Marcar → salvar → apresentar. Uma queda no meio perde um aviso, que
        // volta; a ordem inversa duplicaria um aviso irreversível.
        var occurrence = SeedArmed(ThreePm);
        var savedWhenPresented = -1;
        var attemptWhenPresented = -1;

        _presenter.OnPresent = _ =>
        {
            savedWhenPresented = _repository.SaveCount;
            attemptWhenPresented = occurrence.Reminder.Attempt;
        };

        await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        savedWhenPresented.Should().Be(1);
        attemptWhenPresented.Should().Be(1);
    }

    [Fact]
    public async Task APresenterThatThrows_KeepsTheMarkAndKeepsGoingWithTheOtherReminders()
    {
        // Desmarcar faria o agendador martelar um apresentador quebrado a cada tique.
        var broken = SeedArmed(ThreePm, "Quebrada");
        var healthy = SeedArmed(ThreePm.AddSeconds(-1), "Saudável");
        _presenter.FailFor = broken.Id;

        await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        broken.Reminder.Attempt.Should().Be(1);
        broken.Reminder.NextFireAtUtc.Should().Be(ThreePm.AddMinutes(15));
        _presenter.Presented.Should().ContainSingle()
            .Which.OccurrenceId.Should().Be(healthy.Id);
    }

    [Fact]
    public async Task WhileRemindersArePaused_NothingIsPresented()
    {
        SeedArmed(ThreePm);
        _settings.Settings = _settings.Settings with { PausedUntilUtc = ThreePm.AddHours(1) };

        var result = await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        result.Paused.Should().BeTrue();
        _presenter.Presented.Should().BeEmpty();
    }

    [Fact]
    public async Task WhatCameDueDuringThePause_IsNotLost()
    {
        var occurrence = SeedArmed(ThreePm);
        _settings.Settings = _settings.Settings with { PausedUntilUtc = ThreePm.AddHours(1) };
        await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        _settings.Settings = _settings.Settings with { PausedUntilUtc = null };
        await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        occurrence.Reminder.Attempt.Should().Be(1);
        _presenter.Presented.Should().ContainSingle();
    }

    [Fact]
    public async Task TheNextFireStaysOnTheOriginalGrid()
    {
        var occurrence = SeedArmed(ThreePm);
        _time.SetUtcNow(ThreePm.AddMinutes(7));

        await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        occurrence.Reminder.NextFireAtUtc.Should().Be(ThreePm.AddMinutes(15));
    }

    [Fact]
    public async Task AnAppClosedForThreeDays_FiresOnceAndComesBackInOneInterval()
    {
        // A regra do ADR-004, sem 288 avisos acumulados.
        var occurrence = SeedArmed(ThreePm);
        var reopenedAt = ThreePm.AddDays(3);
        _time.SetUtcNow(reopenedAt);

        await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        _presenter.Presented.Should().ContainSingle();
        occurrence.Reminder.Attempt.Should().Be(1);
        occurrence.Reminder.NextFireAtUtc.Should().Be(reopenedAt.AddMinutes(15));
    }

    [Fact]
    public async Task TheAlertCarriesHowLongTheChecklistHasBeenWaiting()
    {
        // "Aguardando sua atenção há 35 minutos."
        var occurrence = SeedArmed(ThreePm);
        occurrence.MarkReminderFired(ThreePm, ThreePm.AddMinutes(15));
        _time.SetUtcNow(ThreePm.AddMinutes(35));

        await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        _presenter.Presented.Single().Waiting.Should().Be(TimeSpan.FromMinutes(35));
    }

    [Fact]
    public async Task TheFirstAlert_IsTheQuietestRung()
    {
        SeedArmed(ThreePm);

        await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        var level = _presenter.Presented.Single().Level;

        level.Step.Should().Be(1);
        level.PlaySound.Should().BeFalse();
        level.BringToFront.Should().BeFalse();
        _sound.Played.Should().Be(0);
    }

    [Fact]
    public async Task SoundStartsOnlyAtTheThirdAttempt()
    {
        var occurrence = SeedArmed(ThreePm);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await Handler().HandleAsync(new DispatchDueReminders(), Ct);
            _time.SetUtcNow(occurrence.Reminder.NextFireAtUtc!.Value);
        }

        _presenter.Presented.Select(alert => alert.Level.PlaySound)
            .Should().ContainInOrder(false, false, true);
        _sound.Played.Should().Be(1);
    }

    [Fact]
    public async Task TheFifthAttempt_BringsTheChecklistToTheFront()
    {
        var occurrence = SeedArmed(ThreePm);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await Handler().HandleAsync(new DispatchDueReminders(), Ct);
            _time.SetUtcNow(occurrence.Reminder.NextFireAtUtc!.Value);
        }

        _presenter.Presented[4].Level.BringToFront.Should().BeTrue();
    }

    [Fact]
    public async Task MoreThanFiveDue_CollapseIntoOneDigest()
    {
        // Abrir o app depois de um mês não pode jogar 40 janelas na tela.
        for (var index = 0; index < 6; index++)
        {
            SeedArmed(ThreePm.AddSeconds(-index), $"Tarefa {index}");
        }

        await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        _presenter.Presented.Should().BeEmpty();
        _presenter.Digests.Should().ContainSingle().Which.Count.Should().Be(6);
    }

    [Fact]
    public async Task ExactlyFiveDue_AreStillShownOneByOne()
    {
        for (var index = 0; index < 5; index++)
        {
            SeedArmed(ThreePm.AddSeconds(-index), $"Tarefa {index}");
        }

        await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        _presenter.Presented.Should().HaveCount(5);
        _presenter.Digests.Should().BeEmpty();
    }

    [Fact]
    public async Task WithNothingDue_TheTickCostsNoSave()
    {
        SeedArmed(ThreePm.AddHours(1));

        var result = await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        result.Should().Be(DispatchDueRemindersResult.Nothing);
        _repository.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task AnAcknowledgedReminder_IsNeverDispatchedAgain()
    {
        var occurrence = SeedArmed(ThreePm);
        occurrence.AcknowledgeReminder(ThreePm, ReminderAcknowledgement.Opened);

        await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        _presenter.Presented.Should().BeEmpty();
    }

    [Fact]
    public async Task AReminderThatDoesNotRepeat_FiresOnceAndDisarms()
    {
        var once = new ReminderPolicy(
            IsEnabled: true,
            ReminderAnchor.AfterCreation,
            TimeSpan.FromHours(1),
            RepeatUntilAcknowledged: false,
            TimeSpan.Zero,
            AlertChannels.Notification);

        var occurrence = SeedArmed(ThreePm, policy: once);

        await Handler().HandleAsync(new DispatchDueReminders(), Ct);
        await Handler().HandleAsync(new DispatchDueReminders(), Ct);

        _presenter.Presented.Should().ContainSingle();
        occurrence.Reminder.IsArmed.Should().BeFalse();

        // Continua pedindo atenção mesmo desarmado: o ⚠ na tela não some.
        occurrence.Reminder.NeedsAttention.Should().BeTrue();
    }

    private TaskOccurrence SeedArmed(
        DateTimeOffset fireAt,
        string title = "Verificar estoque",
        ReminderPolicy? policy = null)
    {
        var task = TaskItem.Create(
            title,
            ThreePm.AddHours(-1),
            schedule: TaskSchedule.At(new DateOnly(2026, 9, 17), new TimeOnly(15, 30)),
            reminder: policy ?? ReminderPolicy.Default);

        var occurrence = task.Occurrences.Single();

        occurrence.ArmReminder(fireAt);
        _repository.Seed(task);

        return occurrence;
    }

    private DispatchDueRemindersHandler Handler() =>
        new(
            new FakeDueReminderQuery(_repository),
            _repository,
            _repository,
            _settings,
            _presenter,
            _sound,
            _time,
            NullLogger<DispatchDueRemindersHandler>.Instance);
}
