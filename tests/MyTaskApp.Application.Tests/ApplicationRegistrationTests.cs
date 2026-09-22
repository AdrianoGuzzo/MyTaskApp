using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tests.Fakes;

namespace MyTaskApp.Application.Tests;

public class ApplicationRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var repository = new FakeTaskItemRepository();

        return new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ITaskItemRepository>(repository)
            .AddSingleton<IUnitOfWork>(repository)
            .AddSingleton<ITodayQuery>(new StubTodayQuery())
            .AddSingleton<IReminderSettingsStore>(new FakeReminderSettingsStore())
            .AddSingleton<IDueReminderQuery>(new StubDueReminderQuery())
            .AddSingleton<ITaskAuditLog>(new FakeTaskAuditLog())
            .AddSingleton<ICurrentUser>(new FakeCurrentUser())
            .AddSingleton<IDataRetentionSettingsStore>(new FakeDataRetentionSettingsStore())
            .AddSingleton<IChecklistArchiveQuery>(new FakeChecklistArchiveQuery())
            .AddSingleton<ILifecycleSweepQuery>(new FakeLifecycleSweepQuery())
            .AddSingleton<IAlertPresenter>(new StubAlertPresenter())
            .AddSingleton<ISoundPlayer>(new StubSoundPlayer())
            // Normalmente vem do composition root do Desktop (ADR-012).
            .AddSingleton<IUseCaseRunner>(new CountingUseCaseRunner())
            .AddApplication()
            .BuildServiceProvider(validateScopes: true);
    }

    [Theory]
    [InlineData(typeof(CreateTaskHandler))]
    [InlineData(typeof(QuickCaptureHandler))]
    [InlineData(typeof(CompleteOccurrenceHandler))]
    [InlineData(typeof(ReopenOccurrenceHandler))]
    [InlineData(typeof(CancelOccurrenceHandler))]
    [InlineData(typeof(UpdateTaskHandler))]
    [InlineData(typeof(RescheduleOccurrenceHandler))]
    [InlineData(typeof(ArchiveChecklistHandler))]
    [InlineData(typeof(RestoreChecklistHandler))]
    [InlineData(typeof(MoveChecklistToTrashHandler))]
    [InlineData(typeof(RestoreChecklistFromTrashHandler))]
    [InlineData(typeof(PurgeChecklistHandler))]
    [InlineData(typeof(GetChecklistArchiveHandler))]
    [InlineData(typeof(GetChecklistAuditHandler))]
    [InlineData(typeof(GetDataRetentionSettingsHandler))]
    [InlineData(typeof(UpdateDataRetentionSettingsHandler))]
    [InlineData(typeof(RunLifecycleMaintenanceHandler))]
    [InlineData(typeof(LifecycleMaintenanceScheduler))]
    [InlineData(typeof(GetTodayBoardHandler))]
    [InlineData(typeof(ReorderOccurrencesHandler))]
    [InlineData(typeof(GetReminderDefaultsHandler))]
    [InlineData(typeof(UpdateReminderDefaultsHandler))]
    [InlineData(typeof(SetTaskReminderHandler))]
    [InlineData(typeof(PauseRemindersHandler))]
    [InlineData(typeof(ResumeRemindersHandler))]
    [InlineData(typeof(AcknowledgeReminderHandler))]
    [InlineData(typeof(SnoozeReminderHandler))]
    [InlineData(typeof(DispatchDueRemindersHandler))]
    [InlineData(typeof(ReminderScheduler))]
    [InlineData(typeof(IUserClock))]
    public void EveryUseCase_CanBeResolved(Type handlerType)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService(handlerType).Should().NotBeNull();
    }

    [Fact]
    public void UserClock_IsRegisteredAsASingletonSoTheTimeZoneIsResolvedOnce()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IUserClock>()
            .Should().BeSameAs(provider.GetRequiredService<IUserClock>());
    }

    private sealed class StubTodayQuery : ITodayQuery
    {
        public Task<IReadOnlyList<TodayOccurrenceRow>> GetCandidatesAsync(
            DateOnly today,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TodayOccurrenceRow>>([]);
    }

    private sealed class StubDueReminderQuery : IDueReminderQuery
    {
        public Task<IReadOnlyList<DueReminderRow>> GetDueAsync(
            DateTimeOffset nowUtc,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DueReminderRow>>([]);
    }

    private sealed class StubAlertPresenter : IAlertPresenter
    {
        public Task PresentAsync(ReminderAlert alert, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PresentDigestAsync(
            ReminderDigest digest,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DismissAsync(Guid occurrenceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubSoundPlayer : ISoundPlayer
    {
        public void PlayAlert()
        {
        }
    }

    [Fact]
    public void TimeProvider_IsAvailableSoNoHandlerFallsBackToDateTimeNow()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }
}
