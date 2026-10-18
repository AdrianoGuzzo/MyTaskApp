using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Reminders;

public class AcknowledgeAndSnoozeHandlerTests
{
    private static readonly DateTimeOffset ThreePm = new(2026, 9, 17, 18, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTaskItemRepository _repository = new();
    private readonly RecordingAlertPresenter _presenter = new();
    private readonly FakeTimeProvider _time = new(ThreePm);

    [Fact]
    public async Task Acknowledging_StopsTheReminderForGood()
    {
        var occurrence = SeedFiredOccurrence();

        await AckHandler().HandleAsync(
            new AcknowledgeReminder(occurrence.Id, ReminderAcknowledgement.Opened), Ct);

        occurrence.Reminder.IsArmed.Should().BeFalse();
        occurrence.Reminder.AcknowledgedBy.Should().Be(ReminderAcknowledgement.Opened);
        occurrence.Reminder.NeedsAttention.Should().BeFalse();
        _repository.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Acknowledging_TakesTheAlertOffTheScreen()
    {
        var occurrence = SeedFiredOccurrence();

        await AckHandler().HandleAsync(
            new AcknowledgeReminder(occurrence.Id, ReminderAcknowledgement.MarkedSeen), Ct);

        _presenter.Dismissed.Should().ContainSingle().Which.Should().Be(occurrence.Id);
    }

    [Fact]
    public async Task AcknowledgingTwice_OnlyTakesTheAlertOffTheScreenAgain()
    {
        // Atendido por outro caminho com o aviso ainda na tela: recusar deixaria
        // a janela presa, porque ela não tem outro jeito de sair.
        var occurrence = SeedFiredOccurrence();
        await AckHandler().HandleAsync(
            new AcknowledgeReminder(occurrence.Id, ReminderAcknowledgement.Opened), Ct);

        await AckHandler().HandleAsync(
            new AcknowledgeReminder(occurrence.Id, ReminderAcknowledgement.MarkedSeen), Ct);

        occurrence.Reminder.AcknowledgedBy.Should().Be(ReminderAcknowledgement.Opened);
        _repository.SaveCount.Should().Be(1);
        _presenter.Dismissed.Should().Equal(occurrence.Id, occurrence.Id);
    }

    [Fact]
    public async Task AcknowledgingAnOccurrenceThatNoLongerExists_OnlyTakesTheAlertOffTheScreen()
    {
        var gone = Guid.NewGuid();

        await AckHandler().HandleAsync(
            new AcknowledgeReminder(gone, ReminderAcknowledgement.Opened), Ct);

        _presenter.Dismissed.Should().ContainSingle().Which.Should().Be(gone);
        _repository.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Snoozing_BringsTheReminderBackLaterWithTheInsistenceReset()
    {
        var occurrence = SeedFiredOccurrence();

        await SnoozeHandler().HandleAsync(
            new SnoozeReminder(occurrence.Id, TimeSpan.FromMinutes(30)), Ct);

        occurrence.Reminder.NextFireAtUtc.Should().Be(ThreePm.AddMinutes(30));
        occurrence.Reminder.Attempt.Should().Be(0);
        occurrence.Reminder.IsAcknowledged.Should().BeFalse();
    }

    [Fact]
    public async Task Snoozing_TakesTheAlertOffTheScreen()
    {
        var occurrence = SeedFiredOccurrence();

        await SnoozeHandler().HandleAsync(
            new SnoozeReminder(occurrence.Id, TimeSpan.FromMinutes(10)), Ct);

        _presenter.Dismissed.Should().ContainSingle().Which.Should().Be(occurrence.Id);
    }

    [Fact]
    public async Task SnoozingAnAcknowledgedReminder_OnlyTakesTheAlertOffTheScreen()
    {
        var occurrence = SeedFiredOccurrence();
        await AckHandler().HandleAsync(
            new AcknowledgeReminder(occurrence.Id, ReminderAcknowledgement.Opened), Ct);

        await SnoozeHandler().HandleAsync(
            new SnoozeReminder(occurrence.Id, TimeSpan.FromMinutes(10)), Ct);

        occurrence.Reminder.IsAcknowledged.Should().BeTrue();
        _repository.SaveCount.Should().Be(1);
        _presenter.Dismissed.Should().Equal(occurrence.Id, occurrence.Id);
    }

    [Fact]
    public async Task SnoozingACompletedOccurrence_OnlyTakesTheAlertOffTheScreen()
    {
        var occurrence = SeedFiredOccurrence();
        var task = await _repository.FindByOccurrenceIdAsync(occurrence.Id, Ct);
        task!.CompleteOccurrence(occurrence.Id, ThreePm);

        await SnoozeHandler().HandleAsync(
            new SnoozeReminder(occurrence.Id, TimeSpan.FromMinutes(10)), Ct);

        _repository.SaveCount.Should().Be(0);
        _presenter.Dismissed.Should().ContainSingle().Which.Should().Be(occurrence.Id);
    }

    [Fact]
    public async Task SnoozingAnOccurrenceThatNoLongerExists_OnlyTakesTheAlertOffTheScreen()
    {
        var gone = Guid.NewGuid();

        await SnoozeHandler().HandleAsync(new SnoozeReminder(gone, TimeSpan.FromMinutes(10)), Ct);

        _presenter.Dismissed.Should().ContainSingle().Which.Should().Be(gone);
        _repository.SaveCount.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public async Task SnoozingForLessThanAMinute_IsRefused(int seconds)
    {
        var occurrence = SeedFiredOccurrence();

        var snooze = async () => await SnoozeHandler().HandleAsync(
            new SnoozeReminder(occurrence.Id, TimeSpan.FromSeconds(seconds)), Ct);

        await snooze.Should().ThrowAsync<DomainException>().WithMessage("*pelo menos 1 minuto*");
    }

    [Fact]
    public async Task SnoozingForMoreThanADay_IsRefused()
    {
        var occurrence = SeedFiredOccurrence();

        var snooze = async () => await SnoozeHandler().HandleAsync(
            new SnoozeReminder(occurrence.Id, TimeSpan.FromHours(25)), Ct);

        await snooze.Should().ThrowAsync<DomainException>().WithMessage("*24 horas*");
    }

    [Fact]
    public async Task ARefusedSnooze_ChangesNothingAndSavesNothing()
    {
        var occurrence = SeedFiredOccurrence();
        var before = occurrence.Reminder.NextFireAtUtc;

        var snooze = async () => await SnoozeHandler().HandleAsync(
            new SnoozeReminder(occurrence.Id, TimeSpan.FromHours(25)), Ct);

        await snooze.Should().ThrowAsync<DomainException>();
        occurrence.Reminder.NextFireAtUtc.Should().Be(before);
        _repository.SaveCount.Should().Be(0);
        _presenter.Dismissed.Should().BeEmpty();
    }

    private TaskOccurrence SeedFiredOccurrence()
    {
        var task = TaskItem.Create(
            "Verificar estoque",
            ThreePm.AddHours(-1),
            schedule: TaskSchedule.On(new DateOnly(2026, 9, 17)),
            reminder: ReminderPolicy.Default);

        var occurrence = task.Occurrences.Single();

        occurrence.ArmReminder(ThreePm);
        occurrence.MarkReminderFired(ThreePm, ThreePm.AddMinutes(15));

        _repository.Seed(task);

        return occurrence;
    }

    private AcknowledgeReminderHandler AckHandler() =>
        new(
            _repository,
            _repository,
            _presenter,
            _time,
            NullLogger<AcknowledgeReminderHandler>.Instance);

    private SnoozeReminderHandler SnoozeHandler() =>
        new(
            _repository,
            _repository,
            _presenter,
            _time,
            NullLogger<SnoozeReminderHandler>.Instance);
}
