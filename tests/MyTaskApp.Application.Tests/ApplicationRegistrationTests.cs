using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tags;
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
            .AddSingleton<ITagRepository>(new FakeTagRepository())
            .AddSingleton<ITagQuery>(new StubTagQuery())
            .AddSingleton<IAlertPresenter>(new StubAlertPresenter())
            .AddSingleton<ISoundPlayer>(new StubSoundPlayer())
            .AddSingleton<IGitClient>(new FakeGitClient())
            .AddSingleton<IDirectoryProbe>(new FakeDirectoryProbe())
            .AddSingleton<IAgentSessionRepository>(new FakeAgentSessionRepository())
            .AddSingleton<IAgentProcessTracker>(new FakeAgentProcessTracker())
            .AddSingleton<ITerminalWindowManager>(new FakeTerminalWindowManager())
            .AddSingleton<ITerminalLauncher>(new FakeTerminalLauncher(new FakeAgentProcessTracker(), DateTimeOffset.UnixEpoch))
            .AddSingleton<IAgentCliProvider>(new FakeAgentCliProvider())
            .AddSingleton<IDirectoryRemover>(new FakeDirectoryRemover())
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
    [InlineData(typeof(GetTagsHandler))]
    [InlineData(typeof(CreateTagHandler))]
    [InlineData(typeof(UpdateTagHandler))]
    [InlineData(typeof(DeleteTagHandler))]
    [InlineData(typeof(SetTaskTagsHandler))]
    [InlineData(typeof(GetTagDirectoriesHandler))]
    [InlineData(typeof(GetTaskDirectoriesHandler))]
    [InlineData(typeof(AddTagDirectoryHandler))]
    [InlineData(typeof(UpdateTagDirectoryHandler))]
    [InlineData(typeof(RemoveTagDirectoryHandler))]
    [InlineData(typeof(GetTaskDevelopmentsHandler))]
    [InlineData(typeof(ForgetDevelopmentHandler))]
    [InlineData(typeof(DetectGitHandler))]
    [InlineData(typeof(InspectDirectoryHandler))]
    [InlineData(typeof(ListBranchesHandler))]
    [InlineData(typeof(PrepareDevelopmentHandler))]
    [InlineData(typeof(StartDevelopmentHandler))]
    [InlineData(typeof(InspectWorktreeHandler))]
    [InlineData(typeof(RemoveWorktreeHandler))]
    [InlineData(typeof(DetectAgentCliHandler))]
    [InlineData(typeof(GetTaskAgentSessionHandler))]
    [InlineData(typeof(StartAgentSessionHandler))]
    [InlineData(typeof(FocusAgentSessionHandler))]
    [InlineData(typeof(EndAgentSessionHandler))]
    [InlineData(typeof(ReconcileAgentSessionsHandler))]
    [InlineData(typeof(AgentSessionMonitor))]
    [InlineData(typeof(IAgentSessionWatcher))]
    [InlineData(typeof(IAgentCliProviders))]
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

    /// <summary>
    /// Os casos de uso avisam o mesmo monitor que vigia os processos — dois
    /// objetos seriam dois donos dos vigias (ADR-030).
    /// </summary>
    [Fact]
    public void TheSessionWatcher_IsTheMonitorItself()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IAgentSessionWatcher>()
            .Should().BeSameAs(provider.GetRequiredService<AgentSessionMonitor>());
    }

    [Fact]
    public void UserClock_IsRegisteredAsASingletonSoTheTimeZoneIsResolvedOnce()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IUserClock>()
            .Should().BeSameAs(provider.GetRequiredService<IUserClock>());
    }

    private sealed class StubTagQuery : ITagQuery
    {
        public Task<IReadOnlyList<TagRow>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TagRow>>([]);

        public Task<IReadOnlyList<TagDirectoryRow>> ListDirectoriesAsync(
            Guid tagId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TagDirectoryRow>>([]);

        public Task<IReadOnlyList<TagDirectoryRow>> ListDirectoriesForTaskAsync(
            Guid taskId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TagDirectoryRow>>([]);
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
