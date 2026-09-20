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
    public async Task AcknowledgingTwice_SaysSoInsteadOfPretendingItWorked()
    {
        var occurrence = SeedFiredOccurrence();
        await AckHandler().HandleAsync(
            new AcknowledgeReminder(occurrence.Id, ReminderAcknowledgement.Opened), Ct);

        var again = async () => await AckHandler().HandleAsync(
            new AcknowledgeReminder(occurrence.Id, ReminderAcknowledgement.MarkedSeen), Ct);

        await again.Should().ThrowAsync<DomainException>().WithMessage("*já foi atendido*");
    }

    [Fact]
    public async Task AcknowledgingAnUnknownOccurrence_SaysSoInWordsTheUserUnderstands()
    {
        var acknowledge = async () => await AckHandler().HandleAsync(
            new AcknowledgeReminder(Guid.NewGuid(), ReminderAcknowledgement.Opened), Ct);

        await acknowledge.Should().ThrowAsync<DomainException>()
            .WithMessage("*não encontrada*");
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
    public async Task SnoozingAnAcknowledgedReminder_IsRefused()
    {
        var occurrence = SeedFiredOccurrence();
        await AckHandler().HandleAsync(
            new AcknowledgeReminder(occurrence.Id, ReminderAcknowledgement.Opened), Ct);

        var snooze = async () => await SnoozeHandler().HandleAsync(
            new SnoozeReminder(occurrence.Id, TimeSpan.FromMinutes(10)), Ct);

        await snooze.Should().ThrowAsync<DomainException>().WithMessage("*já foi atendido*");
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
